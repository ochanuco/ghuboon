namespace Ghuboon.Core.Domain;

/// <summary>
/// Single source of truth for the set of notification reasons that
/// surface as OS banners. Used by both the sync upstream filter
/// (don't even raise the NewNotifications event for low-signal
/// reasons) and the gate downstream filter (final dedup before
/// shelling out to the OS notification service).
/// <para>
/// Reasons in the set:
///   * <see cref="NotificationReason.Review"/> — review requested
///   * <see cref="NotificationReason.Mention"/> — @-mention
///   * <see cref="NotificationReason.TeamMention"/> — @team mention
///   * <see cref="NotificationReason.Assigned"/> — assignee
///   * <see cref="NotificationReason.MyPr"/> — your authored PR/Issue
///   * <see cref="NotificationReason.State"/> — Open/Draft/Closed/Reopen
///   * <see cref="NotificationReason.Comment"/> — new comment on subscribed
/// Watching / CiActivity / Manual remain suppressed (release bots,
/// dependabot, periodic CI). The set used to live in both
/// NotificationSyncService and HighPriorityNotificationGate; the two
/// copies drifted apart and silenced authored-PR / State / Comment
/// banners for a release. Centralising here prevents recurrence.
/// </para>
/// </summary>
public static class HighPriorityNotificationReasons
{
    public static readonly IReadOnlySet<NotificationReason> Set = new HashSet<NotificationReason>
    {
        NotificationReason.Review,
        NotificationReason.Mention,
        NotificationReason.TeamMention,
        NotificationReason.Assigned,
        NotificationReason.MyPr,
        NotificationReason.State,
        NotificationReason.Comment,
    };

    public static bool Contains(NotificationReason reason) => Set.Contains(reason);
}
