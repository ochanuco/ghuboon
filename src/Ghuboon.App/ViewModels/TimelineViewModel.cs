using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ghuboon.App.Services;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// ViewModel for the timeline list. Loads from <see cref="ITimelineService"/>,
/// applies the current <see cref="TimelineFilter"/>, and exposes
/// <see cref="UnreadCount"/> as a live aggregate so the host can mirror it to
/// the status bar / menu-bar badge.
/// </summary>
public partial class TimelineViewModel : ViewModelBase
{
    private readonly ITimelineService _timelineService;
    private readonly Func<TimelineItemContext> _itemContextFactory;
    private readonly object _filterLock = new();
    private TimelineFilter _filter = TimelineFilter.Default;
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// Optional bookmark repository. Hosted at the VM level so a single
    /// query per Reload hydrates every TimelineItemViewModel's
    /// <see cref="TimelineItemViewModel.IsBookmarked"/> flag (cheaper than
    /// asking per row). Set by the App composition root; null in tests
    /// keeps the Bookmarks tab empty.
    /// </summary>
    public Ghuboon.Core.Abstractions.IBookmarkRepository? Bookmarks { get; init; }

    /// <summary>
    /// Optional account id whose bookmarks we hydrate. The DbBackedTimelineService
    /// already pulls events for the primary account, and bookmarks live on
    /// the same account axis, so we mirror that scope here.
    /// </summary>
    public string? BookmarkAccountId { get; init; }

    // Master cache: every event row the service returned on the last DB
    // fetch, in display order. Tab / repo / search filter changes operate
    // on this cache in-memory so they no longer round-trip the DB and
    // (more importantly) don't tear down + rebuild every TimelineItemViewModel.
    // Sync / explicit reload refreshes this list; in-VM filtering doesn't.
    private readonly List<TimelineItemViewModel> _allItems = new();

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private TimelineItemViewModel? _selectedItem;

    /// <summary>
    /// The row whose body the detail pane is currently rendering. Tracks
    /// <see cref="SelectedItem"/> on positive transitions but is NOT
    /// cleared when SelectedItem becomes null — the ListBox can drop its
    /// selection during reload / virtualization without the user actually
    /// asking to leave the row, and clearing the detail pane in those
    /// cases is a UX papercut. The user replaces DetailItem by clicking
    /// a different row; reload-driven nulls are ignored.
    /// </summary>
    [ObservableProperty]
    private TimelineItemViewModel? _detailItem;

    private bool _repaintingTints;
    private CancellationTokenSource? _renderDeferCts;
    // Track the previous selected / related-thread state so we can
    // touch only the rows whose IsSelectedRow / IsRelatedToFocus
    // values actually change, instead of scanning all _allItems on
    // every keystroke.
    private TimelineItemViewModel? _prevSelectedRow;
    private string? _prevRelatedThreadId;

    /// <summary>
    /// Optional Serilog-style hook the App can set so the VM can surface
    /// performance-debug data into the same file sink as the rest of the
    /// app. Static so we don't have to thread an ILogger through every
    /// constructor.
    /// </summary>
    public static Action<string>? DiagLog;

    partial void OnSelectedItemChanged(TimelineItemViewModel? value)
    {
        // Diagnostic: end-to-end timing of the synchronous part of the
        // handler. Anything we do here blocks the UI thread.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Re-entrance guard: setting IsSelectedRow / IsRelatedToFocus
        // changes RowBackgroundColor → ListBoxItem invalidates → in some
        // Avalonia configurations the ListBox flips its own SelectedItem
        // mid-loop, which would re-fire this handler and recurse. The
        // guard makes the inner repaint a no-op.
        if (_repaintingTints) return;
        _repaintingTints = true;
        try
        {
            // SELECTED tint: only flip the row whose value actually
            // changes. Touching all 200 _allItems per keystroke was
            // the leftover synchronous cost the user reported as
            // residual "stutter" on TL navigation.
            if (!ReferenceEquals(_prevSelectedRow, value))
            {
                if (_prevSelectedRow is not null) _prevSelectedRow.IsSelectedRow = false;
                if (value is not null) value.IsSelectedRow = true;
                _prevSelectedRow = value;
            }
        }
        finally
        {
            _repaintingTints = false;
        }
        var tintMs = sw.ElapsedMilliseconds;

        if (value is null) return;
        var totalMs = sw.ElapsedMilliseconds;
        value.LogSelectionTiming(totalMs, tintMs);

        // Push DetailItem + related-tint + render-gate + body-load
        // triggers off the synchronous keyboard-event handler. We Post
        // at Background priority so Avalonia's UI thread drains pending
        // Input events (further A/S keystrokes) FIRST and only catches
        // up on the DetailView rebind once the user pauses.
        //
        // Debounce: each new selection cancels the prior Background
        // work item via _renderDeferCts. Holding A/S therefore only
        // commits the FINAL row's DetailItem (and related-thread
        // tints, mark-read, body load) to the pane, not every
        // intermediate one.
        var prev = _renderDeferCts;
        _renderDeferCts = null;
        prev?.Cancel();
        prev?.Dispose();
        var cts = new CancellationTokenSource();
        _renderDeferCts = cts;
        var rowSnapshot = value;
        rowSnapshot.RenderingAllowed = false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested) return;
            if (!ReferenceEquals(_renderDeferCts, cts)) return;

            // Related-thread tints (soft green for same-thread
            // siblings). Defer alongside DetailItem so rapid A/S
            // doesn't pay this cost on every transient row. Touch
            // only the rows whose IsRelatedToFocus would actually
            // change relative to the previous related set.
            //
            // Re-entrance guard: setting IsRelatedToFocus changes
            // RowBackgroundColor → ListBoxItem invalidates → in some
            // Avalonia configurations the ListBox flips its own
            // SelectedItem mid-loop and re-fires OnSelectedItemChanged,
            // which schedules another Background post here and so on
            // — a dispatcher storm that froze the UI on Space → Space
            // navigation (jump to oldest unread, then jump to latest).
            // The synchronous handler's _repaintingTints guard already
            // protects the IsSelectedRow flips; mirror it here so the
            // deferred related-tint loop is similarly protected.
            if (_repaintingTints) return;
            _repaintingTints = true;
            try
            {
                var newThreadId = rowSnapshot.NotificationId;
                if (!string.Equals(_prevRelatedThreadId, newThreadId, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(_prevRelatedThreadId))
                    {
                        foreach (var item in _allItems)
                        {
                            if (item.IsRelatedToFocus
                                && string.Equals(item.NotificationId, _prevRelatedThreadId, StringComparison.Ordinal))
                            {
                                item.IsRelatedToFocus = false;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(newThreadId))
                    {
                        foreach (var item in _allItems)
                        {
                            if (!ReferenceEquals(item, rowSnapshot)
                                && string.Equals(item.NotificationId, newThreadId, StringComparison.Ordinal))
                            {
                                item.IsRelatedToFocus = true;
                            }
                        }
                    }
                    _prevRelatedThreadId = newThreadId;
                }
            }
            finally
            {
                _repaintingTints = false;
            }

            DetailItem = rowSnapshot;
            // Read-on-focus: only fire when the selection has actually
            // settled (we're past the debounce window). Firing
            // synchronously from the View's SelectionChanged event used
            // to pin the UI on rapid A/S navigation — every transient
            // row's MarkAsRead triggered the optimistic Unread flip,
            // RecomputeAggregates (O(N) across 200 rows), and an HTTP
            // MarkThreadRead per intermediate row. Now mark-read only
            // runs on the row the user dwelt on.
            if (rowSnapshot.Unread && rowSnapshot.MarkAsReadCommand.CanExecute(null))
            {
                rowSnapshot.MarkAsReadCommand.Execute(null);
            }
            // Fire the body load AFTER the DetailItem rebind so the
            // detail pane shows its header (title etc.) before we
            // wait on the network. Pass the same cts.Token so a
            // selection that moves on cancels the in-flight body
            // fetch — without that, the previous row's body fetch
            // kept running and could fight for the HttpClient pool /
            // SQLCipher connection while the user was already
            // navigating elsewhere.
            _ = Task.Run(() => rowSnapshot.EnsureBodyLoadedAsync(cts.Token));
        }, Avalonia.Threading.DispatcherPriority.Background);

        // Markdown render gate stays on its own ~200 ms timer rooted
        // in the same CTS so a fresh selection cancels both the
        // DetailItem-rebind Post and the render flip together.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            if (!ReferenceEquals(_renderDeferCts, cts)) return;
            // Avalonia 12's threading model requires INotifyPropertyChanged
            // notifications on UI-bound properties to be raised on the
            // UI thread. Setting RenderingAllowed flips RenderableBlocks
            // which is bound to the DetailView's ItemsControl, so the
            // setter call has to marshal even though the Task.Delay
            // itself runs off-UI.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (!ReferenceEquals(_renderDeferCts, cts)) return;
                rowSnapshot.RenderingAllowed = true;
            });
        });
    }

    public TimelineViewModel()
        : this(new StubTimelineService())
    {
    }

    public TimelineViewModel(ITimelineService timelineService)
        : this(timelineService, ResolveItemContextFactory(timelineService))
    {
    }

    public TimelineViewModel(ITimelineService timelineService, Func<TimelineItemContext> itemContextFactory)
    {
        _timelineService = timelineService ?? throw new ArgumentNullException(nameof(timelineService));
        _itemContextFactory = itemContextFactory ?? (() => TimelineItemContext.Empty);
        Items = new ObservableCollection<TimelineItemViewModel>();
        Items.CollectionChanged += OnItemsChanged;

        // Initial synchronous placeholder snapshot; the real impl returns empty,
        // the stub returns demo rows for the previewer / legacy tests.
        // Event-log timeline: the service now returns NotificationEvent rows
        // (one per observed update); we map each to a TimelineItemViewModel
        // via the event-aware overload.
        // Issue #43: invoke the factory per row so each TimelineItemViewModel
        // gets its own TimelineItemContext; sharing a single instance across
        // rows defeats per-row state (e.g., MarkRead callbacks targeting one
        // row can't be customised without leaking into others).
        foreach (var ev in _timelineService.GetPlaceholderItems())
        {
            var vm = new TimelineItemViewModel(ev, _itemContextFactory());
            AttachItem(vm);
            Items.Add(vm);
        }

        RecomputeAggregates();
    }

    private static Func<TimelineItemContext> ResolveItemContextFactory(ITimelineService service)
    {
        // The DB-backed service exposes a per-row context factory so the
        // composition root can hand commands (Browser/Clipboard/Repository/etc.)
        // to each row. Anything else degrades to TimelineItemContext.Empty.
        if (service is DbBackedTimelineService db)
        {
            return db.ItemContextFactory;
        }
        return () => TimelineItemContext.Empty;
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; }

    /// <summary>
    /// String alias used by older code paths (Phase 1) that bound to a tab name.
    /// Setting this maps to the appropriate <see cref="TimelineTab"/> on the
    /// underlying <see cref="Filter"/>.
    /// </summary>
    public string CurrentFilter
    {
        get => _filter.Tab.ToString() switch
        {
            nameof(TimelineTab.All) => "All",
            nameof(TimelineTab.Review) => "Review",
            nameof(TimelineTab.Mention) => "Mention",
            nameof(TimelineTab.MyPrs) => "My PRs",
            nameof(TimelineTab.Watching) => "Watching",
            _ => "All",
        };
        set
        {
            var tab = ParseTab(value);
            ApplyFilter(_filter with { Tab = tab });
        }
    }

    /// <summary>The active filter snapshot.</summary>
    public TimelineFilter Filter
    {
        get
        {
            lock (_filterLock)
            {
                return _filter;
            }
        }
    }

    /// <summary>
    /// Replaces the active filter and triggers a reload. Pass
    /// <see cref="TimelineFilter.Default"/> to clear all filters.
    /// </summary>
    public void ApplyFilter(TimelineFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_filterLock)
        {
            _filter = filter;
        }

        // Filter changes (tab / repo / search) operate on the in-memory
        // master cache without re-hitting the DB or rebuilding VMs. Sync
        // / explicit Reload is what refreshes the master.
        if (_allItems.Count == 0)
        {
            _ = ReloadAsync();
        }
        else
        {
            ApplyCurrentFilter();
        }
        OnPropertyChanged(nameof(CurrentFilter));
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>
    /// Async load entry point. Modern callers should await this directly;
    /// it replaces the legacy synchronous <see cref="Load"/> method.
    /// </summary>
    public Task LoadAsync(CancellationToken ct = default) => ReloadAsync(ct);

    /// <summary>
    /// Synchronous load entry point preserved for back-compat with Phase 1 tests.
    /// Wrapped in <see cref="Task.Run(Func{Task})"/> to escape the calling
    /// SynchronizationContext (e.g. Avalonia's UI sync context) so the inner
    /// awaits cannot deadlock when posting their continuations back.
    /// New code should call <see cref="LoadAsync"/> instead.
    /// </summary>
    [Obsolete("Use LoadAsync. Kept for Phase 1 callers only.")]
    public void Load()
    {
        // Run on the thread-pool to avoid any UI-thread sync-context capture
        // that would deadlock on .GetAwaiter().GetResult().
        Task.Run(() => ReloadAsync()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously reloads the timeline using the current filter. Subsequent
    /// calls cancel any in-flight reload.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var previous = _loadCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = cts;
        if (previous is not null)
        {
            try { previous.Cancel(); } catch { /* best-effort */ }
            previous.Dispose();
        }

        IsLoading = true;
        try
        {
            // Master cache fetch: ALWAYS pull with the default (unfiltered)
            // request so the VM holds every row the service can offer.
            // Filtering then happens in ApplyCurrentFilter() against this
            // cache without hitting the DB on each tab / repo change.
            //
            // Run the DB load and VM construction OFF the UI thread —
            // user reported a multi-second freeze on app launch where
            // the window appeared but TL stayed empty with no input
            // response. The culprit was the initial-load chain
            // (ConfigureAwait(true)) doing 200 VM constructors and
            // 200 IsBookmarked property writes synchronously on the UI
            // dispatcher. We now build the new VM list + look up the
            // bookmark set on the thread pool, then marshal back to UI
            // only for the small cluster of operations that touch
            // bound state (AttachItem subscription, _allItems mutation,
            // ApplyCurrentFilter).
            var events = await _timelineService.LoadAsync(TimelineFilter.Default, cts.Token).ConfigureAwait(false);

            // Reuse existing VMs by stable Id where possible — the Body /
            // BodyAuthorLogin / Unread state on a kept VM survives a Sync,
            // so the detail pane keeps rendering and the user's read flips
            // don't snap back to "loading" until the next selection.
            var existingById = _allItems.ToDictionary(i => i.Id);
            var newAll = new List<TimelineItemViewModel>(events.Count);
            var freshItems = new List<TimelineItemViewModel>();
            foreach (var ev in events)
            {
                var id = ev.Id > 0 ? $"evt:{ev.Id}" : ev.NotificationId;
                if (existingById.TryGetValue(id, out var existing))
                {
                    newAll.Add(existing);
                }
                else
                {
                    // Construct OFF-UI. No listeners attached yet, so the
                    // ObservableProperty setters fire PropertyChanged into
                    // a void — cheap. AttachItem is deferred to the UI
                    // marshal below where it's safe to wire up the
                    // listener chain.
                    var item = new TimelineItemViewModel(ev, _itemContextFactory());
                    newAll.Add(item);
                    freshItems.Add(item);
                }
            }

            // Bookmark hydration off-UI as well. Setting IsBookmarked on a
            // not-yet-attached VM is free (PropertyChanged has no
            // subscribers), so this avoids the 200 OnItemPropertyChanged →
            // InvalidateTabCache calls we'd otherwise rack up on the UI
            // thread.
            IReadOnlySet<string>? bookmarkedIds = null;
            if (Bookmarks is not null && !string.IsNullOrEmpty(BookmarkAccountId))
            {
                try
                {
                    bookmarkedIds = await Bookmarks
                        .GetBookmarkedIdsAsync(BookmarkAccountId!, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch
                {
                    // Bookmark hydration is non-fatal: timeline still renders,
                    // the user can re-bookmark.
                }
            }
            // No-op fast path: if the sync brought back the exact same
            // VM set in the same order AND the bookmark hydration would
            // not flip any IsBookmarked, skip the UI batch entirely.
            // Most poll cycles (60 s on a quiet account) re-fetch the
            // same set, and the ApplyCurrentFilter → ReplaceItems →
            // ObservableCollection diff → ListBox layout pass was
            // visible as a "プチフリ" each minute.
            var noChanges = freshItems.Count == 0
                && newAll.Count == _allItems.Count;
            if (noChanges)
            {
                for (var i = 0; i < newAll.Count; i++)
                {
                    if (!ReferenceEquals(newAll[i], _allItems[i]))
                    {
                        noChanges = false;
                        break;
                    }
                }
            }
            if (noChanges && bookmarkedIds is not null)
            {
                foreach (var item in newAll)
                {
                    if (item.IsBookmarked != bookmarkedIds.Contains(item.NotificationId))
                    {
                        noChanges = false;
                        break;
                    }
                }
            }
            if (noChanges)
            {
                // Still kick the ActorLogin backfill: the prior cts was
                // cancelled at method entry, so without restarting it
                // here, rows with missing actor on a quiet account
                // never resolve on no-op ticks (they'd only catch up
                // when an actual data change finally fires the UI
                // batch). CodeRabbit feedback on PR #72.
                _ = Task.Run(() => BackfillActorLoginsAsync(newAll.ToArray(), cts.Token), cts.Token);
                return;
            }

            // Marshal back to UI for the bits that touch bound collections
            // / fire PropertyChanged into now-live subscribers. In unit
            // tests (no Avalonia Application bootstrapped), the
            // dispatcher InvokeAsync deadlocks the test runner — fall
            // back to inline execution on the current thread there.
            //
            // Bookmark hydration moved inside the UI batch so the
            // IsBookmarked setter (which fires OnItemPropertyChanged
            // → InvalidateTabCache → maybe ApplyFilter) doesn't race
            // the binding system from off-UI. Fresh items have no
            // listeners yet, reused items do — handling both inside
            // the same UI batch keeps the threading model simple.
            Action uiBatch = () =>
            {
                if (cts.IsCancellationRequested) return;

                var batchSw = System.Diagnostics.Stopwatch.StartNew();
                long attachMs, detachMs, hydrateMs, applyMs;

                // Wire up freshly constructed VMs.
                foreach (var item in freshItems)
                {
                    AttachItem(item);
                }
                attachMs = batchSw.ElapsedMilliseconds;

                // Detach VMs that fell out of the master (retention prune /
                // upstream deletion) so their PropertyChanged stops feeding
                // RecomputeAggregates.
                var newIds = new HashSet<string>(newAll.Select(i => i.Id), StringComparer.Ordinal);
                foreach (var stale in _allItems)
                {
                    if (!newIds.Contains(stale.Id))
                    {
                        stale.PropertyChanged -= OnItemPropertyChanged;
                    }
                }

                _allItems.Clear();
                _allItems.AddRange(newAll);
                detachMs = batchSw.ElapsedMilliseconds - attachMs;

                if (bookmarkedIds is not null)
                {
                    foreach (var item in newAll)
                    {
                        var shouldBeBookmarked = bookmarkedIds.Contains(item.NotificationId);
                        if (item.IsBookmarked != shouldBeBookmarked)
                        {
                            item.IsBookmarked = shouldBeBookmarked;
                        }
                    }
                }
                hydrateMs = batchSw.ElapsedMilliseconds - attachMs - detachMs;

                InvalidateTabCache();

                ApplyCurrentFilter();
                applyMs = batchSw.ElapsedMilliseconds - attachMs - detachMs - hydrateMs;

                if (batchSw.ElapsedMilliseconds > 100)
                {
                    DiagLog?.Invoke(
                        $"reload.uiBatch slow: total={batchSw.ElapsedMilliseconds}ms attach={attachMs} detach={detachMs} hydrate={hydrateMs} apply={applyMs} items={newAll.Count} fresh={freshItems.Count}");
                }
            };
            if (Avalonia.Application.Current is null)
            {
                uiBatch();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(uiBatch);
            }

            // Backfill ActorLogin for rows that don't have one persisted yet.
            // Fires EnsureBodyLoadedAsync sequentially in the background so the
            // User column resolves to the real commenter (e.g. @coderabbitai)
            // without the user having to click each row first. Limited to one
            // concurrent fetch to avoid hammering the GitHub API; ~50 rows
            // settles in under a minute and is well within rate limit.
            _ = Task.Run(() => BackfillActorLoginsAsync(_allItems.ToArray(), cts.Token), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded; swallow.
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-apply the current <see cref="Filter"/> to the master cache and
    /// refresh <see cref="Items"/>. Tab / repo / search changes go through
    /// here without hitting the DB or rebuilding any VM, so the UI updates
    /// in O(N) over a 200-row cache instead of paying a SQL round-trip
    /// plus N VM constructions per change.
    /// </summary>
    // Per-tab cache of the sorted display list. Invalidated whenever the
    // master cache (_allItems) or any non-tab filter axis (repo / search /
    // bookmarks) changes. Lets a rapid A/S tab cycle skip the filter +
    // dedup + sort work and jump straight to refilling Items.
    private readonly Dictionary<TimelineTab, List<TimelineItemViewModel>> _tabListCache = new();
    private string? _cachedFilterSignature;

    private static string FilterSignature(TimelineFilter f)
    {
        var repos = f.MatchesAllRepositories
            ? "*"
            : string.Join(",", f.RepositoryFullNames!.OrderBy(r => r, StringComparer.Ordinal));
        return $"r={repos};s={f.SearchText ?? string.Empty}";
    }

    private void InvalidateTabCache()
    {
        _tabListCache.Clear();
        _cachedFilterSignature = null;
    }

    private void ApplyCurrentFilter()
    {
        var filter = Filter;
        var previouslySelectedId = SelectedItem?.Id;

        // Non-tab axes (repo / search) invalidate every tab cache; reusing
        // a stale entry would surface rows that the new repo/search would
        // have filtered out.
        var sig = FilterSignature(filter);
        if (!string.Equals(_cachedFilterSignature, sig, StringComparison.Ordinal))
        {
            _tabListCache.Clear();
            _cachedFilterSignature = sig;
        }

        if (_tabListCache.TryGetValue(filter.Tab, out var cached))
        {
            ReplaceItems(cached, previouslySelectedId);
            return;
        }

        var rearranged = TimelineProjection.Build(_allItems, filter);
        _tabListCache[filter.Tab] = rearranged;
        ReplaceItems(rearranged, previouslySelectedId);
    }

    private void ReplaceItems(IReadOnlyList<TimelineItemViewModel> rearranged, string? previouslySelectedId)
    {
        ObservableCollectionReconciler.Apply(Items, rearranged);
        RecomputeAggregates();

        // Re-select: prefer the row the user was on; otherwise pick the
        // newest (last in Tween order) so the detail pane shows the
        // freshest content for the now-active filter.
        if (Items.Count > 0)
        {
            TimelineItemViewModel? restore = null;
            if (!string.IsNullOrEmpty(previouslySelectedId))
            {
                restore = Items.FirstOrDefault(i => i.Id == previouslySelectedId);
            }
            SelectedItem = restore ?? Items[Items.Count - 1];
        }
    }

    private static async Task BackfillActorLoginsAsync(TimelineItemViewModel[] snapshot, CancellationToken ct)
    {
        foreach (var item in snapshot)
        {
            if (ct.IsCancellationRequested) return;
            // Skip rows that already have a real (non-bot) actor — those
            // came from sync-time resolve and represent the PR/Issue
            // creator. Bot-suffix logins ("[bot]") are likely stale
            // commenter values from before sync-time actor resolution
            // landed; re-fetch to get the real subject author.
            if (!string.IsNullOrEmpty(item.ActorLogin)
                && !item.ActorLogin.EndsWith("[bot]", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                await item.EnsureBodyLoadedAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Per-item failures are non-fatal — keep going so other rows
                // still resolve. EnsureBodyLoadedAsync swallows recoverable
                // errors itself; this catch handles the unlikely re-throws.
            }
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RecomputeAggregates();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineItemViewModel.Unread))
        {
            RecomputeAggregates();
        }
        else if (e.PropertyName == nameof(TimelineItemViewModel.IsBookmarked))
        {
            // A bookmark flip changes which items belong in the Bookmarks
            // tab's cached list, so clear ALL tab caches (other tabs
            // include / exclude the row identically before/after).
            InvalidateTabCache();

            // When the user is currently on the Bookmarks tab, just
            // clearing the cache isn't enough — the visible Items list
            // still holds the unbookmarked row until something forces
            // a re-filter. Re-run the filter now so the row drops out
            // immediately. For other tabs the cached row count is
            // unchanged (only the bookmarked_at flag flipped), so
            // skipping the re-filter is correct.
            if (_filter.Tab == TimelineTab.Bookmarks)
            {
                ApplyFilter(_filter);
            }
        }
    }

    private void AttachItem(TimelineItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void DetachAllItems()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void RecomputeAggregates()
    {
        UnreadCount = UnreadCounter.CountUnread(Items);
        IsEmpty = Items.Count == 0;
    }

    private static TimelineTab ParseTab(string? value) => value switch
    {
        "All" or null or "" => TimelineTab.All,
        "Review" => TimelineTab.Review,
        "Mention" => TimelineTab.Mention,
        "My PRs" => TimelineTab.MyPrs,
        "MyPrs" => TimelineTab.MyPrs,
        "Watching" => TimelineTab.Watching,
        _ => TimelineTab.All,
    };
}
