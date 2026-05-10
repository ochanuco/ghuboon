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
    public async Task Load_ReplacesItems()
    {
        var vm = new TimelineViewModel(new StubTimelineService());

        // Pre-populate with an identifiable dummy element. After LoadAsync the
        // dummy must be gone (the items are *replaced*, not appended) and the
        // new items must be present.
        const string dummyMarker = "DUMMY-LOAD-REPLACES-MARKER";
        var dummy = TimelineItemViewModel.Placeholder(
            "dummy-id",
            "octocat/dummy",
            dummyMarker,
            "mention",
            DateTimeOffset.UtcNow,
            unread: true);
        vm.Items.Insert(0, dummy);
        Assert.Contains(vm.Items, i => i.Title == dummyMarker);

        await vm.LoadAsync();

        Assert.Equal(5, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Title == dummyMarker);
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
        public Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NotificationEvent>>(GetPlaceholderItems());

        public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());

        public IReadOnlyList<NotificationEvent> GetPlaceholderItems()
        {
            var now = DateTimeOffset.UtcNow;
            return new[]
            {
                new NotificationEvent(
                    Id: 1,
                    AccountId: "primary",
                    NotificationId: "primary:a",
                    ThreadId: "a",
                    RepositoryFullName: "repo/one",
                    Subject: new NotificationSubject("PullRequest", "title 1", null, null),
                    Reason: NotificationReason.Mention,
                    SourceUpdatedAt: now,
                    ObservedAt: now,
                    Unread: true,
                    LastReadAt: null,
                    RawJson: "{}"),
                new NotificationEvent(
                    Id: 2,
                    AccountId: "primary",
                    NotificationId: "primary:b",
                    ThreadId: "b",
                    RepositoryFullName: "repo/two",
                    Subject: new NotificationSubject("PullRequest", "title 2", null, null),
                    Reason: NotificationReason.Watching,
                    SourceUpdatedAt: now.AddHours(-1),
                    ObservedAt: now.AddHours(-1),
                    Unread: false,
                    LastReadAt: null,
                    RawJson: "{}"),
            };
        }
    }
}
