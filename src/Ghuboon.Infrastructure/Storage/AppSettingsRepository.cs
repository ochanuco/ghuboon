using Dapper;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Dapper-backed <see cref="IAppSettingsRepository"/>.
/// </summary>
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AppSettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM app_settings WHERE key = @key;",
            new { key },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           INSERT INTO app_settings (key, value, updated_at)
                           VALUES (@key, @value, @updatedAt)
                           ON CONFLICT(key) DO UPDATE SET
                               value = excluded.value,
                               updated_at = excluded.updated_at;
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { key, value, updatedAt = DateTimeOffset.UtcNow.ToString("O") },
            cancellationToken: ct)).ConfigureAwait(false);
    }
}
