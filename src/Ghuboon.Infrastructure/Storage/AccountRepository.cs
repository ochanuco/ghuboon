using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Storage;

internal sealed class AccountRow
{
    public string Id { get; set; } = string.Empty;
    public string HostUrl { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string? Login { get; set; }
    public string CredentialKey { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? LastValidatedAt { get; set; }
}

/// <summary>
/// Dapper-backed <see cref="IAccountRepository"/> over the encrypted SQLite cache.
/// </summary>
public sealed class AccountRepository : IAccountRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AccountRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
                           VALUES (@id, @hostUrl, @apiBaseUrl, @login, @credentialKey, @createdAt, @lastValidatedAt)
                           ON CONFLICT(id) DO UPDATE SET
                             host_url = excluded.host_url,
                             api_base_url = excluded.api_base_url,
                             login = excluded.login,
                             credential_key = excluded.credential_key,
                             last_validated_at = excluded.last_validated_at;
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = account.Id,
                hostUrl = account.Host,
                apiBaseUrl = DeriveApiBaseUrl(account.Host),
                login = account.Login,
                credentialKey = account.CredentialKey,
                createdAt = account.CreatedAt.ToString("O"),
                lastValidatedAt = account.LastValidatedAt?.ToString("O"),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Account?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT id AS Id, host_url AS HostUrl, api_base_url AS ApiBaseUrl,
                                  login AS Login, credential_key AS CredentialKey,
                                  created_at AS CreatedAt, last_validated_at AS LastValidatedAt
                           FROM accounts
                           WHERE id = @id;
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            sql,
            new { id },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT id AS Id, host_url AS HostUrl, api_base_url AS ApiBaseUrl,
                                  login AS Login, credential_key AS CredentialKey,
                                  created_at AS CreatedAt, last_validated_at AS LastValidatedAt
                           FROM accounts
                           ORDER BY created_at;
                           """;

        var rows = await connection.QueryAsync<AccountRow>(new CommandDefinition(
            sql,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    private static Account Map(AccountRow row) => new(
        row.Id,
        row.HostUrl,
        row.Login ?? string.Empty,
        row.CredentialKey,
        DateTimeOffset.Parse(row.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        ParseNullableDate(row.LastValidatedAt));

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    internal static string DeriveApiBaseUrl(string? host)
    {
        // ADR-016: MVP targets github.com but the schema/UI accept Enterprise
        // hosts too. Normalize the input (trim, ensure scheme, parse Uri) and
        // dispatch off the parsed Uri.Host case-insensitively so callers don't
        // have to think about scheme presence or trailing slashes.
        var input = (host ?? string.Empty).Trim().Trim('/');
        if (input.Length == 0)
        {
            return "https://api.github.com";
        }

        var withScheme = input.Contains("://", StringComparison.Ordinal)
            ? input
            : "https://" + input;

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
        {
            return "https://api.github.com";
        }

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.github.com";
        }

        // Enterprise: <scheme>://<host>[:<port>]/api/v3
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/api/v3");
        return builder.Uri.ToString().TrimEnd('/');
    }
}
