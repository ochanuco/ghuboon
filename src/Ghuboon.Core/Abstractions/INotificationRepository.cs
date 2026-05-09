using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Persistence for cached <see cref="GitHubNotification"/>s.
/// Notification rows also retain the original GitHub <c>raw_json</c> payload so the
/// app can re-derive fields without re-issuing API calls (ADR-010).
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Insert or update a notification. <paramref name="rawJson"/> is the original
    /// GitHub API payload as received; callers should pass an empty string if
    /// they have no payload (tests, synthetic data).
    /// </summary>
    Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default);

    Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Delete every cached notification whose <c>synced_at</c> column is older than
    /// <paramref name="cutoff"/>. Returns the number of rows removed.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
