namespace Ghuboon.Core.Domain;

/// <summary>
/// Subject of a notification (the PR/Issue/Discussion/etc. it points at).
/// </summary>
/// <param name="Type">GitHub subject type, e.g. "PullRequest", "Issue", "Discussion", "Release", "Commit".</param>
/// <param name="Title">Display title.</param>
/// <param name="ApiUrl">REST API URL of the subject; may be null for some subject types.</param>
/// <param name="WebUrl">Web URL on github.com to open in a browser; may be null if not derivable.</param>
/// <param name="LatestCommentApiUrl">
/// REST API URL of the latest comment on the thread, as supplied by the
/// notifications listing endpoint. When this points at a /comments/{id}
/// endpoint, the notification was triggered by a comment activity. When it
/// equals <see cref="ApiUrl"/> (or is null), the notification represents
/// a description-mode event (PR creation, Draft toggle, state change, CI).
/// Captured per-event so the timeline can distinguish "PR description rows"
/// from "comment rows" without an extra round-trip per row.
/// </param>
public sealed record NotificationSubject(
    string Type,
    string Title,
    string? ApiUrl,
    string? WebUrl,
    string? LatestCommentApiUrl = null
)
{
    /// <summary>
    /// True when this snapshot's <see cref="LatestCommentApiUrl"/> points at
    /// a real comment endpoint (i.e. contains <c>/comments/</c>). Used by
    /// the timeline filter to exclude comment activity from the My PRs tab.
    /// </summary>
    public bool IsCommentEvent =>
        !string.IsNullOrEmpty(LatestCommentApiUrl)
        && LatestCommentApiUrl.Contains("/comments/", StringComparison.Ordinal);
}
