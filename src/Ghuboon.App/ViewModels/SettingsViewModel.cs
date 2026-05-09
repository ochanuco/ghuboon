using CommunityToolkit.Mvvm.ComponentModel;
using Ghuboon.App.Services;

namespace Ghuboon.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _appSettings;

    [ObservableProperty]
    private string _patPlaceholder = string.Empty;

    [ObservableProperty]
    private bool _osNotificationsEnabled = true;

    [ObservableProperty]
    private string _syncIntervalDisplay = "Every 5 minutes";

    [ObservableProperty]
    private string _cacheRetentionDisplay = "30 days";

    public SettingsViewModel()
        : this(new StubAppSettingsService())
    {
    }

    public SettingsViewModel(IAppSettingsService appSettings)
    {
        _appSettings = appSettings;
    }

    public bool IsConfigured => _appSettings.IsConfigured;
}
