using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.App.Views;

namespace Ghuboon.App;

public partial class App : Application
{
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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(appSettings, timeline),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
