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

        const string sql = """
                           INSERT INTO notifications (
                               id, account_id, thread_id, repository_full_name,
                               subject_type, subject_title, subject_api_url, web_url,
                               reason, unread, updated_at, last_read_at,
                               raw_json, created_at, synced_at)
                           VALUES (
                               @id, @accountId, @threadId, @repositoryFullName,
                               @subjectType, @subjectTitle, @subjectApiUrl, @webUrl,
                               @reason, @unread, @updatedAt, @lastReadAt,
                               @rawJson, @createdAt, @syncedAt)
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
                               last_read_at = excluded.last_read_at,
                               raw_json = excluded.raw_json,
                               synced_at = excluded.synced_at;
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
                updatedAt = notification.UpdatedAt.ToString("O"),
                lastReadAt = notification.LastReadAt?.ToString("O"),
                rawJson = rawJson,
                createdAt = syncedAt.ToString("O"),
                syncedAt = syncedAt.ToString("O"),
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
                                  updated_at AS UpdatedAt, last_read_at AS LastReadAt
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

        const string sql = """
                           SELECT id AS Id, account_id AS AccountId, thread_id AS ThreadId,
                                  repository_full_name AS RepositoryFullName,
                                  subject_type AS SubjectType, subject_title AS SubjectTitle,
                                  subject_api_url AS SubjectApiUrl, web_url AS WebUrl,
                                  reason AS Reason, unread AS Unread,
                                  updated_at AS UpdatedAt, last_read_at AS LastReadAt
                           FROM notifications
                           WHERE account_id = @accountId
                           ORDER BY updated_at DESC;
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

        const string sql = "DELETE FROM notifications WHERE synced_at < @cutoff;";
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { cutoff = cutoff.ToString("O") },
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
            ParseNullableDate(row.LastReadAt));
    }

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind);
}
