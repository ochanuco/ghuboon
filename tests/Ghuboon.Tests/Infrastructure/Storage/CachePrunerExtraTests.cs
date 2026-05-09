using System;
using System.Threading.Tasks;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Phase 15: extra integration tests for <see cref="CachePruner"/> driven against
/// a real <see cref="NotificationRepository"/> + SQLite, exercising boundary and
/// empty-DB conditions the existing stub-driven tests do not cover.
/// </summary>
public class CachePrunerExtraTests
{
    private static GitHubNotification Build(string id, string accountId = "primary") =>
        new(
            Id: id,
            AccountId: accountId,
            ThreadId: id,
            RepositoryFullName: "octo/repo",
            Subject: new NotificationSubject("Issue", "Title", null, null),
            Reason: NotificationReason.Mention,
            Unread: true,
            UpdatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            LastReadAt: null);

    [Fact]
    public async Task Prune_BoundaryAtCutoff_KeepsRowSyncedExactlyAtCutoff()
    {
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);
        var now = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var retention = TimeSpan.FromDays(30);
        var cutoff = now - retention;

        // synced_at exactly at the cutoff — current SQL is `synced_at < @cutoff`,
        // so this row must survive.
        await repo.UpsertAsync(Build("primary:edge"), "{}", cutoff);
        // synced_at strictly before the cutoff — must be deleted.
        await repo.UpsertAsync(Build("primary:stale"), "{}", cutoff - TimeSpan.FromTicks(1));
        // synced_at after the cutoff — must survive.
        await repo.UpsertAsync(Build("primary:fresh"), "{}", now);

        var pruner = new CachePruner(repo, retention, () => now);
        var deleted = await pruner.PruneAsync();

        Assert.Equal(1, deleted);
        Assert.NotNull(await repo.GetByIdAsync("primary:edge"));
        Assert.Null(await repo.GetByIdAsync("primary:stale"));
        Assert.NotNull(await repo.GetByIdAsync("primary:fresh"));
    }

    [Fact]
    public async Task Prune_EmptyDatabase_ReturnsZero_NoException()
    {
        await using var temp = new TempDatabase();
        var repo = new NotificationRepository(temp.Factory);
        var pruner = new CachePruner(repo, TimeSpan.FromDays(30),
            () => new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var deleted = await pruner.PruneAsync();

        Assert.Equal(0, deleted);
    }
}
