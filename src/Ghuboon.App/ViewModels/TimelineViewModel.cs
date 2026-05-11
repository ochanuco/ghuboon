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

    partial void OnSelectedItemChanged(TimelineItemViewModel? value)
    {
        // Repaint per-row tints: selected row gets blue, same-thread
        // siblings get soft green, every other row drops both flags.
        // Operate on the master cache so rows that are filtered out of
        // the current Items still get their flags updated for when they
        // re-enter the filtered view.
        //
        // Re-entrance guard: setting IsSelectedRow / IsRelatedToFocus
        // changes RowBackgroundColor → ListBoxItem invalidates → in some
        // Avalonia configurations the ListBox flips its own SelectedItem
        // mid-loop, which would re-fire this handler and recurse. The
        // guard makes the inner repaint a no-op.
        if (_repaintingTints) return;
        _repaintingTints = true;
        try
        {
            var threadId = value?.NotificationId;
            var hasFocus = !string.IsNullOrEmpty(threadId);
            foreach (var item in _allItems)
            {
                var isSelected = ReferenceEquals(item, value);
                item.IsSelectedRow = isSelected;
                item.IsRelatedToFocus = hasFocus
                    && !isSelected
                    && string.Equals(item.NotificationId, threadId, StringComparison.Ordinal);
            }
        }
        finally
        {
            _repaintingTints = false;
        }

        if (value is null) return;
        DetailItem = value;
        // Lazy-load the PR/Issue body for the detail pane. Fire-and-forget;
        // EnsureBodyLoadedAsync swallows non-fatal errors and is idempotent.
        _ = value.EnsureBodyLoadedAsync();
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
            // ConfigureAwait(true) so the continuation runs back on the
            // caller's SynchronizationContext (Avalonia's UI thread in
            // production, none in unit tests). Items.Clear() / Items.Add()
            // below mutate an ObservableCollection bound to the UI, which
            // Avalonia only accepts on the UI thread. Production callers
            // are already expected to invoke ReloadAsync from the UI
            // thread (see comment on the foreach below); this guard
            // protects against an off-thread resumption when the
            // underlying service awaits something that completes on the
            // thread pool.
            var events = await _timelineService.LoadAsync(TimelineFilter.Default, cts.Token).ConfigureAwait(true);

            // Reuse existing VMs by stable Id where possible — the Body /
            // BodyAuthorLogin / Unread state on a kept VM survives a Sync,
            // so the detail pane keeps rendering and the user's read flips
            // don't snap back to "loading" until the next selection.
            var existingById = _allItems.ToDictionary(i => i.Id);
            var newAll = new List<TimelineItemViewModel>(events.Count);
            foreach (var ev in events)
            {
                var id = ev.Id > 0 ? $"evt:{ev.Id}" : ev.NotificationId;
                if (existingById.TryGetValue(id, out var existing))
                {
                    newAll.Add(existing);
                }
                else
                {
                    var item = new TimelineItemViewModel(ev, _itemContextFactory());
                    AttachItem(item);
                    newAll.Add(item);
                }
            }

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

            // Hydrate bookmark flags from the local-state store. A single
            // query covers every item; we then stamp each VM whose
            // NotificationId is in the set. Survives across reloads
            // because the source of truth is the DB.
            if (Bookmarks is not null && !string.IsNullOrEmpty(BookmarkAccountId))
            {
                try
                {
                    var bookmarkedIds = await Bookmarks
                        .GetBookmarkedIdsAsync(BookmarkAccountId!, cts.Token)
                        .ConfigureAwait(true);
                    foreach (var item in _allItems)
                    {
                        item.IsBookmarked = bookmarkedIds.Contains(item.NotificationId);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch
                {
                    // Bookmark hydration is non-fatal: timeline still renders,
                    // the user can re-bookmark.
                }
            }

            ApplyCurrentFilter();

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
    private void ApplyCurrentFilter()
    {
        var filter = Filter;
        var previouslySelectedId = SelectedItem?.Id;

        // First pass: gather every matching item in _allItems order
        // (oldest first, Tween-style).
        var matched = new List<TimelineItemViewModel>(_allItems.Count);
        foreach (var item in _allItems)
        {
            if (MatchesFilter(item, filter))
            {
                matched.Add(item);
            }
        }

        // Dedup duplicate observations of the same logical event:
        //   * Non-Comment kinds (PR / Issue / State / CI / ...) get
        //     keyed by NotificationId — GitHub bumps updated_at on
        //     push / CI / state without changing latest_comment_url,
        //     so each bump appends another event row the EventKind
        //     classifier reads as PR-mode and the user sees N
        //     visually-identical rows. Keep the latest one per thread.
        //   * Comment kind gets keyed by NotificationId +
        //     LatestCommentApiUrl — the same comment can show up as
        //     multiple event rows when its parent notification's
        //     updated_at re-bumps for unrelated activity. Two events
        //     pointing at the same /comments/{id} are the same logical
        //     comment, so collapse them too. Distinct comment URLs on
        //     the same thread (real new comments) survive.
        // We iterate newest → oldest so the latest event in each
        // group wins, then reverse for display.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coalesced = new List<TimelineItemViewModel>(matched.Count);
        for (var i = matched.Count - 1; i >= 0; i--)
        {
            var item = matched[i];
            var key = item.EventKind == NotificationEventKind.Comment
                ? $"C|{item.NotificationId}|{item.LatestCommentApiUrl ?? string.Empty}"
                : $"T|{item.NotificationId}";

            if (string.IsNullOrEmpty(item.NotificationId) || seen.Add(key))
            {
                coalesced.Add(item);
            }
        }
        coalesced.Reverse(); // restore oldest-first display order

        // Group by thread, put the PR / parent row first, comments after.
        // Without this the timeline shows COMMENT(older) → PR(newer) when
        // the very first observation of a thread happened to be a comment
        // notification: the parent PR's "creation" row is later in the
        // event log because its SourceUpdatedAt is the LATER observation
        // time, not the actual PR-created time. Anchor each thread by its
        // earliest event timestamp so cross-thread chronology is mostly
        // preserved; inside a thread, PR-kind always wins.
        var threadAnchor = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var item in coalesced)
        {
            if (string.IsNullOrEmpty(item.NotificationId)) continue;
            if (!threadAnchor.TryGetValue(item.NotificationId, out var existing)
                || item.UpdatedAt < existing)
            {
                threadAnchor[item.NotificationId] = item.UpdatedAt;
            }
        }

        static int KindPriority(NotificationEventKind k) => k switch
        {
            NotificationEventKind.PullRequest => 0,
            NotificationEventKind.Issue => 0,
            NotificationEventKind.Discussion => 0,
            // Parent-entity kinds share priority 0; comments / others fall to 1.
            _ => 1,
        };

        var rearranged = coalesced
            .OrderBy(i => string.IsNullOrEmpty(i.NotificationId)
                ? i.UpdatedAt
                : (threadAnchor.TryGetValue(i.NotificationId, out var anchor) ? anchor : i.UpdatedAt))
            .ThenBy(i => i.NotificationId, StringComparer.Ordinal)
            .ThenBy(i => KindPriority(i.EventKind))
            .ThenBy(i => i.UpdatedAt)
            .ToList();

        Items.Clear();
        foreach (var item in rearranged)
        {
            Items.Add(item);
        }
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

    /// <summary>
    /// Filter predicate: tab + multi-repo + free-text search. Mirrors the
    /// rules in <see cref="DbBackedTimelineService.LoadAsync"/> so callers
    /// can swap between server-side and client-side filtering without a
    /// behavior change. The service version stays around for direct
    /// integration tests; the VM-side version is what drives tab switches.
    /// </summary>
    private static bool MatchesFilter(TimelineItemViewModel item, TimelineFilter filter)
    {
        if (!DbBackedTimelineService.MatchesTab(item.Reason, filter.Tab))
        {
            return false;
        }
        if (filter.Tab == TimelineTab.MyPrs
            && item.EventKind != NotificationEventKind.PullRequest)
        {
            return false;
        }
        if (filter.Tab == TimelineTab.Bookmarks && !item.IsBookmarked)
        {
            return false;
        }
        if (!filter.MatchesAllRepositories)
        {
            if (string.IsNullOrEmpty(item.RepositoryFullName)) return false;
            var allowed = filter.RepositoryFullNames!;
            var anyMatch = false;
            foreach (var name in allowed)
            {
                if (string.Equals(name, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase))
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch) return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var needle = filter.SearchText.Trim();
            if (!Contains(item.RepositoryFullName, needle)
                && !Contains(item.Title, needle)
                && !Contains(item.Reason.ToString(), needle)
                && !Contains(item.SubjectType, needle))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

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
