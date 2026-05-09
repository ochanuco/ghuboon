namespace Ghuboon.Core.Domain;

/// <summary>
/// Maps GitHub API notification <c>reason</c> strings to <see cref="NotificationReason"/>.
/// Unknown / null / empty inputs map to <see cref="NotificationReason.Unknown"/>.
/// Comparison is case-insensitive (invariant culture).
/// </summary>
public static class NotificationReasonMap
{
    public static NotificationReason From(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NotificationReason.Unknown;
        }

        return raw.ToLowerInvariant() switch
        {
            "review_requested" => NotificationReason.Review,
            "mention" => NotificationReason.Mention,
            "team_mention" => NotificationReason.TeamMention,
            "assign" => NotificationReason.Assigned,
            "author" => NotificationReason.MyPr,
            "comment" => NotificationReason.Comment,
            "state_change" => NotificationReason.State,
            "subscribed" => NotificationReason.Watching,
            "manual" => NotificationReason.Manual,
            "invitation" => NotificationReason.Invitation,
            "security_alert" => NotificationReason.SecurityAlert,
            "ci_activity" => NotificationReason.CiActivity,
            _ => NotificationReason.Unknown,
        };
    }
}
