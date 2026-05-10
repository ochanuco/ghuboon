namespace Ghuboon.Core.Domain;

/// <summary>
/// What this notification event row actually represents in GitHub terms.
/// Computed per-event from <see cref="NotificationSubject.Type"/> and
/// <see cref="NotificationSubject.LatestCommentApiUrl"/>:
///   * <c>LatestCommentApiUrl</c> contains <c>/comments/</c> →
///     <see cref="Comment"/> (the row was triggered by someone posting
///     a comment; actor = commenter, body = comment text).
///   * Otherwise the subject's type drives the kind
///     (<see cref="PullRequest"/>, <see cref="Issue"/>, etc.) — the row
///     represents the PR/Issue itself or one of its non-comment state
///     transitions; actor = subject creator, body = subject description.
/// </summary>
/// <remarks>
/// Distinct from <see cref="NotificationReason"/>, which describes WHY
/// GitHub delivered the notification (Author/Mention/Review/...) — a
/// different axis. A self-authored PR's comment activity has
/// <c>Reason = MyPr</c> AND <c>Kind = Comment</c>: both signals are
/// surfaced separately in the timeline.
/// </remarks>
public enum NotificationEventKind
{
    Unknown = 0,
    PullRequest = 1,
    Issue = 2,
    Comment = 3,
    Discussion = 4,
    Commit = 5,
    Release = 6,
}
