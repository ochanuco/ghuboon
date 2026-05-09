using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class SyncStateRepositoryTests
{
    [Fact]
    public async Task Upsert_then_get_returns_persisted_state()
    {
        await using var temp = new TempDatabase();
        var repo = new SyncStateRepository(temp.Factory);

        var state = new SyncState(
            AccountId: "acct-1",
            LastSyncAt: new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            LastSuccessfulSyncAt: new DateTimeOffset(2026, 5, 9, 11, 55, 0, TimeSpan.Zero),
            NotificationsEtag: "W/\"etag-1\"",
            RateLimitRemaining: 4900,
            RateLimitResetAt: new DateTimeOffset(2026, 5, 9, 13, 0, 0, TimeSpan.Zero));

        await repo.UpsertAsync(state);

        var read = await repo.GetAsync("acct-1");

        Assert.NotNull(read);
        Assert.Equal(state, read);
    }

    [Fact]
    public async Task Upsert_overwrites_existing_state()
    {
        await using var temp = new TempDatabase();
        var repo = new SyncStateRepository(temp.Factory);

        await repo.UpsertAsync(SyncState.Empty("acct-1"));

        var updated = new SyncState(
            "acct-1",
            LastSyncAt: new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            LastSuccessfulSyncAt: null,
            NotificationsEtag: "W/\"new-etag\"",
            RateLimitRemaining: 100,
            RateLimitResetAt: null);

        await repo.UpsertAsync(updated);

        var read = await repo.GetAsync("acct-1");
        Assert.NotNull(read);
        Assert.Equal("W/\"new-etag\"", read!.NotificationsEtag);
        Assert.Equal(100, read.RateLimitRemaining);
        Assert.Null(read.LastSuccessfulSyncAt);
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_account()
    {
        await using var temp = new TempDatabase();
        var repo = new SyncStateRepository(temp.Factory);

        Assert.Null(await repo.GetAsync("nope"));
    }
}
