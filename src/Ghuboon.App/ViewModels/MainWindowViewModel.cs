using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghuboon.App.Services;

namespace Ghuboon.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public const string TabAll = "All";
    public const string TabReview = "Review";
    public const string TabMention = "Mention";
    public const string TabMyPrs = "My PRs";
    public const string TabWatching = "Watching";

    private readonly IAppSettingsService _appSettings;

    [ObservableProperty]
    private string _selectedTab = TabAll;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSettingsVisible;

    public MainWindowViewModel()
        : this(new StubAppSettingsService(), new StubTimelineService())
    {
    }

    public MainWindowViewModel(IAppSettingsService appSettings, ITimelineService timelineService)
    {
        _appSettings = appSettings;
        Tabs = new[] { TabAll, TabReview, TabMention, TabMyPrs, TabWatching };
        Timeline = new TimelineViewModel(timelineService);
        Settings = new SettingsViewModel(appSettings);
    }

    public IReadOnlyList<string> Tabs { get; }

    public TimelineViewModel Timeline { get; }

    public SettingsViewModel Settings { get; }

    public string Title => "Ghuboon";

    public int UnreadCount => Timeline.Items.Count(i => i.Unread);

    partial void OnSelectedTabChanged(string value)
    {
        Timeline.CurrentFilter = value;
    }

    [RelayCommand]
    private void Sync()
    {
        Timeline.Load();
        OnPropertyChanged(nameof(UnreadCount));
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
}
