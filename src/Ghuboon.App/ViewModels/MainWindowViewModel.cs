using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghuboon.App.Services;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// Main window VM. Composes the timeline + settings sub-VMs, drives the status
/// bar (last sync, rate-limit, error message), and bridges
/// <see cref="INotificationSyncService.Progress"/> events from the sync pipeline
/// into observable UI properties.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    public const string TabAll = "All";
    public const string TabBookmarks = "Bookmarks";
    public const string TabReview = "Review";
    public const string TabMention = "Mention";
    public const string TabMyPrs = "My PRs";
    public const string TabWatching = "Watching";

    private readonly IAppSettingsService _appSettings;
    private readonly INotificationSyncService? _syncService;
    private readonly IClock? _clock;
    private readonly Func<Task<string?>>? _accountIdProvider;
    private readonly EventHandler<SyncProgressEvent>? _progressHandler;

    /// <summary>
    /// Optional UI-thread marshaller. The App layer assigns
    /// <see cref="Avalonia.Threading.Dispatcher.UIThread"/>'s post when running for
    /// real; tests leave it null and run handlers synchronously.
    /// </summary>
    public Action<Action>? UiDispatcher { get; set; }

    [ObservableProperty]
    private string _selectedTab = TabAll;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsToggleLabel))]
    private bool _isSettingsVisible;

    /// <summary>
    /// Multi-select repository filter items. Each entry's <c>IsSelected</c>
    /// drives the timeline filter — none selected = "All repos". The list
    /// is rebuilt on <see cref="RefreshRepositoriesAsync"/>, preserving the
    /// previously-checked names so the user's selection survives a sync.
    /// </summary>
    public ObservableCollection<RepositoryFilterItem> RepositoryFilters { get; } = new();

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Transient acknowledgement shown at the bottom of the window
    /// ("Bookmarked" / "Copied" / etc.). Set via <see cref="ShowFlash"/>,
    /// auto-clears after a short delay so it doesn't get stale.
    /// </summary>
    [ObservableProperty]
    private string? _flashText;

    private CancellationTokenSource? _flashCts;

    /// <summary>
    /// Surface a short acknowledgement in the bottom status bar. Each
    /// call resets the timeout, so a rapid sequence of clicks shows the
    /// latest message rather than the first.
    /// </summary>
    public void ShowFlash(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        FlashText = message;

        // Replace any in-flight clear so the latest message lives for its
        // full window rather than getting cut short by a previous timer.
        var previous = _flashCts;
        var cts = new CancellationTokenSource();
        _flashCts = cts;
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            void Clear() { if (ReferenceEquals(_flashCts, cts)) FlashText = null; }
            if (UiDispatcher is { } d) d(Clear); else Clear();
        });
    }

    [ObservableProperty]
    private string? _lastSyncText;

    [ObservableProperty]
    private string? _rateLimitText;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private DateTimeOffset? _lastSuccessfulSyncAt;

    public MainWindowViewModel()
        : this(new StubAppSettingsService(), new StubTimelineService())
    {
    }

    public MainWindowViewModel(IAppSettingsService appSettings, ITimelineService timelineService)
        : this(appSettings, timelineService, syncService: null, clock: null, accountIdProvider: null)
    {
    }

    public MainWindowViewModel(
        IAppSettingsService appSettings,
        ITimelineService timelineService,
        INotificationSyncService? syncService,
        IClock? clock,
        Func<Task<string?>>? accountIdProvider,
        SettingsViewModel? settingsViewModel = null,
        Ghuboon.Core.Abstractions.IBookmarkRepository? bookmarks = null,
        string? bookmarkAccountId = null)
    {
        _appSettings = appSettings;
        _syncService = syncService;
        _clock = clock;
        _accountIdProvider = accountIdProvider;

        Tabs = new[] { TabAll, TabBookmarks, TabReview, TabMention, TabMyPrs, TabWatching };
        Timeline = new TimelineViewModel(timelineService)
        {
            Bookmarks = bookmarks,
            BookmarkAccountId = bookmarkAccountId,
        };
        Settings = settingsViewModel ?? new SettingsViewModel(appSettings);
        Repositories = new List<RepositoryRef>();

        Timeline.PropertyChanged += OnTimelinePropertyChanged;

        if (_syncService is not null)
        {
            _progressHandler = (s, e) =>
            {
                if (UiDispatcher is { } d)
                {
                    d(() => OnSyncProgress(e));
                }
                else
                {
                    OnSyncProgress(e);
                }
            };
            _syncService.Progress += _progressHandler;
        }
    }

    public IReadOnlyList<string> Tabs { get; }

    public TimelineViewModel Timeline { get; }

    public SettingsViewModel Settings { get; }

    public string Title => "Ghuboon";

    public int UnreadCount => Timeline.UnreadCount;

    public IReadOnlyList<RepositoryRef> Repositories { get; private set; }

    /// <summary>
    /// Optional service that lists repos for the dropdown. When null the dropdown
    /// is empty (used by stubs/tests).
    /// </summary>
    public ITimelineService? RepositoriesSource { get; init; }

    public event EventHandler? UnreadCountChanged;

    public async Task RefreshRepositoriesAsync(CancellationToken ct = default)
    {
        if (RepositoriesSource is null)
        {
            return;
        }

        Repositories = await RepositoriesSource.ListRepositoriesAsync(ct).ConfigureAwait(false);
        OnPropertyChanged(nameof(Repositories));

        // Rebuild the multi-select dropdown items, preserving prior ticks
        // by full name. Detach handlers from old items before swapping so
        // a removed repo can't keep firing filter events.
        var previouslySelected = new HashSet<string>(
            RepositoryFilters.Where(f => f.IsSelected).Select(f => f.FullName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var existing in RepositoryFilters)
        {
            existing.PropertyChanged -= OnRepositoryFilterChanged;
        }
        RepositoryFilters.Clear();

        foreach (var repo in Repositories)
        {
            if (string.IsNullOrEmpty(repo.FullName))
            {
                continue;
            }

            var item = new RepositoryFilterItem(repo.FullName)
            {
                IsSelected = previouslySelected.Contains(repo.FullName),
            };
            item.PropertyChanged += OnRepositoryFilterChanged;
            RepositoryFilters.Add(item);
        }

        OnPropertyChanged(nameof(RepositoryFilterLabel));

        // Re-apply the filter against the freshly-rebuilt selection set so
        // the timeline stays in sync with the dropdown's label. A repo
        // that was previously ticked but no longer appears in the new
        // Repositories list (pruned upstream, account swap) used to leave
        // a stale name in the active filter while the dropdown read
        // "All repos" — the visible UI lied about what was being filtered.
        ApplyRepositoryFilter();
    }

    private void OnRepositoryFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepositoryFilterItem.IsSelected))
        {
            return;
        }
        ApplyRepositoryFilter();
        OnPropertyChanged(nameof(RepositoryFilterLabel));
    }

    private void ApplyRepositoryFilter()
    {
        var selected = RepositoryFilters
            .Where(f => f.IsSelected)
            .Select(f => f.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<string>? next = selected.Count == 0 ? null : selected;
        Timeline.ApplyFilter(Timeline.Filter with { RepositoryFullNames = next });
    }

    /// <summary>
    /// Button label for the multi-select dropdown:
    ///   * 0 selected → "All repos"
    ///   * 1 selected → "<owner/name>"
    ///   * N selected → "<N> repos"
    /// </summary>
    public string RepositoryFilterLabel
    {
        get
        {
            var count = RepositoryFilters.Count(f => f.IsSelected);
            return count switch
            {
                0 => "All repos",
                1 => RepositoryFilters.First(f => f.IsSelected).FullName,
                _ => $"{count} repos",
            };
        }
    }

    /// <summary>
    /// Clears every selected repository, returning the filter to "All repos".
    /// Wired to the popup's "Clear" button.
    /// </summary>
    [RelayCommand]
    private void ClearRepositoryFilter()
    {
        var anyCleared = false;
        foreach (var item in RepositoryFilters)
        {
            if (item.IsSelected)
            {
                item.IsSelected = false;
                anyCleared = true;
            }
        }
        if (!anyCleared)
        {
            // Fire the apply path anyway so the timeline matches the label
            // (defensive — a stale filter on the timeline VM with no ticked
            // boxes should round-trip to All repos).
            ApplyRepositoryFilter();
            OnPropertyChanged(nameof(RepositoryFilterLabel));
        }
    }

    public async Task InitialLoadAsync(CancellationToken ct = default)
    {
        // Phase 14: paint cached items first regardless of sync.
        await Timeline.ReloadAsync(ct).ConfigureAwait(false);

        // Sync only if a primary account exists.
        if (_syncService is not null)
        {
            string? accountId = null;
            if (_accountIdProvider is not null)
            {
                accountId = await _accountIdProvider().ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(accountId))
            {
                // Fire-and-forget on purpose (we don't want InitialLoadAsync to
                // block on the network), but observe faults so they don't get
                // swallowed by the unawaited Task.
                _ = TriggerSyncAsync(accountId!).ContinueWith(
                    t => System.Diagnostics.Debug.WriteLine(
                        $"InitialLoad: background sync faulted: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            else
            {
                StatusText = "Configure PAT in Settings to sync.";
            }

            _syncService.Start();
        }
    }

    partial void OnSelectedTabChanged(string value)
    {
        var tab = value switch
        {
            TabBookmarks => TimelineTab.Bookmarks,
            TabReview => TimelineTab.Review,
            TabMention => TimelineTab.Mention,
            TabMyPrs => TimelineTab.MyPrs,
            TabWatching => TimelineTab.Watching,
            _ => TimelineTab.All,
        };
        Timeline.ApplyFilter(Timeline.Filter with { Tab = tab });
    }

    partial void OnSearchTextChanged(string value)
    {
        Timeline.ApplyFilter(Timeline.Filter with { SearchText = value });
    }


    [RelayCommand]
    private async Task SyncAsync(CancellationToken ct)
    {
        if (_syncService is null)
        {
            // Legacy/test path: just reload from the timeline service.
            await Timeline.ReloadAsync(ct).ConfigureAwait(false);
            OnPropertyChanged(nameof(UnreadCount));
            return;
        }

        string? accountId = _accountIdProvider is null ? null : await _accountIdProvider().ConfigureAwait(false);
        if (string.IsNullOrEmpty(accountId))
        {
            StatusText = "Configure PAT in Settings to sync.";
            return;
        }

        await TriggerSyncAsync(accountId, ct).ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    public string SettingsToggleLabel => IsSettingsVisible ? "Done" : "Settings";

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsVisible = false;
    }

    [RelayCommand]
    private Task RetrySyncAsync(CancellationToken ct) => SyncAsync(ct);

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
    }

    private async Task TriggerSyncAsync(string accountId, CancellationToken ct = default)
    {
        if (_syncService is null) return;
        try
        {
            await _syncService.SyncAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: the sync service is supposed to surface failures via
            // Progress events, but if it throws unexpectedly we still need to
            // keep the UI alive.
            ErrorMessage = ex.Message;
            IsSyncing = false;
        }
    }

    private void OnSyncProgress(SyncProgressEvent e)
    {
        switch (e.Stage)
        {
            case SyncStage.Starting:
                IsSyncing = true;
                StatusText = "Syncing…";
                ErrorMessage = null;
                break;
            case SyncStage.Fetching:
                StatusText = "Fetching notifications…";
                break;
            case SyncStage.Persisting:
                StatusText = "Saving…";
                break;
            case SyncStage.Pruning:
                StatusText = "Cleaning cache…";
                break;
            case SyncStage.Completed:
                IsSyncing = false;
                StatusText = "Idle";
                if (_clock is not null)
                {
                    LastSuccessfulSyncAt = _clock.UtcNow;
                    LastSyncText = $"Last sync {FormatSinceNow(_clock.UtcNow, _clock.UtcNow)}";
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    LastSuccessfulSyncAt = now;
                    LastSyncText = $"Last sync {FormatSinceNow(now, now)}";
                }
                if (e.Result?.RateLimit is { } rl)
                {
                    RateLimitText = FormatRateLimit(rl);
                }
                // Reload the timeline to reflect newly persisted rows. We can't
                // await here (this is invoked from a sync handler), but log
                // any faults so they don't disappear silently.
                _ = Timeline.ReloadAsync().ContinueWith(
                    t => System.Diagnostics.Debug.WriteLine(
                        $"OnSyncProgress.Completed: timeline reload faulted: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                break;
            case SyncStage.Failed:
                IsSyncing = false;
                if (e.Result is not null)
                {
                    ErrorMessage = MapErrorMessage(e.Result.Error, e.Result.Message);
                    if (e.Result.RateLimit is { } rl2)
                    {
                        RateLimitText = FormatRateLimit(rl2);
                    }
                }
                else
                {
                    ErrorMessage = "Sync failed.";
                }
                StatusText = "Sync failed";
                // Preserve cached items by NOT clearing Timeline.Items.
                break;
        }
    }

    /// <summary>
    /// Maps a sync error to a user-facing string. Public for testing.
    /// </summary>
    public static string MapErrorMessage(ErrorCategory? category, string? raw)
    {
        return category switch
        {
            ErrorCategory.Auth => "Token invalid. Update PAT in Settings.",
            ErrorCategory.RateLimit => string.IsNullOrEmpty(raw)
                ? "Rate limit reached."
                : $"Rate limit reached. {raw}",
            ErrorCategory.Network => "Network unavailable. Will retry.",
            ErrorCategory.ApiCompatibility => "GitHub API responded unexpectedly.",
            ErrorCategory.Database => "Local cache error. Logs have details.",
            ErrorCategory.Unknown => "Sync failed.",
            null => string.IsNullOrEmpty(raw) ? "Sync failed." : raw!,
            _ => "Sync failed.",
        };
    }

    /// <summary>
    /// Formats "Last sync N..." text. Public for testing.
    /// </summary>
    public static string FormatSinceNow(DateTimeOffset now, DateTimeOffset reference)
    {
        var delta = now - reference;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 30) return "just now";
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>
    /// Formats "API: 4500 / 5000 (resets HH:MM)" style text. Public for testing.
    /// </summary>
    public static string FormatRateLimit(RateLimitInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var rem = info.Remaining?.ToString() ?? "?";
        var resetPart = info.ResetAt is { } r
            ? $" (resets {r.ToLocalTime():HH:mm})"
            : string.Empty;
        return $"API: {rem}{resetPart}";
    }

    /// <summary>
    /// Refresh the human-readable Last sync N... text. Intended to be called
    /// periodically by a UI dispatcher timer.
    /// </summary>
    public void RefreshLastSyncText()
    {
        if (LastSuccessfulSyncAt is null)
        {
            return;
        }
        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        LastSyncText = $"Last sync {FormatSinceNow(now, LastSuccessfulSyncAt.Value)}";
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.UnreadCount))
        {
            OnPropertyChanged(nameof(UnreadCount));
            UnreadCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_syncService is not null && _progressHandler is not null)
        {
            _syncService.Progress -= _progressHandler;
        }

        Timeline.PropertyChanged -= OnTimelinePropertyChanged;
    }
}
