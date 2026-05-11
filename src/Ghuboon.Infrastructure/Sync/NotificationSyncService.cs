using System.Net.Http;
using System.Text.Json;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.GitHub;
using Serilog;

namespace Ghuboon.Infrastructure.Sync;

/// <summary>
/// Default <see cref="INotificationSyncService"/> implementation (Phase 7).
/// <para>
/// Coordinates: account lookup, PAT retrieval, ETag-conditional fetch, upsert into
/// the local cache, repository upsert, sync-state bookkeeping, cache pruning
/// (ADR-022, 30 days), and high-priority new-item eventing (ADR-021).
/// </para>
/// <para>
/// Background loop fires every <see cref="DefaultPeriod"/> (60 seconds —
/// GitHub's recommended floor for etag-conditional polling).
/// Tests may override the period via the constructor for fast iteration.
/// </para>
/// </summary>
public sealed class NotificationSyncService : INotificationSyncService, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Default 60-second periodic sync interval. GitHub's
    /// <c>X-Poll-Interval</c> header guidance for the notifications
    /// endpoint is 60 seconds, and conditional requests that 304 don't
    /// count against the rate-limit budget — so we can poll at the
    /// recommended floor without burning quota. Earlier 5-minute
    /// interval (ADR-020) added 2-3 minute perceived lag on every new
    /// event; reverting to the GitHub-recommended cadence trades that
    /// lag for cheap conditional GETs.
    /// </summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(60);

    /// <summary>30-day cache retention (ADR-022).</summary>
    public static readonly TimeSpan CacheRetention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ICredentialStore _credentialStore;
    private readonly IAccountRepository _accountRepository;
    private readonly IRepositoryRepository _repositoryRepository;
    private readonly INotificationRepository _notificationRepository;
    private readonly INotificationEventRepository _eventRepository;
    private readonly ISyncStateRepository _syncStateRepository;
    private readonly IGitHubApiClient _apiClient;
    private readonly IClock _clock;
    private readonly ILogger? _logger;
    private readonly string? _defaultAccountId;
    private readonly TimeSpan _period;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _backgroundCts;
    private Task? _backgroundTask;

    public NotificationSyncService(
        ICredentialStore credentialStore,
        IAccountRepository accountRepository,
        IRepositoryRepository repositoryRepository,
        INotificationRepository notificationRepository,
        INotificationEventRepository eventRepository,
        ISyncStateRepository syncStateRepository,
        IGitHubApiClient apiClient,
        IClock clock,
        ILogger? logger = null,
        string? defaultAccountId = null,
        TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(accountRepository);
        ArgumentNullException.ThrowIfNull(repositoryRepository);
        ArgumentNullException.ThrowIfNull(notificationRepository);
        ArgumentNullException.ThrowIfNull(eventRepository);
        ArgumentNullException.ThrowIfNull(syncStateRepository);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(clock);

        _credentialStore = credentialStore;
        _accountRepository = accountRepository;
        _repositoryRepository = repositoryRepository;
        _notificationRepository = notificationRepository;
        _eventRepository = eventRepository;
        _syncStateRepository = syncStateRepository;
        _apiClient = apiClient;
        _clock = clock;
        _logger = logger?.ForContext<NotificationSyncService>();
        _defaultAccountId = defaultAccountId;
        _period = period ?? DefaultPeriod;
    }

    public event EventHandler<SyncProgressEvent>? Progress;
    public event EventHandler<NewNotificationsEvent>? NewNotifications;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _backgroundTask is { IsCompleted: false };
            }
        }
    }

    public async Task<SyncResult> SyncAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        RaiseProgress(accountId, SyncStage.Starting, null);

        // 1. Resolve account.
        Account? account;
        try
        {
            account = await _accountRepository.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Sync failed: account lookup error for {AccountId}", accountId);
            var dbResult = new SyncResult(false, 0, 0, 0, ErrorCategory.Database, "Failed to load account from cache.", null);
            RaiseProgress(accountId, SyncStage.Failed, dbResult);
            return dbResult;
        }

        if (account is null)
        {
            var result = new SyncResult(false, 0, 0, 0, ErrorCategory.Auth, "Account not configured", null);
            RaiseProgress(accountId, SyncStage.Failed, result);
            return result;
        }

        // 2. Resolve PAT.
        string? pat;
        try
        {
            pat = await _credentialStore.GetAsync(account.CredentialKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Sync failed: credential lookup error");
            var credResult = new SyncResult(false, 0, 0, 0, ErrorCategory.Auth, "Failed to read credential store.", null);
            RaiseProgress(accountId, SyncStage.Failed, credResult);
            return credResult;
        }

        if (string.IsNullOrEmpty(pat))
        {
            var result = new SyncResult(false, 0, 0, 0, ErrorCategory.Auth, "No PAT stored for account", null);
            RaiseProgress(accountId, SyncStage.Failed, result);
            return result;
        }

        // 3. Load sync state (or empty).
        SyncState state;
        try
        {
            state = await _syncStateRepository.GetAsync(accountId, ct).ConfigureAwait(false)
                    ?? SyncState.Empty(accountId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Sync failed: sync state lookup error");
            var dbResult = new SyncResult(false, 0, 0, 0, ErrorCategory.Database, "Failed to load sync state.", null);
            RaiseProgress(accountId, SyncStage.Failed, dbResult);
            return dbResult;
        }

        // "Have we ever synced before?" — used to suppress an avalanche
        // of OS banners on initial install where every cached unread
        // thread looks brand-new. We OR two signals:
        //   * LastSuccessfulSyncAt: set on every successful sync.
        //   * NotificationsEtag: set when GitHub returns an etag.
        // Either alone would have a gap. GitHub sometimes returns an
        // empty etag for the notifications endpoint, so a long-running
        // session with valid LastSuccessfulSyncAt but an empty etag
        // would otherwise mis-classify as "never synced" and silence
        // its banners (the user-reported "TL has the row but no banner
        // for 1–2 minutes" lag).
        var hasPriorSync = state.LastSuccessfulSyncAt is not null
            || !string.IsNullOrEmpty(state.NotificationsEtag);

        // 4. Call GitHub API.
        RaiseProgress(accountId, SyncStage.Fetching, null);

        NotificationsResponse response;
        try
        {
            response = await _apiClient.ListNotificationsAsync(
                pat,
                new NotificationsRequest(IfNoneMatch: state.NotificationsEtag, AccountId: accountId),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubApiException ex)
        {
            return await HandleApiFailureAsync(accountId, state, ex.Category, ex.Message, null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger?.Warning(ex, "Sync failed: network error");
            return await HandleApiFailureAsync(accountId, state, ErrorCategory.Network, "Network error contacting GitHub.", null, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            _logger?.Warning(ex, "Sync failed: timeout");
            return await HandleApiFailureAsync(accountId, state, ErrorCategory.Network, "Request to GitHub timed out.", null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Sync failed: unknown error");
            return await HandleApiFailureAsync(accountId, state, ErrorCategory.Unknown, ex.Message, null, ct).ConfigureAwait(false);
        }

        var now = _clock.UtcNow;

        // 5. 304 Not Modified.
        if (response.NotModified)
        {
            try
            {
                await PersistSyncStateAsync(state with
                {
                    LastSyncAt = now,
                    LastSuccessfulSyncAt = now,
                    NotificationsEtag = response.Etag ?? state.NotificationsEtag,
                    RateLimitRemaining = response.RateLimit.Remaining,
                    RateLimitResetAt = response.RateLimit.ResetAt,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warning(ex, "Post-304 sync-state persistence failed");
                var dbResult = new SyncResult(false, 0, 0, 0, ErrorCategory.Database, ex.Message, response.RateLimit);
                RaiseProgress(accountId, SyncStage.Failed, dbResult);
                return dbResult;
            }

            // Pruning is best-effort on the 304 path too — matches the
            // non-304 path below so a prune-only failure can never flip a
            // successful sync into a failed result. The data we just
            // confirmed-fresh is already persisted by the etag bookkeeping
            // above; deletion of stale rows is a janitor task that retries
            // on the next sync.
            RaiseProgress(accountId, SyncStage.Pruning, null);
            try
            {
                await PruneAsync(now, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warning(ex, "Post-304 cache prune failed");
            }

            var notModifiedResult = new SyncResult(true, 0, 0, 0, null, null, response.RateLimit);
            RaiseProgress(accountId, SyncStage.Completed, notModifiedResult);
            return notModifiedResult;
        }

        // 6. Persist notifications.
        RaiseProgress(accountId, SyncStage.Persisting, null);

        var newCount = 0;
        var updatedCount = 0;
        var highPriorityNew = new List<GitHubNotification>();
        var seenRepoFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var notification in response.Notifications)
            {
                ct.ThrowIfCancellationRequested();

                var existing = await _notificationRepository
                    .GetByIdAsync(notification.Id, ct)
                    .ConfigureAwait(false);
                var isNew = existing is null;

                // TODO: get true raw_json from API client; design constraint between
                // Phase 6 and Phase 5+7. Synthesize a minimal payload for the MVP so
                // the raw_json column is non-empty and roughly representative.
                var rawJson = SynthesizeRawJson(notification);

                await _notificationRepository
                    .UpsertAsync(notification, rawJson, now, ct)
                    .ConfigureAwait(false);

                // Append an event row alongside the latest-state upsert so the
                // timeline UI can render one row per observed update. The
                // unique index on (account_id, notification_id, source_updated_at)
                // dedups identical re-fetches: when a thread is already known
                // at the same upstream updated_at, TryAppend returns false and
                // the timeline stays at one row for that observation. A
                // separate transition (e.g. PR Open -> Draft) bumps updated_at,
                // so the next sync inserts a new row and the timeline grows.
                //
                // raw_json mirrors the synthesized minimal payload above; once
                // the API client carries the original GitHub JSON forward
                // (Phase 7 follow-up), this snapshot becomes the real raw
                // payload without any further sync-side change.
                // Resolve the actor (commenter for Comment kind, creator
                // for PR/Issue/...) up-front so the User column is populated
                // the moment the row appears — no lazy backfill, no per-row
                // click required.
                //
                // Skip the lookup entirely when the cached notification row
                // already has an actor for this thread: the new event row
                // inherits the value via SetActorLoginAsync below, and re-
                // fetching every sync wastes API budget on data we already
                // have. We still resolve when:
                //   * isNew (no cached row at all)
                //   * existing.ActorLogin is null (never resolved)
                //   * the notification's Kind has changed since the last
                //     observation (the prior commenter no longer represents
                //     the new event row's content).
                var existingKind = existing?.Subject.Kind;
                var newKind = notification.Subject.Kind;
                var needsActorLookup = existing is null
                    || string.IsNullOrEmpty(existing.ActorLogin)
                    || existingKind != newKind;
                var actorLogin = needsActorLookup
                    ? await ResolveActorLoginAsync(pat, notification, ct).ConfigureAwait(false)
                    : existing!.ActorLogin;

                var snapshot = new NotificationEvent(
                    Id: 0,
                    AccountId: notification.AccountId,
                    NotificationId: notification.Id,
                    ThreadId: notification.ThreadId,
                    RepositoryFullName: notification.RepositoryFullName,
                    Subject: notification.Subject,
                    Reason: notification.Reason,
                    SourceUpdatedAt: notification.UpdatedAt,
                    ObservedAt: now,
                    Unread: notification.Unread,
                    LastReadAt: notification.LastReadAt,
                    RawJson: rawJson,
                    ActorLogin: actorLogin);
                var eventAppended = false;
                try
                {
                    eventAppended = await _eventRepository.TryAppendAsync(snapshot, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Event-log append is non-fatal — losing one event row should
                    // not bring the whole sync down because the latest state in
                    // notifications is already persisted by the upsert above.
                    _logger?.Warning(ex, "Append notification event failed for {NotificationId}", notification.Id);
                }

                // Persist the thread-level actor whenever we just resolved a
                // fresh value AND the cached row still lacks one (or the
                // resolved value differs from the cache). The notification
                // row's actor_login feeds the timeline's User column for any
                // sibling event rows that pre-date sync-time resolution, so
                // backfilling here closes the gap for existing legacy data
                // without waiting on a row click.
                if (eventAppended
                    && needsActorLookup
                    && !string.IsNullOrEmpty(actorLogin)
                    && !string.Equals(existing?.ActorLogin, actorLogin, StringComparison.Ordinal))
                {
                    try
                    {
                        await _notificationRepository
                            .SetActorLoginAsync(notification.Id, actorLogin!, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.Information(ex, "Seeding actor_login for {NotificationId} failed (non-fatal)", notification.Id);
                    }
                }

                if (isNew)
                {
                    newCount++;
                }
                else
                {
                    updatedCount++;
                }

                // OS-banner trigger: any newly-observed event (new thread OR
                // existing thread with a fresh source_updated_at) deserves a
                // banner if the reason is high-priority. Old events that
                // upstream surfaces for the first time but whose
                // source_updated_at is older than our previous successful
                // sync are NOT new from the user's perspective — we either
                // banner'd them already in a past run, or the user has
                // since moved on. Re-firing them is the "old notifications
                // suddenly appear" UX bug. We still let them through on
                // the very first sync (LastSuccessfulSyncAt is null and
                // hasPriorSync gates the whole event upstream anyway).
                if (eventAppended
                    && HighPriorityNotificationReasons.Contains(notification.Reason)
                    && (state.LastSuccessfulSyncAt is null
                        || notification.UpdatedAt > state.LastSuccessfulSyncAt))
                {
                    highPriorityNew.Add(notification);
                }

                if (!string.IsNullOrEmpty(notification.RepositoryFullName) &&
                    seenRepoFullNames.Add(notification.RepositoryFullName))
                {
                    try
                    {
                        await UpsertRepositoryAsync(accountId, notification.RepositoryFullName, ct).ConfigureAwait(false);
                    }
                    catch (ArgumentException ex)
                    {
                        // Malformed repository_full_name from upstream — log and skip
                        // rather than failing the whole sync, so cached data is preserved
                        // (issue #16). This is defensive against drift before issue #6's
                        // invariants land.
                        _logger?.Warning(
                            ex,
                            "Skipping repository upsert for notification {NotificationId} due to malformed full_name",
                            notification.Id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Persisting notifications failed");
            var dbResult = new SyncResult(false, response.Notifications.Count, newCount, updatedCount, ErrorCategory.Database, ex.Message, response.RateLimit);
            RaiseProgress(accountId, SyncStage.Failed, dbResult);
            return dbResult;
        }

        // 7. Persist updated sync state.
        try
        {
            await PersistSyncStateAsync(state with
            {
                LastSyncAt = now,
                LastSuccessfulSyncAt = now,
                NotificationsEtag = response.Etag ?? state.NotificationsEtag,
                RateLimitRemaining = response.RateLimit.Remaining,
                RateLimitResetAt = response.RateLimit.ResetAt,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Persisting sync state failed");
            var dbResult = new SyncResult(false, response.Notifications.Count, newCount, updatedCount, ErrorCategory.Database, ex.Message, response.RateLimit);
            RaiseProgress(accountId, SyncStage.Failed, dbResult);
            return dbResult;
        }

        // 8. Prune old cached rows.
        RaiseProgress(accountId, SyncStage.Pruning, null);
        try
        {
            await PruneAsync(now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pruning failures are non-fatal: the sync itself succeeded.
            _logger?.Warning(ex, "Cache prune failed");
        }

        var result2 = new SyncResult(
            Success: true,
            FetchedCount: response.Notifications.Count,
            NewCount: newCount,
            UpdatedCount: updatedCount,
            Error: null,
            Message: null,
            RateLimit: response.RateLimit);

        // 9. NewNotifications event — fire BEFORE Progress.Completed so the
        // OS banner is dispatched alongside the timeline reload rather than
        // racing it. Suppressed only on the FIRST EVER sync (no prior
        // successful sync) so a fresh install doesn't banner every cached
        // thread; once we've synced once, every later sync fires and the
        // gate's per-event dedup keeps re-observations quiet.
        if (hasPriorSync && highPriorityNew.Count > 0)
        {
            try
            {
                NewNotifications?.Invoke(this, new NewNotificationsEvent(accountId, highPriorityNew));
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "NewNotifications subscriber threw");
            }
        }

        RaiseProgress(accountId, SyncStage.Completed, result2);

        return result2;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_backgroundTask is { IsCompleted: false })
            {
                return;
            }

            _backgroundCts = new CancellationTokenSource();
            var cts = _backgroundCts;
            _backgroundTask = Task.Run(() => RunBackgroundLoopAsync(cts.Token), cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? toCancel;
        Task? toAwait;

        lock (_lifecycleLock)
        {
            toCancel = _backgroundCts;
            toAwait = _backgroundTask;
            _backgroundCts = null;
            _backgroundTask = null;
        }

        if (toCancel is null)
        {
            return;
        }

        try
        {
            toCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a previous stop.
        }

        try
        {
            // Best-effort wait. Do not block forever in case the loop is mid-call.
            toAwait?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // PeriodicTimer cancellation surfaces as a TaskCanceledException; ignore.
        }
        finally
        {
            toCancel.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var accountId = await ResolveLoopAccountIdAsync(ct).ConfigureAwait(false);
                if (accountId is null)
                {
                    continue;
                }

                try
                {
                    await SyncAsync(accountId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "Background sync iteration failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop().
        }
    }

    private async Task<string?> ResolveLoopAccountIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_defaultAccountId))
        {
            return _defaultAccountId;
        }

        try
        {
            var accounts = await _accountRepository.ListAsync(ct).ConfigureAwait(false);
            return accounts.Count == 0 ? null : accounts[0].Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Could not resolve account for periodic sync");
            return null;
        }
    }

    private async Task<SyncResult> HandleApiFailureAsync(
        string accountId,
        SyncState state,
        ErrorCategory category,
        string message,
        RateLimitInfo? rateLimit,
        CancellationToken ct)
    {
        // Persist last-attempted-at without bumping last_successful_sync_at, so the
        // status bar can surface "sync failed" but cached data remains intact.
        var now = _clock.UtcNow;
        try
        {
            await PersistSyncStateAsync(state with
            {
                LastSyncAt = now,
                RateLimitRemaining = rateLimit?.Remaining ?? state.RateLimitRemaining,
                RateLimitResetAt = rateLimit?.ResetAt ?? state.RateLimitResetAt,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warning(ex, "Persisting failed-sync bookkeeping itself failed");
        }

        var result = new SyncResult(false, 0, 0, 0, category, message, rateLimit);
        RaiseProgress(accountId, SyncStage.Failed, result);
        return result;
    }

    private Task PersistSyncStateAsync(SyncState updated, CancellationToken ct) =>
        _syncStateRepository.UpsertAsync(updated, ct);

    private async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - CacheRetention;
        await _notificationRepository.DeleteOlderThanAsync(cutoff, ct).ConfigureAwait(false);
        // Event log mirrors the same 30-day retention (ADR-022). Prune by
        // observed_at because a row's source_updated_at may pre-date observation
        // (e.g. backfilled threads), and we want retention measured from when
        // we wrote the row locally.
        await _eventRepository.DeleteOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }

    private async Task UpsertRepositoryAsync(string accountId, string fullName, CancellationToken ct)
    {
        var (owner, name) = SplitFullName(fullName);
        var existing = await _repositoryRepository
            .GetByFullNameAsync(accountId, fullName, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return;
        }

        var repoRef = new RepositoryRef(
            Id: $"{accountId}:{fullName}",
            AccountId: accountId,
            FullName: fullName,
            Owner: owner,
            Name: name,
            HtmlUrl: $"https://github.com/{fullName}");

        await _repositoryRepository.UpsertAsync(repoRef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a GitHub repository "owner/name" string into its parts. Throws
    /// <see cref="ArgumentException"/> for malformed inputs so callers can decide
    /// to skip the offending notification rather than silently constructing a
    /// <see cref="RepositoryRef"/> with ambiguous values (issue #16).
    /// </summary>
    internal static (string Owner, string Name) SplitFullName(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);

        var slash = fullName.IndexOf('/');
        // Require exactly one '/', with both halves non-empty. A second '/'
        // is also rejected because GitHub repository full names never contain
        // sub-paths.
        if (slash <= 0 ||
            slash >= fullName.Length - 1 ||
            fullName.IndexOf('/', slash + 1) >= 0)
        {
            throw new ArgumentException(
                $"Repository full name must be 'owner/name' format. Received: '{fullName}'.",
                nameof(fullName));
        }

        return (fullName[..slash], fullName[(slash + 1)..]);
    }

    /// <summary>
    /// Best-effort actor lookup at sync time, dispatched on
    /// <see cref="NotificationSubject.Kind"/>:
    ///   * Comment kind → fetch the commenter login via latest_comment_url.
    ///   * PR / Issue / Discussion / etc. → fetch the subject creator via
    ///     subject.url.
    /// The actor in either case represents the person who PRODUCED this
    /// row's content (commenter for comment rows, creator for PR rows),
    /// so the timeline's User column attributes correctly per row.
    /// Returns null on any failure — the row falls back to lazy backfill
    /// on selection.
    /// </summary>
    private async Task<string?> ResolveActorLoginAsync(string pat, GitHubNotification notification, CancellationToken ct)
    {
        var subject = notification.Subject;
        var url = subject.Kind == NotificationEventKind.Comment
            ? subject.LatestCommentApiUrl
            : subject.ApiUrl;

        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var (_, login) = await _apiClient
                .GetSubjectBodyAndAuthorAsync(pat, url, ct)
                .ConfigureAwait(false);
            return login;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Information(ex, "Actor lookup at sync time failed for {NotificationId} (non-fatal)", notification.Id);
            return null;
        }
    }

    private static string SynthesizeRawJson(GitHubNotification notification)
    {
        // Minimal but representative payload. Acceptable for MVP because raw_json
        // is reserved for future replay rather than runtime behavior.
        var payload = new
        {
            id = notification.ThreadId,
            reason = notification.Reason.ToString(),
            unread = notification.Unread,
            updated_at = notification.UpdatedAt.ToString("O"),
            subject = new
            {
                title = notification.Subject.Title,
                type = notification.Subject.Type,
                url = notification.Subject.ApiUrl,
            },
            repository = new
            {
                full_name = notification.RepositoryFullName,
            },
        };
        return JsonSerializer.Serialize(payload, RawJsonOptions);
    }

    private void RaiseProgress(string accountId, SyncStage stage, SyncResult? result)
    {
        try
        {
            Progress?.Invoke(this, new SyncProgressEvent(accountId, stage, result));
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Progress subscriber threw at stage {Stage}", stage);
        }
    }
}
