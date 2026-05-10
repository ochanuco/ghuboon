using System.Globalization;
using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Storage;

internal sealed class NotificationEventRow
{
    public long Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string NotificationId { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string RepositoryFullName { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectTitle { get; set; } = string.Empty;
    public string? SubjectApiUrl { get; set; }
    public string? WebUrl { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SourceUpdatedAt { get; set; } = string.Empty;
    public string ObservedAt { get; set; } = string.Empty;
    public long Unread { get; set; }
    public string? LastReadAt { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public string? ActorLogin { get; set; }
    public string? LatestCommentUrl { get; set; }
}

/// <summary>
/// Dapper-backed <see cref="INotificationEventRepository"/>. Each successful
/// <see cref="TryAppendAsync"/> call inserts an append-only event row; a row
/// describing the same <c>(account_id, notification_id, source_updated_at)</c>
/// triple is rejected by the unique index <c>ux_events_dedup</c>, in which case
/// <see cref="TryAppendAsync"/> reports <c>false</c> rather than throwing.
/// Timestamps are normalized to UTC ISO-8601 to keep lexical and chronological
/// ordering aligned (matches <see cref="NotificationRepository"/>).
/// </summary>
public sealed class NotificationEventRepository : INotificationEventRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public NotificationEventRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<bool> TryAppendAsync(NotificationEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // ON CONFLICT DO NOTHING leans on ux_events_dedup
        // (account_id, notification_id, source_updated_at) so a re-observed
        // thread at the same upstream updated_at does not multiply rows;
        // ExecuteAsync returns 0 in that case and we report false to the caller.
        const string sql = """
                           INSERT INTO notification_events (
                               account_id, notification_id, thread_id, repository_full_name,
                               subject_type, subject_title, subject_api_url, web_url,
                               reason, source_updated_at, observed_at,
                               unread, last_read_at, raw_json, actor_login, latest_comment_url)
                           VALUES (
                               @accountId, @notificationId, @threadId, @repositoryFullName,
                               @subjectType, @subjectTitle, @subjectApiUrl, @webUrl,
                               @reason, @sourceUpdatedAt, @observedAt,
                               @unread, @lastReadAt, @rawJson, @actorLogin, @latestCommentUrl)
                           ON CONFLICT (account_id, notification_id, source_updated_at) DO NOTHING;
                           """;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                accountId = ev.AccountId,
                notificationId = ev.NotificationId,
                threadId = ev.ThreadId,
                repositoryFullName = ev.RepositoryFullName,
                subjectType = ev.Subject.Type,
                subjectTitle = ev.Subject.Title,
                subjectApiUrl = ev.Subject.ApiUrl,
                webUrl = ev.Subject.WebUrl,
                reason = ev.Reason.ToString(),
                sourceUpdatedAt = ev.SourceUpdatedAt.ToUniversalTime().ToString("O"),
                observedAt = ev.ObservedAt.ToUniversalTime().ToString("O"),
                unread = ev.Unread ? 1L : 0L,
                lastReadAt = ev.LastReadAt?.ToUniversalTime().ToString("O"),
                rawJson = ev.RawJson,
                actorLogin = ev.ActorLogin,
                latestCommentUrl = ev.Subject.LatestCommentApiUrl,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<IReadOnlyList<NotificationEvent>> ListByAccountAsync(string accountId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (limit <= 0)
        {
            return Array.Empty<NotificationEvent>();
        }

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Tween-like timeline: newest goes at the bottom, so the UI receives
        // events oldest-first. The inner query caps to the latest N rows by
        // observed_at (when WE saw them) so a v4 backfill of historical
        // notifications still surfaces. The outer ORDER uses
        // source_updated_at — i.e. the GitHub thread's updated_at — so the
        // displayed order matches the "Updated" column the user sees and
        // chronologically reflects upstream activity rather than an
        // arbitrary side-effect of when our sync ran.
        const string sql = """
                           SELECT id AS Id,
                                  account_id AS AccountId,
                                  notification_id AS NotificationId,
                                  thread_id AS ThreadId,
                                  repository_full_name AS RepositoryFullName,
                                  subject_type AS SubjectType,
                                  subject_title AS SubjectTitle,
                                  subject_api_url AS SubjectApiUrl,
                                  web_url AS WebUrl,
                                  reason AS Reason,
                                  source_updated_at AS SourceUpdatedAt,
                                  observed_at AS ObservedAt,
                                  unread AS Unread,
                                  last_read_at AS LastReadAt,
                                  raw_json AS RawJson,
                                  actor_login AS ActorLogin,
                                  latest_comment_url AS LatestCommentUrl
                           FROM (
                             SELECT *
                             FROM notification_events
                             WHERE account_id = @accountId
                             -- Tie-break by source_updated_at BEFORE id so a
                             -- v4 backfill that stamps every row with the
                             -- same observed_at still keeps the freshest
                             -- upstream events inside the window. Without
                             -- this, source_updated_at-ordered timelines
                             -- can lose their newest rows when 200+ legacy
                             -- rows share an observed_at.
                             ORDER BY datetime(observed_at) DESC,
                                      datetime(source_updated_at) DESC,
                                      id DESC
                             LIMIT @limit
                           )
                           ORDER BY datetime(source_updated_at) ASC, id ASC;
                           """;

        var rows = await connection.QueryAsync<NotificationEventRow>(new CommandDefinition(
            sql,
            new { accountId, limit },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    public async Task<int> MarkThreadAsReadAsync(string accountId, string notificationId, DateTimeOffset readAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // last_read_at is overwritten unconditionally so a subsequent local
        // read transition always advances the timestamp; keeping the older
        // value would lose information about when the user last touched the
        // thread.
        const string sql = """
                           UPDATE notification_events
                              SET unread = 0,
                                  last_read_at = @readAt
                            WHERE account_id = @accountId
                              AND notification_id = @notificationId;
                           """;

        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                accountId,
                notificationId,
                readAt = readAt.ToUniversalTime().ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // datetime(...) wrap on both sides matches NotificationRepository's
        // strategy: even if a row's observed_at carries a non-UTC offset the
        // chronological compare still wins.
        const string sql = "DELETE FROM notification_events WHERE datetime(observed_at) < datetime(@cutoff);";

        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { cutoff = cutoff.ToUniversalTime().ToString("O") },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetMaxSourceUpdatedAtForThreadAsync(string accountId, string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT source_updated_at
                           FROM notification_events
                           WHERE account_id = @accountId
                             AND notification_id = @notificationId
                           ORDER BY datetime(source_updated_at) DESC
                           LIMIT 1;
                           """;

        var raw = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            sql,
            new { accountId, notificationId },
            cancellationToken: ct)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    public async Task<int> SetActorLoginAsync(long eventId, string actorLogin, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorLogin);
        if (eventId <= 0)
        {
            // 0 means not-yet-persisted; nothing to update.
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Lazy backfill targeting one specific event row by autoincrement id.
        // The caller has just resolved a real author login from a per-thread
        // fetch, so overwriting any prior value with the freshest signal is
        // correct (no COALESCE).
        const string sql = """
                           UPDATE notification_events
                              SET actor_login = @actorLogin
                            WHERE id = @eventId;
                           """;
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { eventId, actorLogin },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static NotificationEvent Map(NotificationEventRow row)
    {
        var subject = new NotificationSubject(row.SubjectType, row.SubjectTitle, row.SubjectApiUrl, row.WebUrl, row.LatestCommentUrl);
        var reason = Enum.TryParse<NotificationReason>(row.Reason, ignoreCase: false, out var parsed)
            ? parsed
            : NotificationReason.Unknown;

        return new NotificationEvent(
            Id: row.Id,
            AccountId: row.AccountId,
            NotificationId: row.NotificationId,
            ThreadId: row.ThreadId,
            RepositoryFullName: row.RepositoryFullName,
            Subject: subject,
            Reason: reason,
            SourceUpdatedAt: DateTimeOffset.Parse(row.SourceUpdatedAt, null, DateTimeStyles.RoundtripKind),
            ObservedAt: DateTimeOffset.Parse(row.ObservedAt, null, DateTimeStyles.RoundtripKind),
            Unread: row.Unread != 0,
            LastReadAt: ParseNullableDate(row.LastReadAt),
            RawJson: row.RawJson ?? string.Empty,
            ActorLogin: row.ActorLogin);
    }

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind);
}
