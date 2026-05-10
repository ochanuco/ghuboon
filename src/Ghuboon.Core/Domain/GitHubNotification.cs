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
/// <param name="ActorLogin">
/// Optional GitHub login of the user/bot whose action produced this thread
/// (e.g. <c>"coderabbitai[bot]"</c>). The notifications listing API does not
/// include this field, so it is lazily back-filled from per-thread fetches
/// and may be null for legacy rows.
/// </param>
/// <exception cref="ArgumentNullException">
/// Thrown when any of <paramref name="Id"/>, <paramref name="AccountId"/>,
/// <paramref name="ThreadId"/>, <paramref name="RepositoryFullName"/>, or
/// <paramref name="Subject"/> is null.
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
        DateTimeOffset? LastReadAt,
        string? ActorLogin = null)
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
        this.ActorLogin = ActorLogin;
    }

    // Issue #35: Id, AccountId, ThreadId, and RepositoryFullName are part of
    // the identity invariant validated by the constructor (Id must equal
    // "{AccountId}:{ThreadId}"). Public `init` would let a `with`-expression
    // overwrite any of them and skip re-validation, so they are `private init`.
    // Mutable-feeling properties (Unread, UpdatedAt, LastReadAt, Subject,
    // Reason) keep public `init` so callers can still produce derived records
    // like `notif with { Unread = false }`.
    public string Id { get; private init; }
    public string AccountId { get; private init; }
    public string ThreadId { get; private init; }
    public string RepositoryFullName { get; private init; }
    public NotificationSubject Subject { get; init; }
    public NotificationReason Reason { get; init; }
    public bool Unread { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastReadAt { get; init; }
    /// <summary>
    /// Optional login of the actor whose action produced the latest observation
    /// of this thread. The notifications listing API does not surface this, so
    /// it is populated lazily on row selection (per-thread fetch) and persisted
    /// back so subsequent sessions see the real author/bot rather than the
    /// repo owner stop-gap. Null until the lazy backfill runs.
    /// </summary>
    public string? ActorLogin { get; init; }
}
