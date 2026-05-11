using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Timeline;

public class TimelineViewModelTests
{
    [Fact]
    public async Task Load_AppliesTabFilter_OnlyMentionsShown()
    {
        var fake = new FakeTimelineService();
        fake.AddNotification(NotificationReason.Mention, "octocat/x", "mention 1");
        fake.AddNotification(NotificationReason.TeamMention, "octocat/x", "team mention");
        fake.AddNotification(NotificationReason.Watching, "octocat/x", "watching");

        var vm = new TimelineViewModel(fake);
        vm.ApplyFilter(new TimelineFilter(TimelineTab.Mention, null, null));
        await vm.ReloadAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, i => Assert.Contains(i.Reason, new[] { NotificationReason.Mention, NotificationReason.TeamMention }));
    }

    [Fact]
    public async Task Load_AppliesRepoFilter()
    {
        var fake = new FakeTimelineService();
        fake.AddNotification(NotificationReason.Mention, "a/r", "a-row");
        fake.AddNotification(NotificationReason.Mention, "b/r", "b-row");

        var vm = new TimelineViewModel(fake);
        vm.ApplyFilter(new TimelineFilter(TimelineTab.All, new HashSet<string> { "a/r" }, null));
        await vm.ReloadAsync();

        Assert.Single(vm.Items);
        Assert.Equal("a/r", vm.Items[0].RepositoryFullName);
    }

    [Fact]
    public async Task Load_AppliesSearch_CaseInsensitive()
    {
        var fake = new FakeTimelineService();
        fake.AddNotification(NotificationReason.Mention, "octocat/r", "Hello world");
        fake.AddNotification(NotificationReason.Mention, "octocat/r", "Goodbye world");

        var vm = new TimelineViewModel(fake);
        vm.ApplyFilter(new TimelineFilter(TimelineTab.All, null, "HELLO"));
        await vm.ReloadAsync();

        Assert.Single(vm.Items);
        Assert.Equal("Hello world", vm.Items[0].Title);
    }

    [Fact]
    public async Task Load_OrdersBySourceUpdatedAtAsc()
    {
        // Tween-style timeline: oldest at the top, newest at the bottom.
        // Mirrors DbBackedTimelineService.LoadAsync's outer ORDER BY
        // source_updated_at ASC.
        var fake = new FakeTimelineService();
        var t0 = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        fake.AddNotification(NotificationReason.Mention, "a/r", "older", updatedAt: t0.AddHours(-2));
        fake.AddNotification(NotificationReason.Mention, "a/r", "newer", updatedAt: t0);
        fake.AddNotification(NotificationReason.Mention, "a/r", "middle", updatedAt: t0.AddHours(-1));

        var vm = new TimelineViewModel(fake);
        await vm.ReloadAsync();

        Assert.Equal(new[] { "older", "middle", "newer" }, new[] { vm.Items[0].Title, vm.Items[1].Title, vm.Items[2].Title });
    }

    [Fact]
    public async Task EmptyResult_SetsIsEmpty()
    {
        var fake = new FakeTimelineService();
        var vm = new TimelineViewModel(fake);
        await vm.ReloadAsync();

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void ItemContextFactory_PlaceholderPath_InvokedPerRow()
    {
        // Issue #43: the constructor's placeholder fast-path must also call
        // the factory per-row, not once and reused.
        var stub = new StubTimelineService();
        var placeholders = stub.GetPlaceholderItems();
        var factoryCalls = 0;
        _ = new TimelineViewModel(stub, () =>
        {
            factoryCalls++;
            return TimelineItemContext.Empty;
        });

        Assert.Equal(placeholders.Count, factoryCalls);
        Assert.True(factoryCalls > 0, "StubTimelineService should yield at least one placeholder.");
    }

    [Fact]
    public async Task ItemContextFactory_IsInvokedPerRow()
    {
        // Issue #43: each TimelineItemViewModel must get its own
        // TimelineItemContext, so the factory is called once per row, not once
        // per load.
        var t0 = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimelineService();
        fake.AddNotification(NotificationReason.Mention, "a/r", "row-1", updatedAt: t0);
        fake.AddNotification(NotificationReason.Mention, "a/r", "row-2", updatedAt: t0.AddMinutes(-1));
        fake.AddNotification(NotificationReason.Mention, "a/r", "row-3", updatedAt: t0.AddMinutes(-2));

        var factoryCalls = 0;
        var vm = new TimelineViewModel(fake, () =>
        {
            factoryCalls++;
            return TimelineItemContext.Empty;
        });

        await vm.ReloadAsync();

        Assert.Equal(3, vm.Items.Count);
        // 1 invocation in the constructor (placeholder fast-path returns 0 rows
        // for FakeTimelineService) + 3 invocations during ReloadAsync.
        Assert.Equal(3, factoryCalls);
    }

    [Fact]
    public async Task UnreadCount_TracksItemReadState()
    {
        var t0 = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimelineService();
        fake.AddNotification(NotificationReason.Mention, "a/r", "u1", unread: true, updatedAt: t0);
        fake.AddNotification(NotificationReason.Mention, "a/r", "u2", unread: true, updatedAt: t0.AddMinutes(-1));
        fake.AddNotification(NotificationReason.Mention, "a/r", "r1", unread: false, updatedAt: t0.AddMinutes(-2));

        var vm = new TimelineViewModel(fake);
        await vm.ReloadAsync();

        Assert.Equal(2, vm.UnreadCount);

        // Find the first unread row and mark it read; aggregate must tick down.
        var firstUnread = vm.Items.First(i => i.Unread);
        firstUnread.Unread = false;
        Assert.Equal(1, vm.UnreadCount);
    }

    /// <summary>
    /// Lightweight test-side <see cref="ITimelineService"/> that maps a list of
    /// <see cref="NotificationEvent"/> through the same filter/sort logic the
    /// production service uses. Removes the dependency on the SQLite stack while
    /// still exercising the filter pipeline.
    /// </summary>
    private sealed class FakeTimelineService : ITimelineService
    {
        public List<NotificationEvent> Events { get; } = new();
        private long _nextId = 1;

        public void AddNotification(
            NotificationReason reason,
            string repo,
            string title,
            bool unread = true,
            DateTimeOffset? updatedAt = null,
            string subjectType = "PullRequest")
        {
            // NotificationEvent invariants mirror GitHubNotification:
            // NotificationId must equal "{AccountId}:{ThreadId}".
            var threadId = Guid.NewGuid().ToString();
            var when = updatedAt ?? DateTimeOffset.UtcNow;
            Events.Add(new NotificationEvent(
                Id: _nextId++,
                AccountId: "primary",
                NotificationId: $"primary:{threadId}",
                ThreadId: threadId,
                RepositoryFullName: repo,
                Subject: new NotificationSubject(subjectType, title, null, null),
                Reason: reason,
                SourceUpdatedAt: when,
                ObservedAt: when,
                Unread: unread,
                LastReadAt: null,
                RawJson: "{}"));
        }

        public Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
        {
            IEnumerable<NotificationEvent> q = Events;
            q = q.Where(n => DbBackedTimelineService.MatchesTab(n.Reason, filter.Tab));

            if (!filter.MatchesAllRepositories)
            {
                var allowed = new HashSet<string>(filter.RepositoryFullNames!, StringComparer.OrdinalIgnoreCase);
                q = q.Where(n => n.RepositoryFullName is not null && allowed.Contains(n.RepositoryFullName));
            }
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var needle = filter.SearchText.Trim();
                q = q.Where(n =>
                    n.RepositoryFullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || n.Subject.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || n.Reason.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (n.Subject.Type ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            // Match the production OUTER ORDER from
            // DbBackedTimelineService.LoadAsync: rows sorted ASC by
            // SourceUpdatedAt (Tween-style — newest at the bottom).
            // Use Id as a deterministic tie-break to match the SQL
            // "ORDER BY source_updated_at ASC, id ASC", so two events
            // sharing a timestamp keep the same relative order the
            // production query would emit. The previous DESC sort masked
            // regressions because tests could pass under either direction.
            return Task.FromResult<IReadOnlyList<NotificationEvent>>(
                q.OrderBy(n => n.SourceUpdatedAt).ThenBy(n => n.Id).ToList());
        }

        public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());

        public IReadOnlyList<NotificationEvent> GetPlaceholderItems() => Array.Empty<NotificationEvent>();
    }
}
