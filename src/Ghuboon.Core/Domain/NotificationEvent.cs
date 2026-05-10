namespace Ghuboon.Core.Domain;

/// <summary>
/// One observed update to a notification thread (Phase 5/7/8 event log).
/// <para>
/// Where <see cref="GitHubNotification"/> represents the latest known state of a
/// thread (one row per thread per account), <see cref="NotificationEvent"/> is
/// append-only: each sync that observes a different <c>updated_at</c> on the
/// thread produces a new event row. The timeline UI renders one row per event,
/// so a PR going Open -&gt; Draft -&gt; Open over multiple sync windows shows up
/// as three separate timeline rows.
/// </para>
/// </summary>
/// <param name="Id">Local autoincrement primary key. <c>0</c> means not-yet-persisted.</param>
/// <param name="AccountId">Owning account id (non-empty).</param>
/// <param name="NotificationId">Mirrors <see cref="GitHubNotification.Id"/> ("{AccountId}:{ThreadId}").</param>
/// <param name="ThreadId">GitHub notification thread id (non-empty).</param>
/// <param name="RepositoryFullName">"owner/name" of the source repo. Must contain exactly one '/'; both sides non-empty.</param>
/// <param name="Subject">Subject (type, title, optional URLs) snapshotted at the time of the event.</param>
/// <param name="Reason">Reason GitHub delivered the notification at the time of the event.</param>
/// <param name="SourceUpdatedAt">GitHub's <c>updated_at</c> for this fetch.</param>
/// <param name="ObservedAt">When the local sync wrote this event row.</param>
/// <param name="Unread">Whether the thread was unread for the user at observation time.</param>
/// <param name="LastReadAt">Last time the user marked it read locally; null when never read.</param>
/// <param name="RawJson">Raw GitHub payload snapshot (may be a synthesized minimal payload for MVP).</param>
/// <param name="ActorLogin">
/// Optional GitHub login of the user/bot whose action produced this event
/// (e.g. <c>"coderabbitai[bot]"</c>). The notifications listing API does not
/// include this field, so it is lazily back-filled from per-thread fetches
/// and may be null for legacy rows.
/// </param>
/// <exception cref="ArgumentNullException">
/// Thrown when any of <paramref name="AccountId"/>, <paramref name="NotificationId"/>,
/// <paramref name="ThreadId"/>, <paramref name="RepositoryFullName"/>,
/// <paramref name="Subject"/>, or <paramref name="RawJson"/> is null.
/// </exception>
/// <exception cref="ArgumentException">
/// Thrown when any required string is empty/whitespace, when
/// <paramref name="RepositoryFullName"/> is not in <c>"owner/name"</c> form, or
/// when <paramref name="NotificationId"/> does not equal
/// <c>"{AccountId}:{ThreadId}"</c>.
/// </exception>
public sealed record NotificationEvent
{
    public NotificationEvent(
        long Id,
        string AccountId,
        string NotificationId,
        string ThreadId,
        string RepositoryFullName,
        NotificationSubject Subject,
        NotificationReason Reason,
        DateTimeOffset SourceUpdatedAt,
        DateTimeOffset ObservedAt,
        bool Unread,
        DateTimeOffset? LastReadAt,
        string RawJson,
        string? ActorLogin = null,
        string? Body = null,
        string? BodyAuthorLogin = null)
    {
        ArgumentNullException.ThrowIfNull(AccountId);
        ArgumentNullException.ThrowIfNull(NotificationId);
        ArgumentNullException.ThrowIfNull(ThreadId);
        ArgumentNullException.ThrowIfNull(RepositoryFullName);
        ArgumentNullException.ThrowIfNull(Subject);
        ArgumentNullException.ThrowIfNull(RawJson);

        if (string.IsNullOrWhiteSpace(AccountId))
        {
            throw new ArgumentException("AccountId must not be empty or whitespace.", nameof(AccountId));
        }

        if (string.IsNullOrWhiteSpace(NotificationId))
        {
            throw new ArgumentException("NotificationId must not be empty or whitespace.", nameof(NotificationId));
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

        var expectedNotificationId = $"{AccountId}:{ThreadId}";
        if (!string.Equals(NotificationId, expectedNotificationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"NotificationId must equal \"{{AccountId}}:{{ThreadId}}\" (expected '{expectedNotificationId}', got '{NotificationId}').",
                nameof(NotificationId));
        }

        this.Id = Id;
        this.AccountId = AccountId;
        this.NotificationId = NotificationId;
        this.ThreadId = ThreadId;
        this.RepositoryFullName = RepositoryFullName;
        this.Subject = Subject;
        this.Reason = Reason;
        this.SourceUpdatedAt = SourceUpdatedAt;
        this.ObservedAt = ObservedAt;
        this.Unread = Unread;
        this.LastReadAt = LastReadAt;
        this.RawJson = RawJson;
        this.ActorLogin = ActorLogin;
        this.Body = Body;
        this.BodyAuthorLogin = BodyAuthorLogin;
    }

    // Identity-bearing fields are private init to mirror GitHubNotification:
    // a public init would let `with` rewrite them and skip the
    // NotificationId == "{AccountId}:{ThreadId}" invariant. Mutable-feeling
    // fields keep public init so callers can produce derived records like
    // `ev with { Unread = false }`.
    public long Id { get; private init; }
    public string AccountId { get; private init; }
    public string NotificationId { get; private init; }
    public string ThreadId { get; private init; }
    public string RepositoryFullName { get; private init; }
    public NotificationSubject Subject { get; init; }
    public NotificationReason Reason { get; init; }
    public DateTimeOffset SourceUpdatedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool Unread { get; init; }
    public DateTimeOffset? LastReadAt { get; init; }
    public string RawJson { get; init; }
    /// <summary>
    /// Optional login of the actor whose action produced this event. The
    /// notifications listing API does not surface this, so it is lazily
    /// populated when the user selects the row (per-thread comment fetch)
    /// and persisted back so subsequent sessions remember the real
    /// author/bot rather than the repo owner stop-gap.
    /// </summary>
    public string? ActorLogin { get; init; }

    /// <summary>
    /// Cached body content (PR/Issue description for description-mode rows,
    /// comment text for comment-mode rows). Populated lazily when the user
    /// selects the row and the detail pane fetches the body, then persisted
    /// so subsequent sessions / re-renders don't re-hit the GitHub API for
    /// the same row. Null until first fetch (or when fetch failed).
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// Login of the user/bot whose body content is currently shown
    /// (PR/Issue creator for description rows, commenter for comment rows).
    /// Stored alongside <see cref="Body"/> so the detail-pane attribution
    /// survives across sessions without an extra fetch.
    /// </summary>
    public string? BodyAuthorLogin { get; init; }
}
