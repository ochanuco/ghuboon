using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Coordinates notification sync between credentials, the GitHub API, and the local
/// cache (Phase 7). Implementations are responsible for ETag reuse, error
/// classification, cache pruning (ADR-022), and high-priority notification eventing
/// (ADR-021). The service exposes both a manual <see cref="SyncAsync"/> and a
/// background loop driven by <see cref="Start"/>/<see cref="Stop"/> on a 5-minute
/// interval (ADR-020).
/// </summary>
public interface INotificationSyncService
{
    /// <summary>
    /// Raised at each <see cref="SyncStage"/> transition during a sync. The final
    /// event of any sync is <see cref="SyncStage.Completed"/> or
    /// <see cref="SyncStage.Failed"/>; both carry a non-null <c>Result</c>.
    /// </summary>
    event EventHandler<SyncProgressEvent>? Progress;

    /// <summary>
    /// Raised after a successful sync that produced new high-priority items.
    /// Suppressed on the first sync after process start (ADR-021: do not notify
    /// for already-existing unread items at startup).
    /// </summary>
    event EventHandler<NewNotificationsEvent>? NewNotifications;

    /// <summary>
    /// Performs a single sync cycle for <paramref name="accountId"/>. Suitable for
    /// startup sync and manual sync. Failures preserve cached data.
    /// </summary>
    Task<SyncResult> SyncAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Begins the background 5-minute periodic sync loop. No-op if already running.
    /// </summary>
    void Start();

    /// <summary>
    /// Cancels the background loop. No-op if not running. The next call to
    /// <see cref="Start"/> resumes a fresh loop.
    /// </summary>
    void Stop();

    /// <summary>
    /// True while the background loop is active.
    /// </summary>
    bool IsRunning { get; }
}

/// <summary>
/// Stages of a sync cycle, surfaced through <see cref="INotificationSyncService.Progress"/>.
/// </summary>
public enum SyncStage
{
    Starting = 0,
    Fetching,
    Persisting,
    Pruning,
    Completed,
    Failed,
}

/// <summary>
/// Outcome of a single <see cref="INotificationSyncService.SyncAsync"/> call.
/// </summary>
/// <param name="Success">True on a successful round-trip, including 304 Not Modified.</param>
/// <param name="FetchedCount">Total notifications returned by the API. Zero on 304.</param>
/// <param name="NewCount">Notifications previously absent from the local cache.</param>
/// <param name="UpdatedCount">Notifications that already existed and were upserted.</param>
/// <param name="Error">Non-null error category when <paramref name="Success"/> is false.</param>
/// <param name="Message">Human-readable error message; null on success.</param>
/// <param name="RateLimit">Captured rate-limit info from the most recent response.</param>
public sealed record SyncResult(
    bool Success,
    int FetchedCount,
    int NewCount,
    int UpdatedCount,
    ErrorCategory? Error,
    string? Message,
    RateLimitInfo? RateLimit
);

/// <summary>
/// Event payload for sync progress. <paramref name="Result"/> is null until the
/// final <see cref="SyncStage.Completed"/> or <see cref="SyncStage.Failed"/>
/// transition.
/// </summary>
public sealed record SyncProgressEvent(
    string AccountId,
    SyncStage Stage,
    SyncResult? Result
);

/// <summary>
/// Event payload describing high-priority new notifications produced by a sync.
/// Only items whose reason is in {Review, Mention, TeamMention, Assigned} appear
/// here (ADR-021).
/// </summary>
public sealed record NewNotificationsEvent(
    string AccountId,
    IReadOnlyList<GitHubNotification> HighPriorityNew
);
