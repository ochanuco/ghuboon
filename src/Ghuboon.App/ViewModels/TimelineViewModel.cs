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

    partial void OnSelectedItemChanged(TimelineItemViewModel? value)
    {
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
            var events = await _timelineService.LoadAsync(TimelineFilter.Default, cts.Token).ConfigureAwait(false);

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

        Items.Clear();
        foreach (var item in _allItems)
        {
            if (MatchesFilter(item, filter))
            {
                Items.Add(item);
            }
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
