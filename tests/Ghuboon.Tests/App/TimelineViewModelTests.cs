using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App;

/// <summary>
/// Smoke tests for the legacy <see cref="TimelineViewModel"/> contract preserved
/// for back-compat. The richer Phase 8/9 behavior lives under
/// <c>App/Timeline/TimelineViewModelTests.cs</c>.
/// </summary>
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

        // After construction the placeholder fast-path returns the fake's two items.
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
        public Task<IReadOnlyList<TimelineItemViewModel>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimelineItemViewModel>>(GetPlaceholderItems());

        public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());

        public IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems() => new[]
        {
            new TimelineItemViewModel("a", "repo/one", "title 1", "mention", DateTimeOffset.UtcNow, true),
            new TimelineItemViewModel("b", "repo/two", "title 2", "subscribed", DateTimeOffset.UtcNow.AddHours(-1), false),
        };
    }
}
