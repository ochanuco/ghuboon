namespace Ghuboon.Core.Domain;

/// <summary>
/// Subject of a notification (the PR/Issue/Discussion/etc. it points at).
/// </summary>
/// <param name="Type">GitHub subject type, e.g. "PullRequest", "Issue", "Discussion", "Release", "Commit".</param>
/// <param name="Title">Display title.</param>
/// <param name="ApiUrl">REST API URL of the subject; may be null for some subject types.</param>
/// <param name="WebUrl">Web URL on github.com to open in a browser; may be null if not derivable.</param>
public sealed record NotificationSubject(
    string Type,
    string Title,
    string? ApiUrl,
    string? WebUrl
);
