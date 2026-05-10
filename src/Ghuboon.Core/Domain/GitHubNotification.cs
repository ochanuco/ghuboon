namespace Ghuboon.Core.Domain;

/// <summary>
/// A single notification thread for a given account.
/// </summary>
/// <param name="Id">Stable local id; must equal <c>"{AccountId}:{ThreadId}"</c>.</param>
/// <param name="AccountId">Owning account id (non-empty).</param>
/// <param name="ThreadId">GitHub notification thread id (string form of API id, non-empty).</param>
/// <param name="RepositoryFullName">"owner/name" of the source repo. Must contain exactly one '/'; both sides non-empty.</param>
/// <param name="Subject">Subject (type, title, optional URLs) describing what the thread is about.</param>
/// <param name="Reason">Reason GitHub delivered the notification.</param>
/// <param name="Unread">True while the thread is unread for the user.</param>
/// <param name="UpdatedAt">Last time the thread changed on GitHub (server clock).</param>
/// <param name="LastReadAt">Last time the user marked it read locally; null when never read.</param>
/// <exception cref="ArgumentNullException">
/// Thrown when any of <paramref name="Id"/>, <paramref name="AccountId"/>,
/// <paramref name="ThreadId"/>, or <paramref name="RepositoryFullName"/> is null.
/// </exception>
/// <exception cref="ArgumentException">
/// Thrown when any required string is empty/whitespace, when
/// <paramref name="RepositoryFullName"/> is not in <c>"owner/name"</c> form, or
/// when <paramref name="Id"/> does not equal <c>"{AccountId}:{ThreadId}"</c>.
/// </exception>
public sealed record GitHubNotification
{
    public GitHubNotification(
        string Id,
        string AccountId,
        string ThreadId,
        string RepositoryFullName,
        NotificationSubject Subject,
        NotificationReason Reason,
        bool Unread,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? LastReadAt)
    {
        ArgumentNullException.ThrowIfNull(Id);
        ArgumentNullException.ThrowIfNull(AccountId);
        ArgumentNullException.ThrowIfNull(ThreadId);
        ArgumentNullException.ThrowIfNull(RepositoryFullName);
        ArgumentNullException.ThrowIfNull(Subject);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("Id must not be empty or whitespace.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(AccountId))
        {
            throw new ArgumentException("AccountId must not be empty or whitespace.", nameof(AccountId));
        }

        if (string.IsNullOrWhiteSpace(ThreadId))
        {
            throw new ArgumentException("ThreadId must not be empty or whitespace.", nameof(ThreadId));
        }

        if (string.IsNullOrWhiteSpace(RepositoryFullName))
        {
            throw new ArgumentException("RepositoryFullName must not be empty or whitespace.", nameof(RepositoryFullName));
        }

        var slashCount = 0;
        for (var i = 0; i < RepositoryFullName.Length; i++)
        {
            if (RepositoryFullName[i] == '/')
            {
                slashCount++;
            }
        }

        if (slashCount != 1)
        {
            throw new ArgumentException(
                $"RepositoryFullName must be in the form 'owner/name' with exactly one '/'; got '{RepositoryFullName}'.",
                nameof(RepositoryFullName));
        }

        var slashIndex = RepositoryFullName.IndexOf('/');
        var owner = RepositoryFullName.AsSpan(0, slashIndex);
        var name = RepositoryFullName.AsSpan(slashIndex + 1);
        if (owner.IsWhiteSpace() || owner.IsEmpty || name.IsWhiteSpace() || name.IsEmpty)
        {
            throw new ArgumentException(
                $"RepositoryFullName must have non-empty owner and name on each side of '/'; got '{RepositoryFullName}'.",
                nameof(RepositoryFullName));
        }

        var expectedId = $"{AccountId}:{ThreadId}";
        if (!string.Equals(Id, expectedId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Id must equal \"{{AccountId}}:{{ThreadId}}\" (expected '{expectedId}', got '{Id}').",
                nameof(Id));
        }

        this.Id = Id;
        this.AccountId = AccountId;
        this.ThreadId = ThreadId;
        this.RepositoryFullName = RepositoryFullName;
        this.Subject = Subject;
        this.Reason = Reason;
        this.Unread = Unread;
        this.UpdatedAt = UpdatedAt;
        this.LastReadAt = LastReadAt;
    }

    public string Id { get; init; }
    public string AccountId { get; init; }
    public string ThreadId { get; init; }
    public string RepositoryFullName { get; init; }
    public NotificationSubject Subject { get; init; }
    public NotificationReason Reason { get; init; }
    public bool Unread { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastReadAt { get; init; }
}
