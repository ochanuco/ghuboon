namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Shows OS-level notifications for high-priority new GitHub items (ADR-021,
/// PLAN.md Phase 11). Implementations are platform-specific (macOS today,
/// no-op on Windows/Linux for the MVP). Implementations MUST NOT throw on
/// platform/permission failures &mdash; failures must be swallowed and logged
/// so that a misbehaving notification surface never breaks sync.
/// </summary>
/// <remarks>
/// Expected wiring (Lane I, App composition):
/// <code>
/// syncService.NewNotifications += async (_, ev) =&gt;
/// {
///     var toShow = await gate.FilterAsync(ev.AccountId, ev.HighPriorityNew);
///     foreach (var n in toShow)
///     {
///         await notifService.ShowAsync(new DesktopNotification(
///             Id: n.Id,
///             Title: $"{n.Reason}: {n.RepositoryFullName}",
///             Body: n.Subject.Title,
///             Url: n.Subject.WebUrl));
///     }
/// };
/// </code>
/// The gate (<see cref="IDesktopNotificationGate"/>) handles reason filtering and
/// dedup against <c>notification_local_states.last_notified_at</c>; this service
/// is responsible only for surfacing the OS toast.
/// </remarks>
public interface IDesktopNotificationService
{
    /// <summary>
    /// Display <paramref name="notification"/> to the user. Returns once the
    /// notification has been dispatched (not necessarily shown). Implementations
    /// must catch and log any platform-level errors instead of propagating them.
    /// </summary>
    Task ShowAsync(DesktopNotification notification, CancellationToken ct = default);
}

/// <summary>
/// A platform-agnostic OS notification payload.
/// </summary>
/// <param name="Id">Stable id of the source notification, used for diagnostics.</param>
/// <param name="Title">Title shown in the OS notification UI.</param>
/// <param name="Body">Body text shown beneath the title.</param>
/// <param name="Url">Optional URL to associate with the notification (currently
/// not actionable on macOS via osascript &mdash; reserved for a future
/// UNUserNotificationCenter implementation).</param>
public sealed record DesktopNotification(
    string Id,
    string Title,
    string Body,
    string? Url
);
