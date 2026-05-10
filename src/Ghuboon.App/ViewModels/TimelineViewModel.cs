using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isLoading;

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
        // Issue #8: services return domain notifications now, so we map here.
        var ctx = _itemContextFactory();
        foreach (var notification in _timelineService.GetPlaceholderItems())
        {
            var vm = new TimelineItemViewModel(notification, ctx);
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

        _ = ReloadAsync();
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
            var filter = Filter;
            var notifications = await _timelineService.LoadAsync(filter, cts.Token).ConfigureAwait(false);

            // Replace the collection on the same thread the observable model lives on.
            // For unit tests we're already there; in Avalonia, callers should drive this
            // from the UI thread (Dispatcher.UIThread.Post).
            DetachAllItems();
            Items.Clear();
            // Issue #8: services return domain notifications now; map to VMs here
            // (Presentation-layer responsibility) using the per-row context.
            var ctx = _itemContextFactory();
            foreach (var notification in notifications)
            {
                var item = new TimelineItemViewModel(notification, ctx);
                AttachItem(item);
                Items.Add(item);
            }
            RecomputeAggregates();
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
