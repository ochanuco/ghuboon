using System.Globalization;
using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Storage;

internal sealed class SyncStateRow
{
    public string AccountId { get; set; } = string.Empty;
    public string? NotificationsEtag { get; set; }
    public string? LastSyncAt { get; set; }
    public string? LastSuccessfulSyncAt { get; set; }
    public long? RateLimitRemaining { get; set; }
    public string? RateLimitResetAt { get; set; }
}

/// <summary>
/// Dapper-backed <see cref="ISyncStateRepository"/>.
/// </summary>
public sealed class SyncStateRepository : ISyncStateRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SyncStateRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<SyncState?> GetAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT account_id AS AccountId,
                                  notifications_etag AS NotificationsEtag,
                                  last_sync_at AS LastSyncAt,
                                  last_successful_sync_at AS LastSuccessfulSyncAt,
                                  rate_limit_remaining AS RateLimitRemaining,
                                  rate_limit_reset_at AS RateLimitResetAt
                           FROM sync_states
                           WHERE account_id = @accountId;
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<SyncStateRow>(new CommandDefinition(
            sql,
            new { accountId },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(SyncState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           INSERT INTO sync_states (
                               account_id, notifications_etag, last_sync_at,
                               last_successful_sync_at, rate_limit_remaining, rate_limit_reset_at)
                           VALUES (
                               @accountId, @notificationsEtag, @lastSyncAt,
                               @lastSuccessfulSyncAt, @rateLimitRemaining, @rateLimitResetAt)
                           ON CONFLICT(account_id) DO UPDATE SET
                               notifications_etag = excluded.notifications_etag,
                               last_sync_at = excluded.last_sync_at,
                               last_successful_sync_at = excluded.last_successful_sync_at,
                               rate_limit_remaining = excluded.rate_limit_remaining,
                               rate_limit_reset_at = excluded.rate_limit_reset_at;
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                accountId = state.AccountId,
                notificationsEtag = state.NotificationsEtag,
                lastSyncAt = state.LastSyncAt?.ToString("O"),
                lastSuccessfulSyncAt = state.LastSuccessfulSyncAt?.ToString("O"),
                rateLimitRemaining = (long?)state.RateLimitRemaining,
                rateLimitResetAt = state.RateLimitResetAt?.ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static SyncState Map(SyncStateRow row) => new(
        row.AccountId,
        ParseNullableDate(row.LastSyncAt),
        ParseNullableDate(row.LastSuccessfulSyncAt),
        row.NotificationsEtag,
        row.RateLimitRemaining is null ? null : checked((int)row.RateLimitRemaining.Value),
        ParseNullableDate(row.RateLimitResetAt));

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind);
}
