using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub;
using Ghuboon.Infrastructure.Sync;

namespace Ghuboon.Tests.Infrastructure.Sync;

public class NotificationSyncServiceTests
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

    private static GitHubNotification BuildNotification(
        string threadId,
        NotificationReason reason = NotificationReason.Mention,
        string accountId = AccountId,
        string repo = "octo/hello",
        bool unread = true,
        DateTimeOffset? updatedAt = null)
    {
        return new GitHubNotification(
            Id: $"{accountId}:{threadId}",
            AccountId: accountId,
            ThreadId: threadId,
            RepositoryFullName: repo,
            Subject: new NotificationSubject("PullRequest", $"Title {threadId}", $"https://api.github.com/repos/{repo}/pulls/{threadId}", $"https://github.com/{repo}/pull/{threadId}"),
            Reason: reason,
            Unread: unread,
            UpdatedAt: updatedAt ?? new DateTimeOffset(2026, 5, 9, 11, 0, 0, TimeSpan.Zero),
            LastReadAt: null);
    }

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

        public NotificationSyncService BuildService(TimeSpan? period = null) =>
            new(Credentials, Accounts, Repositories, Notifications, Events, SyncStates, Api, Clock,
                logger: null, defaultAccountId: AccountId, period: period);

        public async Task SeedAccountAndPatAsync(bool seedPat = true)
        {
            await Accounts.UpsertAsync(SampleAccount());
            if (seedPat)
            {
                await Credentials.SetAsync(CredentialKey, Pat);
            }
        }
    }

    [Fact]
    public async Task SyncAsync_FirstSync_PersistsAllAsNewWithoutFiringNotifications()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var notifs = new[]
        {
            BuildNotification("1", NotificationReason.Review),
            BuildNotification("2", NotificationReason.Mention),
            BuildNotification("3", NotificationReason.Comment),
        };
        h.Api.EnqueueList(new NotificationsResponse(notifs, "\"etag-1\"", new RateLimitInfo(4990, null), NotModified: false));

        var service = h.BuildService();
        var fired = new List<NewNotificationsEvent>();
        service.NewNotifications += (_, e) => fired.Add(e);

        var result = await service.SyncAsync(AccountId);

        Assert.True(result.Success);
        Assert.Equal(3, result.FetchedCount);
        Assert.Equal(3, result.NewCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(fired); // ADR-021: no startup notifications

        // ETag is now persisted for the next sync.
        var state = h.SyncStates.Peek(AccountId);
        Assert.NotNull(state);
        Assert.Equal("\"etag-1\"", state!.NotificationsEtag);
        Assert.NotNull(state.LastSuccessfulSyncAt);

        // All three rows are upserted in the cache.
        Assert.Equal(3, h.Notifications.Count);
    }

    [Fact]
    public async Task SyncAsync_SecondSync_DetectsTrulyNewItems()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var first = BuildNotification("1", NotificationReason.Review);
        h.Api.EnqueueList(new NotificationsResponse(new[] { first }, "\"e1\"", RateLimitInfo.Empty, NotModified: false));

        // The second notification's UpdatedAt must be > the
        // LastSuccessfulSyncAt persisted by the first sync (which is the
        // harness clock). The "old-event suppression" gate
        // (notification.UpdatedAt > state.LastSuccessfulSyncAt) is what
        // keeps banners quiet for zombie threads upstream surfaces with
        // stale timestamps; the test's job is to exercise the truly-new
        // path, so set updatedAt explicitly.
        var second = BuildNotification("2", NotificationReason.Mention,
            updatedAt: h.Clock.UtcNow.AddMinutes(1));
        h.Api.EnqueueList(new NotificationsResponse(new[] { first, second }, "\"e2\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        var fired = new List<NewNotificationsEvent>();
        service.NewNotifications += (_, e) => fired.Add(e);

        await service.SyncAsync(AccountId);
        var second_result = await service.SyncAsync(AccountId);

        Assert.True(second_result.Success);
        Assert.Equal(2, second_result.FetchedCount);
        Assert.Equal(1, second_result.NewCount);
        Assert.Equal(1, second_result.UpdatedCount);

        var ev = Assert.Single(fired);
        Assert.Equal(AccountId, ev.AccountId);
        var newItem = Assert.Single(ev.HighPriorityNew);
        Assert.Equal("acct-1:2", newItem.Id);
    }

    [Fact]
    public async Task SyncAsync_NotModified_LeavesCacheIntact()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var existing = BuildNotification("1", NotificationReason.Mention);
        h.Notifications.SeedWithSyncedAt(existing, h.Clock.UtcNow);
        await h.SyncStates.UpsertAsync(SyncState.Empty(AccountId) with { NotificationsEtag = "\"prev\"" });

        h.Api.EnqueueList(new NotificationsResponse(Array.Empty<GitHubNotification>(), "\"prev\"", new RateLimitInfo(4500, null), NotModified: true));

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.True(result.Success);
        Assert.Equal(0, result.FetchedCount);
        Assert.Equal(0, result.NewCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, h.Notifications.UpsertCallCount);
        Assert.Equal(1, h.Notifications.Count); // existing row untouched

        var sentRequest = Assert.Single(h.Api.Requests);
        Assert.Equal("\"prev\"", sentRequest.IfNoneMatch);

        // 304 still updates the rate-limit/last-sync metadata.
        var state = h.SyncStates.Peek(AccountId);
        Assert.NotNull(state);
        Assert.Equal(4500, state!.RateLimitRemaining);
    }

    [Fact]
    public async Task SyncAsync_HighPriorityOnlyForNotifications()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        // Seed prior etag so the second-sync NewNotifications gating kicks in.
        await h.SyncStates.UpsertAsync(SyncState.Empty(AccountId) with { NotificationsEtag = "\"prev\"" });

        var items = new[]
        {
            BuildNotification("1", NotificationReason.Review),
            BuildNotification("2", NotificationReason.Mention),
            BuildNotification("3", NotificationReason.TeamMention),
            BuildNotification("4", NotificationReason.Assigned),
            BuildNotification("5", NotificationReason.Comment),
            BuildNotification("6", NotificationReason.MyPr),
            BuildNotification("7", NotificationReason.Watching),
            BuildNotification("8", NotificationReason.Unknown),
        };
        h.Api.EnqueueList(new NotificationsResponse(items, "\"e2\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        var fired = new List<NewNotificationsEvent>();
        service.NewNotifications += (_, e) => fired.Add(e);

        await service.SyncAsync(AccountId);

        var ev = Assert.Single(fired);
        // Allow-list: Review/Mention/TeamMention/Assigned/MyPr/State/Comment.
        // The set was widened beyond ADR-021's original four after the user
        // wanted authored-PR activity (MyPr), state transitions (State),
        // and comment threads (Comment) to also fire OS banners.
        Assert.Equal(6, ev.HighPriorityNew.Count);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.Review);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.Mention);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.TeamMention);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.Assigned);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.MyPr);
        Assert.Contains(ev.HighPriorityNew, n => n.Reason == NotificationReason.Comment);
    }

    [Fact]
    public async Task SyncAsync_AccountMissing_ReturnsAuthError()
    {
        var h = new Harness();
        // No account seeded.

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCategory.Auth, result.Error);
        Assert.Equal("Account not configured", result.Message);
        Assert.Empty(h.Api.Requests);
    }

    [Fact]
    public async Task SyncAsync_PatMissing_ReturnsAuthError()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync(seedPat: false);

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCategory.Auth, result.Error);
        Assert.Empty(h.Api.Requests);
    }

    [Fact]
    public async Task SyncAsync_ApiAuthError_PreservesCachedData()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var existing = BuildNotification("1", NotificationReason.Mention);
        h.Notifications.SeedWithSyncedAt(existing, h.Clock.UtcNow);

        h.Api.ListThrows = _ => new GitHubApiException(ErrorCategory.Auth, "Token invalid", 401);

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCategory.Auth, result.Error);
        Assert.Equal(1, h.Notifications.Count); // cached data preserved

        // last_sync_at advanced but last_successful_sync_at did not.
        var state = h.SyncStates.Peek(AccountId);
        Assert.NotNull(state);
        Assert.NotNull(state!.LastSyncAt);
        Assert.Null(state.LastSuccessfulSyncAt);
    }

    [Fact]
    public async Task SyncAsync_NetworkError_PreservesCachedDataAndReturnsNetworkError()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var existing = BuildNotification("1", NotificationReason.Mention);
        h.Notifications.SeedWithSyncedAt(existing, h.Clock.UtcNow);

        h.Api.ListThrows = _ => new HttpRequestException("dns failure");

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCategory.Network, result.Error);
        Assert.Equal(1, h.Notifications.Count);
    }

    [Fact]
    public async Task SyncAsync_RateLimit_ReturnsRateLimitErrorAndCapturesReset()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        h.Api.ListThrows = _ => new GitHubApiException(ErrorCategory.RateLimit, "rate limited", 403);

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCategory.RateLimit, result.Error);

        // Failed-sync bookkeeping persisted (last_sync_at set).
        var state = h.SyncStates.Peek(AccountId);
        Assert.NotNull(state);
        Assert.NotNull(state!.LastSyncAt);
    }

    [Fact]
    public async Task SyncAsync_PrunesOldNotifications()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        // Seed an old row that is older than 30 days.
        var stale = BuildNotification("stale", NotificationReason.Comment);
        h.Notifications.SeedWithSyncedAt(stale, h.Clock.UtcNow - TimeSpan.FromDays(45));

        var fresh = BuildNotification("fresh", NotificationReason.Comment);
        h.Api.EnqueueList(new NotificationsResponse(new[] { fresh }, "\"e1\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        var result = await service.SyncAsync(AccountId);

        Assert.True(result.Success);
        Assert.True(h.Notifications.DeleteCallCount >= 1);
        Assert.Null(await h.Notifications.GetByIdAsync($"{AccountId}:stale"));
        Assert.NotNull(await h.Notifications.GetByIdAsync($"{AccountId}:fresh"));
    }

    [Fact]
    public async Task SyncAsync_RaisesProgressEventsInOrder()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        h.Api.EnqueueList(new NotificationsResponse(
            new[] { BuildNotification("1", NotificationReason.Comment) },
            "\"e1\"",
            RateLimitInfo.Empty,
            NotModified: false));

        var stages = new List<SyncStage>();
        var service = h.BuildService();
        service.Progress += (_, e) => stages.Add(e.Stage);

        await service.SyncAsync(AccountId);

        Assert.Equal(SyncStage.Starting, stages[0]);
        Assert.Contains(SyncStage.Fetching, stages);
        Assert.Contains(SyncStage.Persisting, stages);
        Assert.Contains(SyncStage.Pruning, stages);
        Assert.Equal(SyncStage.Completed, stages[^1]);
    }

    [Theory]
    [InlineData("octo")]
    [InlineData("/octo")]
    [InlineData("octo/")]
    [InlineData("/")]
    [InlineData("octo/hello/extra")]
    [InlineData("")]
    public void SplitFullName_throws_for_malformed_inputs(string fullName)
    {
        // Issue #16: SplitFullName must reject anything that is not exactly
        // "owner/name" so we never silently construct a RepositoryRef with
        // duplicated or partial values.
        var ex = Assert.Throws<ArgumentException>(() =>
            NotificationSyncService.SplitFullName(fullName));
        Assert.Contains(fullName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitFullName_throws_for_null_input()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NotificationSyncService.SplitFullName(null!));
    }

    [Fact]
    public void SplitFullName_returns_owner_and_name_for_valid_input()
    {
        var (owner, name) = NotificationSyncService.SplitFullName("octo/hello");
        Assert.Equal("octo", owner);
        Assert.Equal("hello", name);
    }

    // Note: a former integration test
    // (SyncAsync_SkipsNotificationWithMalformedRepositoryFullName) was removed
    // as part of Wave 6 integration. After Lane N (issue #6) hardened
    // GitHubNotification's invariants to require RepositoryFullName to be
    // exactly "owner/name" with both sides non-empty, it is impossible to
    // construct a GitHubNotification carrying a malformed repository name in a
    // test, so the integration scenario is no longer reachable from in-process
    // fakes. The malformed-repo skip path in NotificationSyncService remains
    // as defence-in-depth for upstream drift, and SplitFullName's behaviour is
    // covered directly by the unit tests above.

    [Fact]
    public async Task SyncAsync_RepositoryUpsertedOncePerUniqueRepo()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var items = new[]
        {
            BuildNotification("1", repo: "octo/a"),
            BuildNotification("2", repo: "octo/a"),
            BuildNotification("3", repo: "octo/b"),
        };
        h.Api.EnqueueList(new NotificationsResponse(items, "\"e1\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        await service.SyncAsync(AccountId);

        Assert.Equal(2, h.Repositories.UpsertCallCount);
        var listed = await h.Repositories.ListByAccountAsync(AccountId);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Start_TriggersPeriodicSync()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        // Drive at least two ticks. PeriodicTimer fires after the first interval, so
        // a 100ms period is enough to expect 2+ ticks within ~600ms.
        var firstItems = new[] { BuildNotification("1", NotificationReason.Comment) };
        h.Api.EnqueueList(new NotificationsResponse(firstItems, "\"e1\"", RateLimitInfo.Empty, NotModified: false));
        h.Api.EnqueueList(new NotificationsResponse(firstItems, "\"e1\"", RateLimitInfo.Empty, NotModified: true));

        var service = h.BuildService(period: TimeSpan.FromMilliseconds(100));

        Assert.False(service.IsRunning);
        service.Start();
        Assert.True(service.IsRunning);

        // Wait long enough for at least 2 ticks.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && h.Api.Requests.Count < 2)
        {
            await Task.Delay(50);
        }

        service.Stop();
        Assert.False(service.IsRunning);
        Assert.True(h.Api.Requests.Count >= 2, $"expected at least 2 sync requests, got {h.Api.Requests.Count}");
    }

    [Fact]
    public void Start_NoOpIfAlreadyRunning()
    {
        var h = new Harness();
        var service = h.BuildService(period: TimeSpan.FromSeconds(5));

        service.Start();
        var firstIsRunning = service.IsRunning;
        service.Start(); // should be a no-op
        Assert.True(firstIsRunning);
        Assert.True(service.IsRunning);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task SyncAsync_AppendsOneEventPerUpsert()
    {
        // Each notification observed during a sync should produce exactly one
        // event row alongside the latest-state upsert. Re-running the same
        // sync against the same upstream timestamp must dedup (no second row).
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var notifs = new[]
        {
            BuildNotification("1", NotificationReason.Review),
            BuildNotification("2", NotificationReason.Mention),
            BuildNotification("3", NotificationReason.Comment),
        };
        h.Api.EnqueueList(new NotificationsResponse(notifs, "\"e1\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        await service.SyncAsync(AccountId);

        Assert.Equal(3, h.Events.Count);
        var listed = await h.Events.ListByAccountAsync(AccountId, 100);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, e => Assert.Equal(AccountId, e.AccountId));
    }

    [Fact]
    public async Task SyncAsync_SameSourceUpdatedAt_DedupsEventRow()
    {
        // Two syncs at the same upstream updated_at must collapse to one
        // event row (the unique index does the work in the storage layer; the
        // sync should not work around it). The notifications upsert still
        // happens — only the event-log row is dedup'd.
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var fixedAt = new DateTimeOffset(2026, 5, 9, 11, 0, 0, TimeSpan.Zero);
        var notif = BuildNotification("1", NotificationReason.Mention, updatedAt: fixedAt);

        h.Api.EnqueueList(new NotificationsResponse(new[] { notif }, "\"e1\"", RateLimitInfo.Empty, NotModified: false));
        h.Api.EnqueueList(new NotificationsResponse(new[] { notif }, "\"e1\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        await service.SyncAsync(AccountId);
        // Advance the clock between syncs so observed_at would differ if it
        // was the dedup key — the unique index uses source_updated_at instead.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await service.SyncAsync(AccountId);

        Assert.Equal(1, h.Events.Count);
        Assert.True(h.Events.AppendCallCount >= 2,
            $"expected two append attempts, observed {h.Events.AppendCallCount}");
        Assert.True(h.Events.DedupedCount >= 1,
            $"expected at least one dedup, observed {h.Events.DedupedCount}");
    }

    [Fact]
    public async Task SyncAsync_MultipleUpdatesProduceMultipleEventRows()
    {
        // Open -> Draft -> Open over multiple sync windows must produce one
        // event row per transition. The thread id is constant; only
        // source_updated_at advances, so the event log's unique index lets
        // every observation through.
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var t0 = new DateTimeOffset(2026, 5, 9, 11, 0, 0, TimeSpan.Zero);
        var first = BuildNotification("1", NotificationReason.Review, updatedAt: t0);
        var second = BuildNotification("1", NotificationReason.Review, updatedAt: t0.AddMinutes(10));
        var third = BuildNotification("1", NotificationReason.Review, updatedAt: t0.AddMinutes(20));

        h.Api.EnqueueList(new NotificationsResponse(new[] { first }, "\"e1\"", RateLimitInfo.Empty, NotModified: false));
        h.Api.EnqueueList(new NotificationsResponse(new[] { second }, "\"e2\"", RateLimitInfo.Empty, NotModified: false));
        h.Api.EnqueueList(new NotificationsResponse(new[] { third }, "\"e3\"", RateLimitInfo.Empty, NotModified: false));

        var service = h.BuildService();
        await service.SyncAsync(AccountId);
        await service.SyncAsync(AccountId);
        await service.SyncAsync(AccountId);

        Assert.Equal(3, h.Events.Count);
        var listed = await h.Events.ListByAccountAsync(AccountId, 100);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, e => Assert.Equal($"{AccountId}:1", e.NotificationId));
    }

    [Fact]
    public async Task Stop_StopsPeriodicLoop()
    {
        var h = new Harness();
        await h.SeedAccountAndPatAsync();

        var service = h.BuildService(period: TimeSpan.FromMilliseconds(50));
        service.Start();

        await Task.Delay(150);
        var requestsBeforeStop = h.Api.Requests.Count;

        service.Stop();
        Assert.False(service.IsRunning);

        // After Stop, no new requests should accumulate.
        await Task.Delay(200);
        var requestsAfterStop = h.Api.Requests.Count;

        Assert.True(requestsAfterStop - requestsBeforeStop <= 1,
            $"expected loop to stop, before={requestsBeforeStop} after={requestsAfterStop}");
    }
}
