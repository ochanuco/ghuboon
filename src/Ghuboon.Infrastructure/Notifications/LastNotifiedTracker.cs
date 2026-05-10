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

    /// <summary>
    /// Atomically marks <paramref name="notificationId"/> as notified at
    /// <paramref name="at"/> if and only if no prior notification has been
    /// recorded (i.e. <c>last_notified_at IS NULL</c> or the row does not yet
    /// exist). Returns <c>true</c> when this call performed the marking,
    /// <c>false</c> when another caller had already marked it.
    /// </summary>
    /// <remarks>
    /// Implemented as a single SQLite <c>INSERT … ON CONFLICT … DO UPDATE …
    /// WHERE last_notified_at IS NULL</c> statement. SQLite reports the number
    /// of rows actually written (inserted or updated); a result of <c>1</c>
    /// means this caller won the race, <c>0</c> means another writer had
    /// already populated <c>last_notified_at</c>. This eliminates the
    /// Get-then-Set TOCTOU window in the previous gate logic.
    /// </remarks>
    public async Task<bool> TryMarkAsNotifiedAsync(
        string accountId,
        string notificationId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // The WHERE clause on the DO UPDATE branch ensures the update only
        // happens when no prior notification is recorded. If the row already
        // exists with a non-null last_notified_at, the conflict triggers an
        // update whose WHERE filters it out, yielding 0 affected rows.
        const string sql = """
                           INSERT INTO notification_local_states (
                               notification_id, account_id, last_notified_at, is_hidden)
                           VALUES (@id, @account, @at, 0)
                           ON CONFLICT(notification_id) DO UPDATE SET
                               account_id = excluded.account_id,
                               last_notified_at = excluded.last_notified_at
                           WHERE notification_local_states.last_notified_at IS NULL;
                           """;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = notificationId,
                account = accountId,
                at = at.ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// Parses an ISO-8601 round-trip-formatted timestamp written by
    /// <see cref="SetLastNotifiedAsync"/> / <see cref="TryMarkAsNotifiedAsync"/>.
    /// Returns <c>null</c> for null/empty input or any value that cannot be
    /// parsed, rather than throwing &mdash; callers treat "no value" the same
    /// as "unparseable" and the dedup gate fails open (re-notifies) rather
    /// than crashing the sync loop on a corrupted row.
    /// </summary>
    internal static DateTimeOffset? ParseNullableDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return null;
        }

        return parsed;
    }
}
