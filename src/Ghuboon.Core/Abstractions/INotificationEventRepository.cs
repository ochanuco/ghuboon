using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Append-only persistence for <see cref="NotificationEvent"/> rows that back the
/// timeline UI. Each observed update to a notification thread (i.e. each fetch
/// that sees a different <c>updated_at</c>) becomes a row, so the timeline can
/// render an event log rather than a per-thread snapshot.
/// </summary>
public interface INotificationEventRepository
{
    /// <summary>
    /// Insert a new event. Returns <c>true</c> if a row was inserted, <c>false</c>
    /// if a duplicate row for the same
    /// <c>(account_id, notification_id, source_updated_at)</c> already exists.
    /// Implementations must enforce that uniqueness so re-syncing the same
    /// thread at the same upstream <c>updated_at</c> does not multiply rows.
    /// </summary>
    Task<bool> TryAppendAsync(NotificationEvent ev, CancellationToken ct = default);

    /// <summary>
    /// List recent events for an account, newest first by
    /// <see cref="NotificationEvent.ObservedAt"/> with <see cref="NotificationEvent.Id"/>
    /// as a tiebreaker.
    /// </summary>
    Task<IReadOnlyList<NotificationEvent>> ListByAccountAsync(string accountId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Mark every event for a thread as read: sets <c>unread = 0</c> and updates
    /// <c>last_read_at</c> to <paramref name="readAt"/> (UTC-normalized) for every
    /// matching event row. Returns the number of rows affected.
    /// </summary>
    Task<int> MarkThreadAsReadAsync(string accountId, string notificationId, DateTimeOffset readAt, CancellationToken ct = default);

    /// <summary>
    /// Cache prune: delete every event whose <c>observed_at</c> is older than
    /// <paramref name="cutoff"/>. Returns the number of rows removed.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Return the maximum <c>source_updated_at</c> across every event row for
    /// a given thread. Used by the detail pane to decide whether the row the
    /// user just selected is the latest observation of the thread (show
    /// latest comment) or an older one (show subject body).
    /// </summary>
    Task<DateTimeOffset?> GetMaxSourceUpdatedAtForThreadAsync(
        string accountId,
        string notificationId,
        CancellationToken ct = default);
}
