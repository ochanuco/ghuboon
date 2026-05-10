using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ghuboon.App.Platform;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.App.Views;
using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.Credentials;
using Ghuboon.Infrastructure.GitHub;
using Ghuboon.Infrastructure.Logging;
using Ghuboon.Infrastructure.Storage;
using Ghuboon.Infrastructure.Sync;
using Serilog.Core;

namespace Ghuboon.App;

public partial class App : Application
{
    // Phase 12: macOS menu bar residency.
    private IMenuBarHost? _menuBar;

    // Phase 8/9/10/14: production DI graph composed at startup.
    private HttpClient? _httpClient;
    private NotificationSyncService? _syncService;
    private MainWindowViewModel? _mainVm;
    private DispatcherTimer? _lastSyncRefreshTimer;
    private Logger? _logger;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _mainVm = BuildMainViewModelWithRealDependencies();
            }
            catch (Exception ex)
            {
                // If the production graph fails to compose (no Keychain access on
                // a CI host, missing native deps, etc.), fall back to the stub
                // wiring so the app still launches and the user can see status.
                System.Diagnostics.Debug.WriteLine($"Composition root failed; using stubs. {ex.Message}");
                _mainVm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());
            }

            var mainWindow = new MainWindow
            {
                DataContext = _mainVm,
            };
            desktop.MainWindow = mainWindow;

            RegisterPlatformIntegrations(desktop, mainWindow, _mainVm);

            // Kick the initial load (cached items first, then sync).
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    await _mainVm.RefreshRepositoriesAsync().ConfigureAwait(true);
                    await _mainVm.InitialLoadAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Initial load failed: {ex.Message}");
                }
            });

            // Refresh the "Last sync N min ago" string every 30s.
            _lastSyncRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _lastSyncRefreshTimer.Tick += (_, _) => _mainVm?.RefreshLastSyncText();
            _lastSyncRefreshTimer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindowViewModel BuildMainViewModelWithRealDependencies()
    {
        // 1. Credential store (Keychain on macOS).
        var credentialStore = CredentialStoreFactory.Create();

        // 2. Encrypted SQLite connection factory.
        var dbFactory = new SqliteConnectionFactory(credentialStore);

        // 3. Repositories.
        var accountRepo = new AccountRepository(dbFactory);
        var repoRepo = new RepositoryRepository(dbFactory);
        var notificationRepo = new NotificationRepository(dbFactory);
        var syncStateRepo = new SyncStateRepository(dbFactory);
        var settingsRepo = new AppSettingsRepository(dbFactory);

        // 4. HttpClient + GitHub API client.
        _httpClient = new HttpClient();
        _logger = GhuboonLogger.Create();
        var apiClient = new GitHubApiClient(_httpClient, _logger);

        // 5. Clock.
        var clock = new SystemClock();

        // 6. Sync service.
        _syncService = new NotificationSyncService(
            credentialStore,
            accountRepo,
            repoRepo,
            notificationRepo,
            syncStateRepo,
            apiClient,
            clock,
            _logger,
            defaultAccountId: AppSettingsService.PrimaryAccountId);

        // 7. App settings facade.
        IAppSettingsService appSettings = new AppSettingsService(accountRepo, settingsRepo);

        // 8. Per-row context for read-state actions.
        var browser = new Browser();
        var clipboard = new AvaloniaClipboard();
        TimelineItemContext ItemCtxFactory()
        {
            return new TimelineItemContext(
                Repository: notificationRepo,
                Api: apiClient,
                Browser: browser,
                Clipboard: clipboard,
                Clock: clock,
                PatProvider: async ct =>
                {
                    var account = await appSettings.GetPrimaryAccountAsync(ct).ConfigureAwait(false);
                    if (account is null) return null;
                    return await credentialStore.GetAsync(account.CredentialKey, ct).ConfigureAwait(false);
                },
                OnMarkRead: null,
                Log: _logger);
        }

        // 9. Timeline service backed by the real cache.
        var timelineService = new DbBackedTimelineService(
            notificationRepo,
            repoRepo,
            accountRepo,
            ItemCtxFactory,
            AppSettingsService.PrimaryAccountId);

        // 10. MainWindow VM.
        var vm = new MainWindowViewModel(
            appSettings,
            timelineService,
            _syncService,
            clock,
            accountIdProvider: async () =>
            {
                var account = await appSettings.GetPrimaryAccountAsync().ConfigureAwait(false);
                return account?.Id;
            })
        {
            RepositoriesSource = timelineService,
            UiDispatcher = action => Dispatcher.UIThread.Post(action),
        };

        return vm;
    }

    // Phase 12: macOS menu bar residency. Kept narrow on purpose so other
    // lanes touching App.axaml.cs don't conflict.
    private void RegisterPlatformIntegrations(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow,
        MainWindowViewModel mainVm)
    {
        _menuBar = MenuBarHostFactory.Create();
        _menuBar.Initialize(new MenuBarContext(
            ShowMainWindow: () => Dispatcher.UIThread.Post(() =>
            {
                mainWindow.Show();
                mainWindow.Activate();
            }),
            HideMainWindow: () => Dispatcher.UIThread.Post(mainWindow.Hide),
            SyncNow: () => Dispatcher.UIThread.Post(() => mainVm.SyncCommand?.Execute(null)),
            OpenSettings: () => Dispatcher.UIThread.Post(() =>
            {
                mainWindow.Show();
                mainWindow.Activate();
                mainVm.OpenSettingsCommand?.Execute(null);
            }),
            Quit: () => Dispatcher.UIThread.Post(() => desktop.Shutdown())));

        // Mirror UnreadCount changes into the menu-bar host. Marshal to the UI
        // thread because the Timeline VM may raise the event from a background
        // continuation (notification arrives, mark-read API completes, etc.) and
        // AppKit / NSStatusItem mutation is main-thread-only.
        mainVm.UnreadCountChanged += (_, _) =>
        {
            var count = mainVm.UnreadCount;
            Dispatcher.UIThread.Post(() => _menuBar?.UpdateUnreadCount(count));
        };
        // Issue #43: read UnreadCount inside the lambda so the posted update
        // uses the latest value rather than a stale snapshot captured here.
        // If a UnreadCountChanged event fires between this line and the post
        // running on the UI thread, the lambda observes the up-to-date value.
        Dispatcher.UIThread.Post(() => _menuBar?.UpdateUnreadCount(mainVm.UnreadCount));

        // Close-to-hide on macOS so the app keeps living in the menu bar.
        // On other platforms _menuBar is a NoOpMenuBarHost; closing should
        // still quit the app, so we leave the default behavior alone.
        var shuttingDown = false;
        if (_menuBar is not NoOpMenuBarHost)
        {
            mainWindow.Closing += (_, e) =>
            {
                if (!shuttingDown)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
            };
        }

        desktop.ShutdownRequested += (_, _) =>
        {
            shuttingDown = true;
            try
            {
                _lastSyncRefreshTimer?.Stop();
            }
            catch { /* best-effort */ }

            try
            {
                _syncService?.Stop();
                _syncService?.Dispose();
            }
            catch { /* best-effort */ }

            try
            {
                _menuBar?.Dispose();
            }
            catch { /* best-effort */ }
            _menuBar = null;

            try
            {
                _httpClient?.Dispose();
            }
            catch { /* best-effort */ }
            _httpClient = null;

            try
            {
                _mainVm?.Dispose();
            }
            catch { /* best-effort */ }

            try
            {
                _logger?.Dispose();
            }
            catch { /* best-effort */ }
        };
    }
}
