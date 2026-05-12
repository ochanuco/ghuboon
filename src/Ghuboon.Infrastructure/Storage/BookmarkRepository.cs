using Dapper;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Dapper-backed <see cref="IBookmarkRepository"/>. Persists in
/// <c>notification_local_states.bookmarked_at</c> so the flag survives
/// the per-thread retention prune (local-states are kept separately).
/// </summary>
public sealed class BookmarkRepository : IBookmarkRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public BookmarkRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task SetAsync(string accountId, string notificationId, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // UPSERT — INSERT a fresh local-states row when none exists yet
        // (e.g. the thread was just observed and hasn't been opened or
        // banner'd), otherwise overwrite bookmarked_at. account_id is in
        // the composite primary key so we don't touch it in the update.
        const string sql = """
                           INSERT INTO notification_local_states (
                               notification_id, account_id, bookmarked_at, is_hidden)
                           VALUES (@id, @account, @at, 0)
                           ON CONFLICT(account_id, notification_id) DO UPDATE SET
                               bookmarked_at = excluded.bookmarked_at;
                           """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = notificationId,
                account = accountId,
                at = at.ToUniversalTime().ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task ClearAsync(string accountId, string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Targeted UPDATE only — clearing a bookmark must not delete the
        // whole local-state row (last_notified_at / opened_at could still
        // be live). When the row doesn't exist yet, there's nothing to
        // clear, which is the desired no-op.
        const string sql = """
                           UPDATE notification_local_states
                              SET bookmarked_at = NULL
                            WHERE account_id = @account
                              AND notification_id = @id;
                           """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { id = notificationId, account = accountId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetBookmarkedIdsAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT notification_id
                           FROM notification_local_states
                           WHERE account_id = @account
                             AND bookmarked_at IS NOT NULL;
                           """;
        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new { account = accountId },
            cancellationToken: ct)).ConfigureAwait(false);

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }
}
