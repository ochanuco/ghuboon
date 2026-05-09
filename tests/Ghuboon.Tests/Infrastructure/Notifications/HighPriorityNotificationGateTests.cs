using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Notifications;
using Ghuboon.Tests.Infrastructure.Storage;
using Ghuboon.Tests.Infrastructure.Sync;

namespace Ghuboon.Tests.Infrastructure.Notifications;

public class HighPriorityNotificationGateTests
{
    private static GitHubNotification Sample(
        string id,
        NotificationReason reason,
        string accountId = "acct-1")
    {
        return new GitHubNotification(
            Id: id,
            AccountId: accountId,
            ThreadId: id,
            RepositoryFullName: "ochanuco/ghuboon",
            Subject: new NotificationSubject("PullRequest", $"PR {id}", null, "https://github.com/x/y/pull/1"),
            Reason: reason,
            Unread: true,
            UpdatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            LastReadAt: null);
    }

    [Theory]
    [InlineData(NotificationReason.Comment)]
    [InlineData(NotificationReason.MyPr)]
    [InlineData(NotificationReason.State)]
    [InlineData(NotificationReason.Watching)]
    [InlineData(NotificationReason.Manual)]
    [InlineData(NotificationReason.Invitation)]
    [InlineData(NotificationReason.SecurityAlert)]
    [InlineData(NotificationReason.CiActivity)]
    [InlineData(NotificationReason.Unknown)]
    public async Task FilterAsync_excludes_low_priority_reasons(NotificationReason reason)
    {
        await using var temp = new TempDatabase();
        var clock = new FakeClock();
        var gate = new HighPriorityNotificationGate(temp.Factory, clock);

        var result = await gate.FilterAsync("acct-1", new[] { Sample("n1", reason) });

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(NotificationReason.Review)]
    [InlineData(NotificationReason.Mention)]
    [InlineData(NotificationReason.TeamMention)]
    [InlineData(NotificationReason.Assigned)]
    public async Task FilterAsync_accepts_high_priority_reason_on_first_pass(NotificationReason reason)
    {
        await using var temp = new TempDatabase();
        var clock = new FakeClock();
        var gate = new HighPriorityNotificationGate(temp.Factory, clock);

        var notif = Sample("n1", reason);
        var first = await gate.FilterAsync("acct-1", new[] { notif });

        Assert.Single(first);
        Assert.Equal("n1", first[0].Id);

        // Verify last_notified_at was persisted by the dedup write.
        var tracker = new LastNotifiedTrackerProbe(temp.Factory);
        var stored = await tracker.GetLastNotifiedAtAsync("n1");
        Assert.NotNull(stored);
        Assert.Equal(clock.UtcNow, stored);
    }

    [Fact]
    public async Task FilterAsync_dedups_previously_notified_id()
    {
        await using var temp = new TempDatabase();
        var clock = new FakeClock();
        var gate = new HighPriorityNotificationGate(temp.Factory, clock);

        var notif = Sample("n1", NotificationReason.Mention);
        var first = await gate.FilterAsync("acct-1", new[] { notif });
        Assert.Single(first);

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await gate.FilterAsync("acct-1", new[] { notif });

        Assert.Empty(second);
    }

    [Fact]
    public async Task FilterAsync_returns_only_new_when_mixed_with_already_notified()
    {
        await using var temp = new TempDatabase();
        var clock = new FakeClock();
        var gate = new HighPriorityNotificationGate(temp.Factory, clock);

        var n1 = Sample("n1", NotificationReason.Review);
        var n2 = Sample("n2", NotificationReason.Mention);
        var n3 = Sample("n3", NotificationReason.Comment); // low-priority, never accepted
        var n4 = Sample("n4", NotificationReason.Assigned);

        // Pre-mark n1 as already notified.
        await gate.FilterAsync("acct-1", new[] { n1 });

        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await gate.FilterAsync("acct-1", new[] { n1, n2, n3, n4 });

        var ids = result.Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "n2", "n4" }, ids);
    }

    [Fact]
    public async Task FilterAsync_with_empty_candidates_returns_empty()
    {
        await using var temp = new TempDatabase();
        var clock = new FakeClock();
        var gate = new HighPriorityNotificationGate(temp.Factory, clock);

        var result = await gate.FilterAsync("acct-1", Array.Empty<GitHubNotification>());

        Assert.Empty(result);
    }

    /// <summary>
    /// Test-only probe that exposes the internal <c>LastNotifiedTracker</c> for
    /// post-condition checks without making the production type public.
    /// </summary>
    private sealed class LastNotifiedTrackerProbe
    {
        private readonly LastNotifiedTracker _inner;

        public LastNotifiedTrackerProbe(Ghuboon.Core.Abstractions.IDbConnectionFactory factory)
        {
            _inner = new LastNotifiedTracker(factory);
        }

        public Task<DateTimeOffset?> GetLastNotifiedAtAsync(string id) =>
            _inner.GetLastNotifiedAtAsync(id);
    }
}
