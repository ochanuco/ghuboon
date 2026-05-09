using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private bool _isSettingsVisible;

    [ObservableProperty]
    private string? _selectedRepositoryFullName;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string _statusText = string.Empty;

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
        Func<Task<string?>>? accountIdProvider)
    {
        _appSettings = appSettings;
        _syncService = syncService;
        _clock = clock;
        _accountIdProvider = accountIdProvider;

        Tabs = new[] { TabAll, TabReview, TabMention, TabMyPrs, TabWatching };
        Timeline = new TimelineViewModel(timelineService);
        Settings = new SettingsViewModel(appSettings);
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
                _ = TriggerSyncAsync(accountId!);
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

    partial void OnSelectedRepositoryFullNameChanged(string? value)
    {
        Timeline.ApplyFilter(Timeline.Filter with { RepositoryFullName = string.IsNullOrEmpty(value) ? null : value });
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
        IsSettingsVisible = true;
    }

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
                // Reload the timeline to reflect newly persisted rows.
                _ = Timeline.ReloadAsync();
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
        if (info is null) return string.Empty;

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
