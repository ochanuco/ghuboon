using System.Globalization;
using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Storage;

internal sealed class NotificationRow
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string RepositoryFullName { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectTitle { get; set; } = string.Empty;
    public string? SubjectApiUrl { get; set; }
    public string? WebUrl { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long Unread { get; set; }
    public string UpdatedAt { get; set; } = string.Empty;
    public string? LastReadAt { get; set; }
    public string? ActorLogin { get; set; }
}

/// <summary>
/// Dapper-backed <see cref="INotificationRepository"/>. Persists the original
/// <c>raw_json</c> payload alongside structured columns so the cache can be
/// re-derived without further API calls (ADR-010).
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public NotificationRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(GitHubNotification notification, string rawJson, DateTimeOffset syncedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        rawJson ??= string.Empty;

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Issue #12: persist all timestamps in UTC so range queries
        // (e.g. ORDER BY updated_at, synced_at < @cutoff) sort lexically the
        // same way they sort chronologically. Without normalization, ISO-8601
        // strings carrying different offsets sort by string compare in ways
        // that diverge from absolute time.
        // Issue #25: COALESCE incoming last_read_at with the existing column
        // value so a sync that re-upserts a remote-still-unread notification
        // (last_read_at = null) does not clobber a locally-set read marker.
        // actor_login uses the same COALESCE(excluded, existing) trick as
        // last_read_at so a sync upsert that doesn't know the actor (the
        // listing API never includes it) can't clobber a value the detail
        // pane lazily back-filled. The detail-pane back-fill goes through
        // SetActorLoginAsync rather than UpsertAsync, so this guard is the
        // belt-and-braces backstop for future call sites.
        const string sql = """
                           INSERT INTO notifications (
                               id, account_id, thread_id, repository_full_name,
                               subject_type, subject_title, subject_api_url, web_url,
                               reason, unread, updated_at, last_read_at,
                               raw_json, created_at, synced_at, actor_login)
                           VALUES (
                               @id, @accountId, @threadId, @repositoryFullName,
                               @subjectType, @subjectTitle, @subjectApiUrl, @webUrl,
                               @reason, @unread, @updatedAt, @lastReadAt,
                               @rawJson, @createdAt, @syncedAt, @actorLogin)
                           ON CONFLICT(id) DO UPDATE SET
                               account_id = excluded.account_id,
                               thread_id = excluded.thread_id,
                               repository_full_name = excluded.repository_full_name,
                               subject_type = excluded.subject_type,
                               subject_title = excluded.subject_title,
                               subject_api_url = excluded.subject_api_url,
                               web_url = excluded.web_url,
                               reason = excluded.reason,
                               unread = excluded.unread,
                               updated_at = excluded.updated_at,
                               last_read_at = COALESCE(excluded.last_read_at, last_read_at),
                               raw_json = excluded.raw_json,
                               synced_at = excluded.synced_at,
                               actor_login = COALESCE(excluded.actor_login, actor_login);
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = notification.Id,
                accountId = notification.AccountId,
                threadId = notification.ThreadId,
                repositoryFullName = notification.RepositoryFullName,
                subjectType = notification.Subject.Type,
                subjectTitle = notification.Subject.Title,
                subjectApiUrl = notification.Subject.ApiUrl,
                webUrl = notification.Subject.WebUrl,
                reason = notification.Reason.ToString(),
                unread = notification.Unread ? 1L : 0L,
                updatedAt = notification.UpdatedAt.ToUniversalTime().ToString("O"),
                lastReadAt = notification.LastReadAt?.ToUniversalTime().ToString("O"),
                rawJson = rawJson,
                createdAt = syncedAt.ToUniversalTime().ToString("O"),
                syncedAt = syncedAt.ToUniversalTime().ToString("O"),
                actorLogin = notification.ActorLogin,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<GitHubNotification?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT id AS Id, account_id AS AccountId, thread_id AS ThreadId,
                                  repository_full_name AS RepositoryFullName,
                                  subject_type AS SubjectType, subject_title AS SubjectTitle,
                                  subject_api_url AS SubjectApiUrl, web_url AS WebUrl,
                                  reason AS Reason, unread AS Unread,
                                  updated_at AS UpdatedAt, last_read_at AS LastReadAt,
                                  actor_login AS ActorLogin
                           FROM notifications
                           WHERE id = @id;
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<NotificationRow>(new CommandDefinition(
            sql,
            new { id },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubNotification>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Issue #37: a backlog of records persisted by older builds may have
        // updated_at values with non-UTC offsets. Lexical sort on the raw
        // column would then diverge from chronological order. Normalize by
        // ordering on datetime(updated_at), which SQLite parses as the
        // chronological UTC timestamp regardless of the original offset.
        // Once a back-fill migration normalizes the column the wrap is a
        // no-op, but it keeps mixed-offset DBs honest in the meantime.
        const string sql = """
                           SELECT id AS Id, account_id AS AccountId, thread_id AS ThreadId,
                                  repository_full_name AS RepositoryFullName,
                                  subject_type AS SubjectType, subject_title AS SubjectTitle,
                                  subject_api_url AS SubjectApiUrl, web_url AS WebUrl,
                                  reason AS Reason, unread AS Unread,
                                  updated_at AS UpdatedAt, last_read_at AS LastReadAt,
                                  actor_login AS ActorLogin
                           FROM notifications
                           WHERE account_id = @accountId
                           ORDER BY datetime(updated_at) DESC;
                           """;

        var rows = await connection.QueryAsync<NotificationRow>(new CommandDefinition(
            sql,
            new { accountId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Normalize cutoff to UTC so the lexical compare against synced_at
        // (also stored UTC, see UpsertAsync) is consistent with chronological
        // ordering regardless of caller offset.
        // Issue #37: legacy rows may have synced_at written with a non-UTC
        // offset, in which case raw lexical compare would over- or
        // under-delete. Wrap synced_at and the parameter in datetime() so
        // SQLite compares chronological UTC values on both sides.
        const string sql = "DELETE FROM notifications WHERE datetime(synced_at) < datetime(@cutoff);";
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { cutoff = cutoff.ToUniversalTime().ToString("O") },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> SetActorLoginAsync(string id, string actorLogin, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorLogin);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Lazy backfill from the detail-pane fetch: only update the matching
        // row, and only when the row exists. We don't COALESCE here because
        // the caller (TimelineItemViewModel) already gates the call on a
        // non-null author login it just resolved from a per-thread fetch —
        // overwriting any prior value with the freshest signal is correct.
        const string sql = """
                           UPDATE notifications
                              SET actor_login = @actorLogin
                            WHERE id = @id;
                           """;
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { id, actorLogin },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static GitHubNotification Map(NotificationRow row)
    {
        var subject = new NotificationSubject(row.SubjectType, row.SubjectTitle, row.SubjectApiUrl, row.WebUrl);
        var reason = Enum.TryParse<NotificationReason>(row.Reason, ignoreCase: false, out var parsed)
            ? parsed
            : NotificationReason.Unknown;

        return new GitHubNotification(
            row.Id,
            row.AccountId,
            row.ThreadId,
            row.RepositoryFullName,
            subject,
            reason,
            row.Unread != 0,
            DateTimeOffset.Parse(row.UpdatedAt, null, DateTimeStyles.RoundtripKind),
            ParseNullableDate(row.LastReadAt),
            row.ActorLogin);
    }

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind);
}
