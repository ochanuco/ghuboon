using System.Globalization;
using Dapper;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Notifications;

/// <summary>
/// Lightweight reader/writer over <c>notification_local_states.last_notified_at</c>.
/// Lives here (Notifications/ folder) rather than under Storage/ to keep
/// Phase 11 changes within the OS-notification lane &mdash; the underlying table
/// is owned by Phase 5 (Storage). Uses Dapper directly against
/// <see cref="IDbConnectionFactory"/> so it does not depend on
/// <c>INotificationRepository</c>.
/// </summary>
internal sealed class LastNotifiedTracker
{
    private readonly IDbConnectionFactory _connectionFactory;

    public LastNotifiedTracker(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Returns <see cref="DateTimeOffset"/> of the last OS notification fired
    /// for <paramref name="notificationId"/>, or <c>null</c> if it has never
    /// been notified.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastNotifiedAtAsync(string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT last_notified_at
                           FROM notification_local_states
                           WHERE notification_id = @id;
                           """;

        var raw = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            sql,
            new { id = notificationId },
            cancellationToken: ct)).ConfigureAwait(false);

        return ParseNullableDate(raw);
    }

    /// <summary>
    /// Upserts <c>last_notified_at = at</c> for the given notification. Other
    /// columns on <c>notification_local_states</c> (opened_at, focused_at,
    /// is_hidden) are left untouched on conflict.
    /// </summary>
    public async Task SetLastNotifiedAsync(
        string accountId,
        string notificationId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           INSERT INTO notification_local_states (
                               notification_id, account_id, last_notified_at, is_hidden)
                           VALUES (@id, @account, @at, 0)
                           ON CONFLICT(notification_id) DO UPDATE SET
                               account_id = excluded.account_id,
                               last_notified_at = excluded.last_notified_at;
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = notificationId,
                account = accountId,
                at = at.ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind);
}
