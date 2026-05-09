using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ghuboon.App.Services;

namespace Ghuboon.App.ViewModels;

public partial class TimelineViewModel : ViewModelBase
{
    private readonly ITimelineService _timelineService;

    [ObservableProperty]
    private string _currentFilter = "All";

    public TimelineViewModel()
        : this(new StubTimelineService())
    {
    }

    public TimelineViewModel(ITimelineService timelineService)
    {
        _timelineService = timelineService;
        Items = new ObservableCollection<TimelineItemViewModel>();
        Load();
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; }

    public void Load()
    {
        Items.Clear();
        foreach (var item in _timelineService.GetPlaceholderItems())
        {
            Items.Add(item);
        }
    }
}
