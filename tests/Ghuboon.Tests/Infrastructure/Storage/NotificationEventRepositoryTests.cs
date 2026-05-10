using Dapper;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class NotificationEventRepositoryTests
{
    private static NotificationEvent SampleEvent(
        string notificationId = "acct-1:thread-1",
        string accountId = "acct-1",
        string repo = "ochanuco/ghuboon",
        NotificationReason reason = NotificationReason.Mention,
        bool unread = true,
        DateTimeOffset? sourceUpdatedAt = null,
        DateTimeOffset? observedAt = null)
    {
        var sep = notificationId.IndexOf(':');
        var threadId = sep >= 0 ? notificationId[(sep + 1)..] : notificationId;
        return new NotificationEvent(
            Id: 0,
            AccountId: accountId,
            NotificationId: notificationId,
            ThreadId: threadId,
            RepositoryFullName: repo,
            Subject: new NotificationSubject(
                "PullRequest",
                "Sample PR",
                "https://api.github.com/repos/ochanuco/ghuboon/pulls/1",
                "https://github.com/ochanuco/ghuboon/pull/1"),
            Reason: reason,
            SourceUpdatedAt: sourceUpdatedAt ?? new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            ObservedAt: observedAt ?? new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            Unread: unread,
            LastReadAt: null,
            RawJson: """{"id":"thread-1"}""");
    }

    /// <summary>
    /// Inserts a notifications row that the FK from notification_events
    /// requires. The default <see cref="TempDatabase"/> only seeds a small
    /// fixture set of notifications so most repo tests need to add their own
    /// parent row before exercising event inserts.
    /// </summary>
    private static async Task SeedNotificationAsync(TempDatabase temp, string id, string accountId)
    {
        await using var conn = await temp.Factory.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT OR IGNORE INTO notifications (
                id, account_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, unread, updated_at, last_read_at,
                raw_json, created_at, synced_at)
            VALUES (@id, @accountId, @threadId, 'ochanuco/ghuboon',
                    'PullRequest', 'Sample PR', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');
            """,
            new { id, accountId, threadId = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id });
    }

    [Fact]
    public async Task TryAppend_inserts_first_observation_returns_true()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var inserted = await repo.TryAppendAsync(SampleEvent());

        Assert.True(inserted);
        var list = await repo.ListByAccountAsync("acct-1", 100);
        var single = Assert.Single(list);
        Assert.Equal("acct-1:thread-1", single.NotificationId);
        // Repository assigns the local autoincrement id.
        Assert.True(single.Id > 0);
    }

    [Fact]
    public async Task TryAppend_dedups_on_account_notification_source_updated_at()
    {
        // The unique index ux_events_dedup must reject re-observation of the
        // same upstream updated_at; TryAppend reports false rather than
        // throwing. Observed_at is intentionally different across the two
        // calls — only the (account, notification, source_updated_at) triple
        // is dedup'd.
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var first = await repo.TryAppendAsync(SampleEvent());
        var second = await repo.TryAppendAsync(SampleEvent(observedAt: new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(first);
        Assert.False(second);
        var list = await repo.ListByAccountAsync("acct-1", 100);
        Assert.Single(list);
    }

    [Fact]
    public async Task TryAppend_distinct_source_updated_at_creates_new_row()
    {
        // A real upstream transition (Open -> Draft -> Open) bumps updated_at,
        // so the third observation must produce a third row even though the
        // notification id has not changed.
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0)));
        Assert.True(await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0.AddHours(1))));
        Assert.True(await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0.AddHours(2))));

        var list = await repo.ListByAccountAsync("acct-1", 100);
        Assert.Equal(3, list.Count);
        Assert.All(list, e => Assert.Equal("acct-1:thread-1", e.NotificationId));
    }

    [Fact]
    public async Task ListByAccount_returns_only_matching_account_ordered_by_source_updated_at_ascending()
    {
        // Tween-like timeline: rows come back oldest-first (newest at the
        // bottom of the UI). Ordering is by source_updated_at — the GitHub
        // thread updated_at — so the displayed order matches the "Updated"
        // column rather than reflecting when our sync happened to run.
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:1", "acct-1");
        await SeedNotificationAsync(temp, "acct-1:2", "acct-1");
        await SeedNotificationAsync(temp, "acct-2:1", "acct-2");
        var repo = new NotificationEventRepository(temp.Factory);

        var observedNow = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var older = SampleEvent(notificationId: "acct-1:1") with
        {
            SourceUpdatedAt = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            ObservedAt = observedNow,
        };
        var newer = SampleEvent(notificationId: "acct-1:2") with
        {
            SourceUpdatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            ObservedAt = observedNow,
        };
        var otherAccount = SampleEvent(notificationId: "acct-2:1", accountId: "acct-2");

        await repo.TryAppendAsync(older);
        await repo.TryAppendAsync(newer);
        await repo.TryAppendAsync(otherAccount);

        var list = await repo.ListByAccountAsync("acct-1", 100);
        Assert.Equal(2, list.Count);
        Assert.Equal("acct-1:1", list[0].NotificationId);
        Assert.Equal("acct-1:2", list[1].NotificationId);
    }

    [Fact]
    public async Task ListByAccount_respects_limit()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0.AddHours(i)));
        }

        var capped = await repo.ListByAccountAsync("acct-1", 2);
        Assert.Equal(2, capped.Count);

        var none = await repo.ListByAccountAsync("acct-1", 0);
        Assert.Empty(none);
    }

    [Fact]
    public async Task MarkThreadAsRead_flips_every_event_for_thread()
    {
        // mark-as-read on a thread must affect every cached event row, not
        // just the latest one — otherwise scrolling back through the
        // timeline still shows past observations as unread after the user
        // resolved the thread.
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        await SeedNotificationAsync(temp, "acct-1:thread-other", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0));
        await repo.TryAppendAsync(SampleEvent(sourceUpdatedAt: t0.AddHours(1)));
        await repo.TryAppendAsync(SampleEvent(notificationId: "acct-1:thread-other", sourceUpdatedAt: t0));

        var readAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var affected = await repo.MarkThreadAsReadAsync("acct-1", "acct-1:thread-1", readAt);

        Assert.Equal(2, affected);

        var list = await repo.ListByAccountAsync("acct-1", 100);
        var sameThread = list.Where(e => e.NotificationId == "acct-1:thread-1").ToList();
        Assert.Equal(2, sameThread.Count);
        Assert.All(sameThread, e =>
        {
            Assert.False(e.Unread);
            Assert.Equal(readAt, e.LastReadAt);
        });
        // Untouched sibling thread must remain unread.
        var otherThread = Assert.Single(list, e => e.NotificationId == "acct-1:thread-other");
        Assert.True(otherThread.Unread);
        Assert.Null(otherThread.LastReadAt);
    }

    [Fact]
    public async Task DeleteOlderThan_removes_only_stale_event_rows()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var stale = SampleEvent(observedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                                sourceUpdatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fresh = SampleEvent(observedAt: new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero),
                                sourceUpdatedAt: new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero));
        Assert.True(await repo.TryAppendAsync(stale));
        Assert.True(await repo.TryAppendAsync(fresh));

        var cutoff = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var deleted = await repo.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, deleted);
        var list = await repo.ListByAccountAsync("acct-1", 100);
        var remaining = Assert.Single(list);
        Assert.Equal(fresh.SourceUpdatedAt, remaining.SourceUpdatedAt);
    }

    [Fact]
    public async Task TryAppend_normalizes_timestamps_to_utc()
    {
        // Production stores timestamps as UTC ISO-8601 so lexical ordering
        // matches chronological ordering. Verify the round-trip preserves
        // chronological semantics even when the caller supplies a non-UTC
        // offset.
        await using var temp = new TempDatabase(seedNotifications: false);
        await SeedNotificationAsync(temp, "acct-1:thread-1", "acct-1");
        var repo = new NotificationEventRepository(temp.Factory);

        var withOffset = SampleEvent(sourceUpdatedAt: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(9)));
        Assert.True(await repo.TryAppendAsync(withOffset));

        var list = await repo.ListByAccountAsync("acct-1", 100);
        var single = Assert.Single(list);
        // Compared as UTC instants, the round-tripped value matches the input.
        Assert.Equal(withOffset.SourceUpdatedAt.UtcDateTime, single.SourceUpdatedAt.UtcDateTime);
    }
}
