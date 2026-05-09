using Ghuboon.Core.Domain;

namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Decides which <see cref="GitHubNotification"/>s should fire OS-level toasts
/// (ADR-021, PLAN.md Phase 11). Implementations apply two filters:
/// <list type="number">
///   <item>Reason gate &mdash; only
///   <see cref="NotificationReason.Review"/>,
///   <see cref="NotificationReason.Mention"/>,
///   <see cref="NotificationReason.TeamMention"/>, and
///   <see cref="NotificationReason.Assigned"/> are eligible.</item>
///   <item>Dedup gate &mdash; an item that already has
///   <c>notification_local_states.last_notified_at</c> set is suppressed.</item>
/// </list>
/// Accepted candidates are persisted with <c>last_notified_at = now</c> as a
/// side effect, so a subsequent call returns an empty list for the same id.
/// </summary>
public interface IDesktopNotificationGate
{
    /// <summary>
    /// Filter <paramref name="candidates"/> down to the items that should
    /// trigger an OS notification, and mark accepted items as notified.
    /// </summary>
    Task<IReadOnlyList<GitHubNotification>> FilterAsync(
        string accountId,
        IReadOnlyList<GitHubNotification> candidates,
        CancellationToken ct = default);
}
