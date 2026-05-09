using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Infrastructure.Storage;

internal sealed class RepositoryRow
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? HtmlUrl { get; set; }
}

/// <summary>
/// Dapper-backed <see cref="IRepositoryRepository"/>.
/// </summary>
public sealed class RepositoryRepository : IRepositoryRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RepositoryRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(RepositoryRef repository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        const string sql = """
                           INSERT INTO repositories (id, account_id, full_name, owner, name, html_url, created_at, updated_at)
                           VALUES (@id, @accountId, @fullName, @owner, @name, @htmlUrl, @now, @now)
                           ON CONFLICT(id) DO UPDATE SET
                             account_id = excluded.account_id,
                             full_name = excluded.full_name,
                             owner = excluded.owner,
                             name = excluded.name,
                             html_url = excluded.html_url,
                             updated_at = excluded.updated_at;
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = repository.Id,
                accountId = repository.AccountId,
                fullName = repository.FullName,
                owner = repository.Owner,
                name = repository.Name,
                htmlUrl = repository.HtmlUrl,
                now = nowIso,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<RepositoryRef?> GetByFullNameAsync(string accountId, string fullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT id AS Id, account_id AS AccountId, full_name AS FullName,
                                  owner AS Owner, name AS Name, html_url AS HtmlUrl
                           FROM repositories
                           WHERE account_id = @accountId AND full_name = @fullName;
                           """;

        var row = await connection.QuerySingleOrDefaultAsync<RepositoryRow>(new CommandDefinition(
            sql,
            new { accountId, fullName },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListByAccountAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
                           SELECT id AS Id, account_id AS AccountId, full_name AS FullName,
                                  owner AS Owner, name AS Name, html_url AS HtmlUrl
                           FROM repositories
                           WHERE account_id = @accountId
                           ORDER BY full_name;
                           """;

        var rows = await connection.QueryAsync<RepositoryRow>(new CommandDefinition(
            sql,
            new { accountId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    private static RepositoryRef Map(RepositoryRow row) => new(
        row.Id,
        row.AccountId,
        row.FullName,
        row.Owner,
        row.Name,
        row.HtmlUrl ?? string.Empty);
}
