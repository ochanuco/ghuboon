using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class CachePrunerTests
{
    private sealed class StubNotificationRepository : INotificationRepository
    {
        public DateTimeOffset? LastCutoff { get; private set; }
        public int CallCount { get; private set; }
        public int Result { get; init; } = 7;

        public Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<GitHubNotification?>(null);

        public Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitHubNotification>>(Array.Empty<GitHubNotification>());

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            LastCutoff = cutoff;
            CallCount++;
            return Task.FromResult(Result);
        }

        public Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> SetReadStateAsync(string id, bool unread, DateTimeOffset readAt, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    [Fact]
    public async Task Default_retention_is_thirty_days()
    {
        var repo = new StubNotificationRepository();
        var now = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var pruner = new CachePruner(repo, CachePruner.DefaultRetention, () => now);

        Assert.Equal(TimeSpan.FromDays(30), pruner.Retention);

        var deleted = await pruner.PruneAsync();

        Assert.Equal(7, deleted);
        Assert.Equal(now - TimeSpan.FromDays(30), repo.LastCutoff);
        Assert.Equal(1, repo.CallCount);
    }

    [Fact]
    public async Task Custom_retention_is_passed_through_to_repository()
    {
        var repo = new StubNotificationRepository { Result = 0 };
        var now = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var retention = TimeSpan.FromDays(7);
        var pruner = new CachePruner(repo, retention, () => now);

        await pruner.PruneAsync();

        Assert.Equal(now - retention, repo.LastCutoff);
    }

    [Fact]
    public void Constructor_rejects_non_positive_retention()
    {
        var repo = new StubNotificationRepository();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CachePruner(repo, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CachePruner(repo, TimeSpan.FromSeconds(-1)));
    }
}
