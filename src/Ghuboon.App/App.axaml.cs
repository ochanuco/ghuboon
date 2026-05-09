using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ghuboon.App.Platform;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.App.Views;

namespace Ghuboon.App;

public partial class App : Application
{
    // Phase 12: macOS menu bar residency.
    private IMenuBarHost? _menuBar;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IAppSettingsService appSettings = new StubAppSettingsService();
            ITimelineService timeline = new StubTimelineService();

            var mainVm = new MainWindowViewModel(appSettings, timeline);
            var mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            desktop.MainWindow = mainWindow;

            // Phase 12: macOS menu bar residency.
            RegisterPlatformIntegrations(desktop, mainWindow, mainVm);
        }

        base.OnFrameworkInitializationCompleted();
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
            _menuBar?.Dispose();
            _menuBar = null;
        };
    }
}
