using System;
using System.Collections.Generic;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;

namespace Ghuboon.Tests.App;

public class TimelineViewModelTests
{
    [Fact]
    public void Constructor_LoadsItemsFromService()
    {
        var vm = new TimelineViewModel(new StubTimelineService());

        Assert.Equal(5, vm.Items.Count);
    }

    [Fact]
    public void Load_ReplacesItems()
    {
        var vm = new TimelineViewModel(new StubTimelineService());

        vm.Load();

        Assert.Equal(5, vm.Items.Count);
    }

    [Fact]
    public void Load_UsesProvidedService()
    {
        var fake = new FakeTimelineService();
        var vm = new TimelineViewModel(fake);

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("repo/one", vm.Items[0].RepositoryFullName);
    }

    [Fact]
    public void DefaultFilter_IsAll()
    {
        var vm = new TimelineViewModel(new StubTimelineService());

        Assert.Equal("All", vm.CurrentFilter);
    }

    private sealed class FakeTimelineService : ITimelineService
    {
        public IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems() => new[]
        {
            new TimelineItemViewModel("a", "repo/one", "title 1", "mention", DateTimeOffset.UtcNow, true),
            new TimelineItemViewModel("b", "repo/two", "title 2", "subscribed", DateTimeOffset.UtcNow.AddHours(-1), false),
        };
    }
}
