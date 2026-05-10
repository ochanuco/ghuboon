using System;
using System.Threading.Tasks;
using Dapper;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Phase 15: extra tests for <see cref="NotificationRepository"/> documenting
/// roundtrip semantics relevant to the sync + read-state pipeline.
/// </summary>
public class NotificationRepositoryExtraTests
{
    private static GitHubNotification Build(
        string id = "primary:1",
        string accountId = "primary",
        bool unread = true,
        DateTimeOffset? lastReadAt = null) =>
        new(
            Id: id,
            AccountId: accountId,
            ThreadId: id.Replace("primary:", string.Empty, StringComparison.Ordinal),
            RepositoryFullName: "octo/repo",
            Subject: new NotificationSubject("PullRequest", "Title", null, "https://github.com/octo/repo/pull/1"),
            Reason: NotificationReason.Mention,
            Unread: unread,
            UpdatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            LastReadAt: lastReadAt);

    [Fact]
    public async Task Upsert_PersistsLastReadAt_And_Roundtrips()
    {
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);

        var readAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        await repo.UpsertAsync(Build(unread: false, lastReadAt: readAt), "{}", DateTimeOffset.UtcNow);

        var read = await repo.GetByIdAsync("primary:1");
        Assert.NotNull(read);
        Assert.False(read!.Unread);
        Assert.Equal(readAt, read.LastReadAt);
    }

    [Fact]
    public async Task Upsert_PreservesLocalLastReadAt_WhenIncomingIsNull()
    {
        // Issue #25: NotificationRepository.Upsert previously used
        // `last_read_at = excluded.last_read_at`, so a sync that re-upserted a
        // remote-still-unread notification (LastReadAt = null) clobbered any
        // local read marker. The fix uses
        // `last_read_at = COALESCE(excluded.last_read_at, last_read_at)` so
        // an incoming null leaves the existing column value alone.
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        var localReadAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        // 1) Locally mark as read.
        await repo.UpsertAsync(Build(unread: false, lastReadAt: localReadAt), "{}", DateTimeOffset.UtcNow);
        // 2) Remote sync upserts the same row with LastReadAt = null because
        //    GitHub has not yet propagated the read-state.
        await repo.UpsertAsync(Build(unread: true, lastReadAt: null), "{}", DateTimeOffset.UtcNow);

        var read = await repo.GetByIdAsync("primary:1");
        Assert.NotNull(read);
        // Unread flag still tracks the incoming value (server is authoritative
        // for "is this thread currently unread") — only LastReadAt is preserved.
        Assert.True(read!.Unread);
        Assert.Equal(localReadAt, read.LastReadAt);
    }

    [Fact]
    public async Task Upsert_AppliesIncomingLastReadAt_WhenNotNull()
    {
        // Issue #25: when the incoming value is non-null, COALESCE returns
        // the incoming side, so the column is updated as before.
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        var firstReadAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var secondReadAt = new DateTimeOffset(2026, 5, 9, 13, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(Build(unread: false, lastReadAt: firstReadAt), "{}", DateTimeOffset.UtcNow);
        await repo.UpsertAsync(Build(unread: false, lastReadAt: secondReadAt), "{}", DateTimeOffset.UtcNow);

        var read = await repo.GetByIdAsync("primary:1");
        Assert.NotNull(read);
        Assert.Equal(secondReadAt, read!.LastReadAt);
    }

    [Fact]
    public async Task Upsert_NormalizesUpdatedAtToUtc()
    {
        // Issue #12: timestamps should be persisted UTC so the round-trip
        // preserves absolute time and string-compare ranges line up with
        // chronological order. Verify that an UpdatedAt with a non-UTC offset
        // is stored as a UTC ISO-8601 string ending in "Z" or "+00:00".
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        var nonUtc = new DateTimeOffset(2026, 5, 9, 21, 0, 0, TimeSpan.FromHours(9));
        var notif = Build() with
        {
            UpdatedAt = nonUtc,
            LastReadAt = nonUtc,
        };

        await repo.UpsertAsync(notif, "{}", nonUtc);

        // Read raw column directly via a connection so we can inspect the
        // string form, not the DateTimeOffset round-trip (which would be
        // equal regardless of offset).
        await using var connection = await temp.Factory.OpenAsync();
        var stored = await connection.QuerySingleAsync<(string UpdatedAt, string LastReadAt, string SyncedAt)>(
            """
            SELECT updated_at AS UpdatedAt, last_read_at AS LastReadAt, synced_at AS SyncedAt
            FROM notifications WHERE id = @id;
            """,
            new { id = notif.Id });

        Assert.EndsWith("+00:00", stored.UpdatedAt, StringComparison.Ordinal);
        Assert.EndsWith("+00:00", stored.LastReadAt, StringComparison.Ordinal);
        Assert.EndsWith("+00:00", stored.SyncedAt, StringComparison.Ordinal);

        // And the round-tripped value still equals the original instant.
        var read = await repo.GetByIdAsync(notif.Id);
        Assert.NotNull(read);
        Assert.Equal(nonUtc.UtcDateTime, read!.UpdatedAt.UtcDateTime);
        Assert.Equal(nonUtc.UtcDateTime, read.LastReadAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task DeleteOlderThan_OnEmptyTable_ReturnsZero()
    {
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);

        var deleted = await repo.DeleteOlderThanAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ListByAccount_OnUnknownAccount_ReturnsEmpty()
    {
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);

        await repo.UpsertAsync(Build(accountId: "primary"), "{}", DateTimeOffset.UtcNow);

        var list = await repo.ListByAccountAsync("nobody");
        Assert.Empty(list);
    }
}
