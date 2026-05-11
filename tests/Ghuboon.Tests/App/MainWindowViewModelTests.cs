using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

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
                MainWindowViewModel.TabBookmarks,
                MainWindowViewModel.TabReview,
                MainWindowViewModel.TabMention,
                MainWindowViewModel.TabMyPrs,
                MainWindowViewModel.TabWatching,
            },
            vm.Tabs);
    }

    [Fact]
    public async Task ChangingSelectedTab_RaisesPropertyChanged_AndUpdatesTimelineFilter()
    {
        var vm = new MainWindowViewModel(new StubAppSettingsService(), new StubTimelineService());
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SelectedTab = MainWindowViewModel.TabReview;

        Assert.Contains(nameof(MainWindowViewModel.SelectedTab), changes);
        Assert.Equal(MainWindowViewModel.TabReview, vm.SelectedTab);

        // OnSelectedTabChanged debounces (~60 ms) before pushing the new
        // Tab into Timeline.Filter to coalesce rapid A/S cycling. Wait
        // out the debounce window before asserting the filter applied.
        await WaitUntil(() => vm.Timeline.CurrentFilter == "Review", TimeSpan.FromSeconds(2));
        Assert.Equal("Review", vm.Timeline.CurrentFilter);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
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
    public async Task SyncCommand_ReloadsTimeline()
    {
        // Issue #8: assert that SyncCommand actually drives a reload through the
        // timeline service (was previously only checking Items.Count).
        var spy = new SpyTimelineService();
        var vm = new MainWindowViewModel(new StubAppSettingsService(), spy);

        Assert.Equal(1, spy.PlaceholderCalls); // ctor placeholder fast-path
        Assert.Equal(0, spy.LoadCalls);

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.True(spy.LoadCalls >= 1, $"Expected SyncCommand to trigger ITimelineService.LoadAsync; observed {spy.LoadCalls}.");
    }

    /// <summary>
    /// Records every <see cref="ITimelineService.LoadAsync"/> call so tests can
    /// assert reload behavior without coupling to <c>Items.Count</c>.
    /// </summary>
    private sealed class SpyTimelineService : ITimelineService
    {
        public int LoadCalls { get; private set; }
        public int PlaceholderCalls { get; private set; }
        public List<TimelineFilter> Filters { get; } = new();

        public Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
        {
            LoadCalls++;
            Filters.Add(filter);
            return Task.FromResult<IReadOnlyList<NotificationEvent>>(Array.Empty<NotificationEvent>());
        }

        public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());

        public IReadOnlyList<NotificationEvent> GetPlaceholderItems()
        {
            PlaceholderCalls++;
            return Array.Empty<NotificationEvent>();
        }
    }
}
