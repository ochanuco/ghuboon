using System;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub;
using Ghuboon.Infrastructure.Sync;

namespace Ghuboon.Tests.Infrastructure.Sync;

/// <summary>
/// Phase 15: cancellation + concurrency edge cases for <see cref="NotificationSyncService"/>.
/// </summary>
public class NotificationSyncServiceExtraTests
{
    private const string AccountId = "acct-1";
    private const string CredentialKey = "ghuboon.acct-1.pat";
    private const string Pat = "ghp_" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123";

    private static Account SampleAccount() => new(
        Id: AccountId,
        Host: "github.com",
        Login: "octocat",
        CredentialKey: CredentialKey,
        CreatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        LastValidatedAt: null);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeCredentialStore Credentials { get; } = new();
        public InMemoryAccountRepository Accounts { get; } = new();
        public InMemoryRepositoryRepository Repositories { get; } = new();
        public InMemoryNotificationRepository Notifications { get; } = new();
        public InMemoryNotificationEventRepository Events { get; } = new();
        public InMemorySyncStateRepository SyncStates { get; } = new();
        public FakeGitHubApiClient Api { get; } = new();

        public NotificationSyncService BuildService() =>
            new(Credentials, Accounts, Repositories, Notifications, Events, SyncStates, Api, Clock,
                logger: null, defaultAccountId: AccountId, period: null);

        public async Task SeedAsync()
        {
            await Accounts.UpsertAsync(SampleAccount());
            await Credentials.SetAsync(CredentialKey, Pat);
        }
    }

    [Fact]
    public async Task SyncAsync_PreCancelledToken_DoesNotThrow_BecausePreFlightLookupsIgnoreToken()
    {
        // Phase 15 finding: a pre-cancelled token does NOT short-circuit the sync.
        // The first awaitable call (`accountRepository.GetByIdAsync`) is the only
        // pre-API checkpoint, and the in-memory test fakes do not observe the
        // CancellationToken. The real <see cref="Storage.AccountRepository"/> does
        // forward `ct` into Dapper, so production code may surface OCE; in tests
        // we therefore only verify the call does not crash. The downstream API
        // call (`ListNotificationsAsync`) will then proceed normally (or throw
        // OCE if its impl honors ct — see the mid-fetch test below).
        var h = new Harness();
        await h.SeedAsync();
        h.Api.EnqueueList(new NotificationsResponse(
            Array.Empty<GitHubNotification>(), "\"e\"", RateLimitInfo.Empty, NotModified: true));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = h.BuildService();
        var ex = await Record.ExceptionAsync(() => service.SyncAsync(AccountId, cts.Token));
        // Either OCE (if any layer observes ct) or null (if no layer does); both
        // are acceptable and current behavior is "no throw".
        Assert.True(ex is null || ex is OperationCanceledException,
            $"unexpected exception type: {ex?.GetType().FullName}");
    }

    [Fact]
    public async Task SyncAsync_CancelMidFetch_PropagatesOperationCanceled()
    {
        // Cancellation during the API call surfaces as OCE without crashing
        // bookkeeping. We pin the API to throw OCE directly.
        var h = new Harness();
        await h.SeedAsync();

        using var cts = new CancellationTokenSource();
        h.Api.ListThrows = _ =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };

        var service = h.BuildService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SyncAsync(AccountId, cts.Token));
    }

    [Fact]
    public async Task SyncAsync_TwoConsecutiveCalls_BothComplete_NoLockingErrors()
    {
        // Phase 15: there is no internal serialization in NotificationSyncService —
        // two awaited calls run sequentially and both finish cleanly. (We're not
        // asserting that overlapping calls coalesce; the service does not advertise
        // that contract.) This test guards against future regressions that might
        // accidentally introduce a re-entrancy crash.
        var h = new Harness();
        await h.SeedAsync();

        h.Api.EnqueueList(req => new NotificationsResponse(
            Array.Empty<GitHubNotification>(), "\"e1\"", RateLimitInfo.Empty, NotModified: false));
        h.Api.EnqueueList(req => new NotificationsResponse(
            Array.Empty<GitHubNotification>(), "\"e1\"", RateLimitInfo.Empty, NotModified: true));

        var service = h.BuildService();

        var first = await service.SyncAsync(AccountId);
        var second = await service.SyncAsync(AccountId);

        Assert.True(first.Success);
        Assert.True(second.Success);
        // Two API calls observed.
        Assert.Equal(2, h.Api.Requests.Count);
    }
}
