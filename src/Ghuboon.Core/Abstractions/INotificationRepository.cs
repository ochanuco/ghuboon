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

    /// <summary>
    /// Lazily backfill the per-thread actor login (e.g. <c>"coderabbitai[bot]"</c>)
    /// for the notification with the given id. Used when a row is selected and
    /// the detail-pane fetch surfaces the latest comment author. Updates only
    /// the matching row and returns the number of rows affected (0 or 1).
    /// </summary>
    Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default);
}
