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
    /// for <paramref name="notificationId"/> under the (caller-implicit) account
    /// scope, or <c>null</c> if it has never been notified.
    /// </summary>
    /// <remarks>
    /// Issue #32: <c>notification_local_states</c> is keyed by
    /// <c>(account_id, notification_id)</c>. The legacy single-id lookup is
    /// kept here for backwards compatibility with tests that hard-code the
    /// gate-form id (<c>{accountId}:{threadId}</c>) into the column. When more
    /// than one row exists for the same notification id (cross-account), we
    /// take the most recent <c>last_notified_at</c> so the gate degrades to
    /// "we have already notified this thread" rather than missing the row.
    /// New callers should prefer
    /// <see cref="GetLastNotifiedAtAsync(string, string, CancellationToken)"/>
    /// for an explicit account scope.
    /// </remarks>
    public async Task<DateTimeOffset?> GetLastNotifiedAtAsync(string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT last_notified_at
                           FROM notification_local_states
                           WHERE notification_id = @id
                           ORDER BY last_notified_at DESC NULLS LAST
                           LIMIT 1;
                           """;

        var raw = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            sql,
            new { id = notificationId },
            cancellationToken: ct)).ConfigureAwait(false);

        return ParseNullableDate(raw);
    }

    /// <summary>
    /// Account-scoped variant of
    /// <see cref="GetLastNotifiedAtAsync(string, CancellationToken)"/>. Returns
    /// the <c>last_notified_at</c> for the row keyed by
    /// <c>(accountId, notificationId)</c>, or <c>null</c> if no such row
    /// exists.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastNotifiedAtAsync(
        string accountId,
        string notificationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT last_notified_at
                           FROM notification_local_states
                           WHERE account_id = @account AND notification_id = @id;
                           """;

        var raw = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            sql,
            new { id = notificationId, account = accountId },
            cancellationToken: ct)).ConfigureAwait(false);

        return ParseNullableDate(raw);
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
        DateTimeOffset eventAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Fire when the candidate event's timestamp is strictly newer than
        // any prior notification we recorded for this thread. We also
        // re-notify when the column is null (first time seeing the thread).
        // The previous IS NULL gate fired only once per thread for life,
        // which is wrong for an event-log timeline: a Draft → Open
        // transition or a fresh comment on an authored PR has its own
        // source_updated_at and deserves its own banner.
        //
        // last_notified_at stores the candidate's source_updated_at (so we
        // can compare future events against the same axis), not now(). The
        // event-log timeline already uses source_updated_at for ordering.
        //
        // Issue #32: composite conflict key (account_id, notification_id) so
        // two accounts that observe the same notification id stay separate.
        // The DO UPDATE branch never touches account_id (cannot change for
        // an existing primary key).
        const string sql = """
                           INSERT INTO notification_local_states (
                               notification_id, account_id, last_notified_at, is_hidden)
                           VALUES (@id, @account, @at, 0)
                           ON CONFLICT(account_id, notification_id) DO UPDATE SET
                               last_notified_at = excluded.last_notified_at
                           WHERE notification_local_states.last_notified_at IS NULL
                              OR datetime(notification_local_states.last_notified_at) < datetime(excluded.last_notified_at);
                           """;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = notificationId,
                account = accountId,
                at = eventAt.ToUniversalTime().ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// Parses an ISO-8601 round-trip-formatted timestamp written by
    /// <see cref="TryMarkAsNotifiedAsync"/>. Returns <c>null</c> for null/empty
    /// input or any value that cannot be parsed, rather than throwing &mdash;
    /// callers treat "no value" the same as "unparseable" and the dedup gate
    /// fails open (re-notifies) rather than crashing the sync loop on a
    /// corrupted row.
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
