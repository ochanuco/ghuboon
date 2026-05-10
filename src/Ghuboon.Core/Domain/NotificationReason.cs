namespace Ghuboon.Core.Domain;

/// <summary>
/// Reason a GitHub notification was delivered.
/// Mapped from GitHub API <c>reason</c> field via <see cref="NotificationReasonMap"/>.
/// </summary>
/// <remarks>
/// Each member is assigned an explicit numeric value so that the underlying
/// integer representation stays stable even if members are reordered or
/// inserted later. Persisted/serialized values that round-trip through the
/// numeric form must keep matching the values declared here.
/// </remarks>
public enum NotificationReason
{
    Unknown = 0,
    Review = 1,
    Mention = 2,
    TeamMention = 3,
    Assigned = 4,
    MyPr = 5,
    Comment = 6,
    State = 7,
    Watching = 8,
    Manual = 9,
    Invitation = 10,
    SecurityAlert = 11,
    CiActivity = 12,
}
