using System;
using System.Threading.Tasks;
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
    public async Task Upsert_OverwritesLastReadAt_WithIncomingValue_DocumentsCurrentBehavior()
    {
        // Phase 15 finding: NotificationRepository.Upsert uses
        // `last_read_at = excluded.last_read_at` so a sync that re-upserts a
        // remotely-still-unread notification (LastReadAt=null) clobbers any
        // local read marker. This is a known gap — see notes / issue tracker.
        // The test pins down current behavior to avoid silent regressions.
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);

        var localReadAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        // 1) Locally mark as read.
        await repo.UpsertAsync(Build(unread: false, lastReadAt: localReadAt), "{}", DateTimeOffset.UtcNow);
        // 2) Remote sync upserts the same row with LastReadAt=null because GitHub
        //    has not yet propagated the read-state.
        await repo.UpsertAsync(Build(unread: true, lastReadAt: null), "{}", DateTimeOffset.UtcNow);

        var read = await repo.GetByIdAsync("primary:1");
        Assert.NotNull(read);
        // TODO(local-read-preservation): If we add a "preserve last_read_at when
        // remote returns null" rule, flip this assertion. Today, the second
        // upsert replaces last_read_at with null and Unread with true.
        Assert.True(read!.Unread);
        Assert.Null(read.LastReadAt);
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
