namespace Ghuboon.Core.Domain;

/// <summary>
/// Reason a GitHub notification was delivered.
/// Mapped from GitHub API <c>reason</c> field via <see cref="NotificationReasonMap"/>.
/// </summary>
public enum NotificationReason
{
    Unknown = 0,
    Review,
    Mention,
    TeamMention,
    Assigned,
    MyPr,
    Comment,
    State,
    Watching,
    Manual,
    Invitation,
    SecurityAlert,
    CiActivity,
}
