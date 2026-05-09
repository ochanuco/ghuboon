namespace Ghuboon.Core.Domain;

/// <summary>
/// A single notification thread for a given account.
/// </summary>
/// <param name="Id">Stable local id (e.g. "{accountId}:{threadId}").</param>
/// <param name="AccountId">Owning account id.</param>
/// <param name="ThreadId">GitHub notification thread id (string form of API id).</param>
/// <param name="RepositoryFullName">"owner/name" of the source repo.</param>
public sealed record GitHubNotification(
    string Id,
    string AccountId,
    string ThreadId,
    string RepositoryFullName,
    NotificationSubject Subject,
    NotificationReason Reason,
    bool Unread,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastReadAt
);
