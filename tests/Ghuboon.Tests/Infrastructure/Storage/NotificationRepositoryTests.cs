using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class NotificationRepositoryTests
{
    private static GitHubNotification SampleNotification(
        string id = "acct-1:thread-1",
        string accountId = "acct-1",
        string repo = "ochanuco/ghuboon",
        NotificationReason reason = NotificationReason.Mention,
        bool unread = true)
    {
        // GitHubNotification invariants require Id == "{AccountId}:{ThreadId}".
        var sep = id.IndexOf(':');
        var threadId = sep >= 0 ? id[(sep + 1)..] : id;
        return new GitHubNotification(
            Id: id,
            AccountId: accountId,
            ThreadId: threadId,
            RepositoryFullName: repo,
            Subject: new NotificationSubject("PullRequest", "Sample PR", "https://api.github.com/repos/ochanuco/ghuboon/pulls/1", "https://github.com/ochanuco/ghuboon/pull/1"),
            Reason: reason,
            Unread: unread,
            UpdatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            LastReadAt: null);
    }

    [Fact]
    public async Task Upsert_then_get_by_id_roundtrips_notification()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        var notif = SampleNotification();
        var syncedAt = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(notif, """{"id":"thread-1"}""", syncedAt);

        var read = await repo.GetByIdAsync(notif.Id);

        Assert.NotNull(read);
        Assert.Equal(notif.Id, read!.Id);
        Assert.Equal(notif.AccountId, read.AccountId);
        Assert.Equal(notif.RepositoryFullName, read.RepositoryFullName);
        Assert.Equal(notif.Subject, read.Subject);
        Assert.Equal(notif.Reason, read.Reason);
        Assert.Equal(notif.Unread, read.Unread);
        Assert.Equal(notif.UpdatedAt, read.UpdatedAt);
    }

    [Fact]
    public async Task Second_upsert_with_same_id_replaces_fields()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);
        var syncedAt = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(SampleNotification(), "{}", syncedAt);

        var updated = SampleNotification() with
        {
            Unread = false,
            LastReadAt = new DateTimeOffset(2026, 5, 9, 13, 0, 0, TimeSpan.Zero),
            Subject = new NotificationSubject("PullRequest", "Updated title", null, null),
        };

        await repo.UpsertAsync(updated, """{"id":"thread-1","title":"Updated"}""", syncedAt);

        var read = await repo.GetByIdAsync(updated.Id);
        Assert.NotNull(read);
        Assert.False(read!.Unread);
        Assert.Equal("Updated title", read.Subject.Title);
        Assert.Equal(updated.LastReadAt, read.LastReadAt);

        var list = await repo.ListByAccountAsync("acct-1");
        Assert.Single(list);
    }

    [Fact]
    public async Task ListByAccount_returns_only_matching_account_ordered_by_updated()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);
        var syncedAt = DateTimeOffset.UtcNow;

        var older = SampleNotification(id: "acct-1:1") with
        {
            UpdatedAt = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var newer = SampleNotification(id: "acct-1:2") with
        {
            UpdatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var otherAccount = SampleNotification(id: "acct-2:1", accountId: "acct-2");

        await repo.UpsertAsync(older, "{}", syncedAt);
        await repo.UpsertAsync(newer, "{}", syncedAt);
        await repo.UpsertAsync(otherAccount, "{}", syncedAt);

        var list = await repo.ListByAccountAsync("acct-1");
        Assert.Equal(2, list.Count);
        Assert.Equal("acct-1:2", list[0].Id);
        Assert.Equal("acct-1:1", list[1].Id);
    }

    [Fact]
    public async Task DeleteOlderThan_removes_only_stale_rows()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        var old = SampleNotification(id: "acct-1:old");
        var fresh = SampleNotification(id: "acct-1:fresh");

        var oldSyncedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var freshSyncedAt = new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero);

        await repo.UpsertAsync(old, "{}", oldSyncedAt);
        await repo.UpsertAsync(fresh, "{}", freshSyncedAt);

        var cutoff = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var deleted = await repo.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, deleted);
        Assert.Null(await repo.GetByIdAsync("acct-1:old"));
        Assert.NotNull(await repo.GetByIdAsync("acct-1:fresh"));
    }

    [Fact]
    public async Task GetById_returns_null_for_missing_row()
    {
        await using var temp = new TempDatabase(seedNotifications: false);
        var repo = new NotificationRepository(temp.Factory);

        Assert.Null(await repo.GetByIdAsync("missing"));
    }
}
