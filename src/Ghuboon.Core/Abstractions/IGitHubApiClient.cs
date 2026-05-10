using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Abstraction over the small subset of the GitHub REST API that Ghuboon uses (ADR-009):
/// PAT validation, notification listing, and thread read operations. Implementations are
/// expected to handle ETag-based caching, capture rate-limit headers, and never log
/// Authorization headers or PAT values (ADR-007, ADR-012).
/// </summary>
public interface IGitHubApiClient
{
    /// <summary>
    /// Validates a Personal Access Token against <c>GET /user</c>.
    /// Returns a structured result rather than throwing on auth failure so callers can
    /// surface UI messages without try/catch.
    /// </summary>
    Task<UserValidationResult> ValidateAsync(string pat, CancellationToken ct = default);

    /// <summary>
    /// Lists notifications for the authenticated user via <c>GET /notifications</c>.
    /// Honors <c>If-None-Match</c> and captures ETag plus rate-limit headers.
    /// </summary>
    Task<NotificationsResponse> ListNotificationsAsync(string pat, NotificationsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Marks the given notification thread as read via
    /// <c>PATCH /notifications/threads/{thread_id}</c>.
    /// </summary>
    Task MarkThreadReadAsync(string pat, string threadId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the body of a notification subject via the API URL on the
    /// notification's <c>subject.url</c>. For PullRequest / Issue subjects this
    /// returns the description; for Comment-typed subjects returns the latest
    /// comment body. Returns null if the subject doesn't expose a body or the
    /// fetch fails (callers display a placeholder).
    /// </summary>
    Task<string?> GetSubjectBodyAsync(string pat, string subjectApiUrl, CancellationToken ct = default);

    /// <summary>
    /// Fetches the notification thread metadata via
    /// <c>GET /notifications/threads/{thread_id}</c>. Used to recover the
    /// <c>subject.url</c> for cached rows that lost it (legacy data).
    /// Returns null on failure.
    /// </summary>
    Task<string?> GetThreadSubjectUrlAsync(string pat, string threadId, CancellationToken ct = default);
}

/// <summary>
/// Result of validating a PAT.
/// </summary>
public sealed record UserValidationResult(
    bool IsValid,
    string? Login,
    ErrorCategory? Error,
    string? Message
);

/// <summary>
/// Request parameters for listing notifications.
/// </summary>
/// <param name="IfNoneMatch">Previously captured ETag, sent as <c>If-None-Match</c>.</param>
/// <param name="All">If true, includes already-read notifications.</param>
/// <param name="Participating">If true, returns only participating notifications.</param>
/// <param name="Since">Optional ISO timestamp lower bound.</param>
/// <param name="AccountId">
/// Account identifier the sync service uses to scope persisted notifications.
/// Used by the mapper to compose the domain id (<c>{accountId}:{threadId}</c>).
/// Defaults to <c>"default"</c> for the MVP single-account UI (ADR-013).
/// </param>
public sealed record NotificationsRequest(
    string? IfNoneMatch = null,
    bool All = false,
    bool Participating = false,
    DateTimeOffset? Since = null,
    string AccountId = "default"
);

/// <summary>
/// Response from a notifications list request.
/// </summary>
/// <param name="Notifications">Empty when <see cref="NotModified"/> is true.</param>
/// <param name="Etag">Captured response ETag, when present.</param>
/// <param name="RateLimit">Captured rate-limit headers.</param>
/// <param name="NotModified">True when the API responded with 304 Not Modified.</param>
public sealed record NotificationsResponse(
    IReadOnlyList<GitHubNotification> Notifications,
    string? Etag,
    RateLimitInfo RateLimit,
    bool NotModified
);

/// <summary>
/// Captured GitHub rate-limit headers.
/// </summary>
public sealed record RateLimitInfo(
    int? Remaining,
    DateTimeOffset? ResetAt
)
{
    public static RateLimitInfo Empty { get; } = new(null, null);
}
