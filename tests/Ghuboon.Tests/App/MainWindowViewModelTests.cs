using System.ComponentModel;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;

namespace Ghuboon.Tests.App;

public class MainWindowViewModelTests
{
    [Fact]
    public void DefaultSelectedTab_IsAll()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());

        Assert.Equal(MainWindowViewModel.TabAll, vm.SelectedTab);
    }

    [Fact]
    public void Tabs_ContainsAllExpectedTabs()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());

        Assert.Equal(
            new[]
            {
                MainWindowViewModel.TabAll,
                MainWindowViewModel.TabReview,
                MainWindowViewModel.TabMention,
                MainWindowViewModel.TabMyPrs,
                MainWindowViewModel.TabWatching,
            },
            vm.Tabs);
    }

    [Fact]
    public void ChangingSelectedTab_RaisesPropertyChanged_AndUpdatesTimelineFilter()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SelectedTab = MainWindowViewModel.TabReview;

        Assert.Contains(nameof(MainWindowViewModel.SelectedTab), changes);
        Assert.Equal(MainWindowViewModel.TabReview, vm.SelectedTab);
        Assert.Equal(MainWindowViewModel.TabReview, vm.Timeline.CurrentFilter);
    }

    [Fact]
    public void SearchText_IsObservable()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SearchText = "ghuboon";

        Assert.Equal("ghuboon", vm.SearchText);
        Assert.Contains(nameof(MainWindowViewModel.SearchText), changes);
    }

    [Fact]
    public void OpenSettingsCommand_TogglesSettingsVisible()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());

        Assert.False(vm.IsSettingsVisible);

        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsVisible);

        vm.CloseSettingsCommand.Execute(null);
        Assert.False(vm.IsSettingsVisible);
    }

    [Fact]
    public void SyncCommand_ReloadsTimeline()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());
        var initialCount = vm.Timeline.Items.Count;

        vm.SyncCommand.Execute(null);

        Assert.Equal(initialCount, vm.Timeline.Items.Count);
    }
}
