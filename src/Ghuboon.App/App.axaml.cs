using System;
using System.Collections.Generic;
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
using Ghuboon.Infrastructure.Notifications;
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
        var eventRepo = new NotificationEventRepository(dbFactory);
        var syncStateRepo = new SyncStateRepository(dbFactory);
        var settingsRepo = new AppSettingsRepository(dbFactory);

        // 4. HttpClient + GitHub API client.
        //
        // SocketsHttpHandler-tuned for laptop sleep/wake survival.
        // The previous setup (default handler + HttpClient.Timeout=15s)
        // wedged after macOS sleep on 2026-05-14: a half-dead TCP
        // connection in the default pool stayed pooled across sleep,
        // and a subsequent SendAsync never returned because the wall-
        // clock-frozen sleep period broke the internal CancelAfter
        // timer used by HttpClient.Timeout. Result: SyncGate held,
        // every later sync.SyncAsync queued behind WaitAsync forever.
        //
        // Three knobs handle the regression:
        //   * PooledConnectionLifetime = 3 min — even an apparently-
        //     healthy connection is rotated out, so a stale one
        //     cannot be reused indefinitely.
        //   * PooledConnectionIdleTimeout = 1 min — idle connections
        //     dropped before macOS's TCP keepalive would normally
        //     notice.
        //   * ConnectTimeout = 10 s — fail fast on TCP connect rather
        //     than waiting on the OS-level SYN retransmit.
        // We still set HttpClient.Timeout=15s as a per-request safety
        // net (now backed by SocketsHttpHandler's own deadline path).
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
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
            eventRepo,
            syncStateRepo,
            apiClient,
            clock,
            _logger,
            defaultAccountId: AppSettingsService.PrimaryAccountId);

        // 6a. OS desktop notifications. The sync service raises
        // NewNotifications for high-priority new threads; the gate dedupes
        // via notification_local_states.last_notified_at and the platform
        // service shells out to osascript on macOS.
        IDesktopNotificationGate notifyGate = new HighPriorityNotificationGate(dbFactory, clock);
        IDesktopNotificationService notifyService = DesktopNotificationFactory.Create(_logger!);
        _syncService.NewNotifications += async (_, ev) =>
        {
            try
            {
                var bannerStartedAt = DateTimeOffset.UtcNow;
                _logger?.Information(
                    "banner.dispatch.start account={AccountId} candidateCount={CandidateCount}",
                    ev.AccountId,
                    ev.HighPriorityNew.Count);
                var toShow = await notifyGate
                    .FilterAsync(ev.AccountId, ev.HighPriorityNew)
                    .ConfigureAwait(false);
                _logger?.Information(
                    "banner.dispatch.afterGate account={AccountId} acceptedCount={AcceptedCount} suppressedCount={SuppressedCount} gateMs={GateMs:F0}",
                    ev.AccountId,
                    toShow.Count,
                    ev.HighPriorityNew.Count - toShow.Count,
                    (DateTimeOffset.UtcNow - bannerStartedAt).TotalMilliseconds);
                // Fire every banner in parallel: each osascript spawn is
                // ~50–200 ms and macOS's UserNotificationCenter queues
                // them anyway, so serializing with `await ShowAsync`
                // inside the foreach used to mean 5 banners = 5×spawn
                // serially while the TL had already rendered the new
                // rows. Letting them run concurrently keeps banner
                // latency closer to the single-banner case.
                var dispatched = new List<Task>(toShow.Count);
                foreach (var n in toShow)
                {
                    var dn = new DesktopNotification(
                        Id: n.Id,
                        Title: $"{n.Reason}: {n.RepositoryFullName}",
                        Body: n.Subject.Title,
                        Url: n.Subject.WebUrl);
                    dispatched.Add(notifyService.ShowAsync(dn));
                }
                if (dispatched.Count > 0)
                {
                    await Task.WhenAll(dispatched).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Desktop notification dispatch failed");
            }
        };

        // 7. App settings facade.
        IAppSettingsService appSettings = new AppSettingsService(accountRepo, settingsRepo);

        // 8. Per-row context for read-state actions.
        var browser = new Browser();
        var clipboard = new AvaloniaClipboard();
        IBookmarkRepository bookmarks = new BookmarkRepository(dbFactory);
        // Late-bound reference so each per-row context can route its
        // user-facing acks ("Copied" / "Bookmarked" / ...) into the
        // window's status bar. We can't capture the VM directly because
        // the factory is consumed before construction returns the VM.
        MainWindowViewModel? mainVmRef = null;
        TimelineItemContext ItemCtxFactory()
        {
            return new TimelineItemContext(
                Repository: notificationRepo,
                EventRepository: eventRepo,
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
                Log: _logger,
                Bookmarks: bookmarks,
                OnFlash: msg => mainVmRef?.ShowFlash(msg));
        }

        // 9. Timeline service backed by the event-log cache.
        var timelineService = new DbBackedTimelineService(
            eventRepo,
            repoRepo,
            accountRepo,
            ItemCtxFactory,
            AppSettingsService.PrimaryAccountId);

        // 10. Settings VM with real abstractions (token validation must hit
        //     the actual GitHubApiClient, not the design-time stub).
        var settingsDeps = new SettingsViewModel.SettingsViewModelDependencies(
            AppSettings: appSettings,
            CredentialStore: credentialStore,
            PatValidation: new PatValidationService(apiClient),
            NotificationRepository: notificationRepo,
            OpenBrowser: ViewModels.Settings.AboutSettingsViewModel.DefaultOpenBrowser,
            Clock: TimeProvider.System);
        var settingsVm = new SettingsViewModel(settingsDeps);

        // 11. MainWindow VM.
        var vm = new MainWindowViewModel(
            appSettings,
            timelineService,
            _syncService,
            clock,
            accountIdProvider: async () =>
            {
                var account = await appSettings.GetPrimaryAccountAsync().ConfigureAwait(false);
                return account?.Id;
            },
            settingsViewModel: settingsVm,
            bookmarks: bookmarks,
            bookmarkAccountId: AppSettingsService.PrimaryAccountId)
        {
            RepositoriesSource = timelineService,
            UiDispatcher = action => Dispatcher.UIThread.Post(action),
            AppSettingsStore = settingsRepo,
        };
        mainVmRef = vm;

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
