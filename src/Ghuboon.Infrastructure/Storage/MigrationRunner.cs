using System.Data.Common;
using Dapper;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Applies pending <see cref="Migration"/>s to a SQLite database. Idempotent: each
/// migration version is recorded in <c>_schema_migrations</c> and only applied once.
/// </summary>
public sealed class MigrationRunner
{
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner()
        : this(Migrations.All)
    {
    }

    internal MigrationRunner(IReadOnlyList<Migration> migrations)
    {
        _migrations = migrations.OrderBy(m => m.Version).ToList();
    }

    /// <summary>
    /// Apply any migrations not yet recorded in <c>_schema_migrations</c>.
    /// Returns the list of versions that were applied this call.
    /// </summary>
    public async Task<IReadOnlyList<int>> RunAsync(DbConnection connection, CancellationToken ct = default)
    {
        await EnsureMigrationsTableAsync(connection, ct).ConfigureAwait(false);

        var applied = (await connection.QueryAsync<int>(
            new CommandDefinition("SELECT version FROM _schema_migrations", cancellationToken: ct))
            .ConfigureAwait(false)).ToHashSet();

        var newlyApplied = new List<int>();

        foreach (var migration in _migrations)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                migration.Sql,
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO _schema_migrations (version, applied_at) VALUES (@version, @appliedAt)",
                new { version = migration.Version, appliedAt = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            newlyApplied.Add(migration.Version);
        }

        return newlyApplied;
    }

    private static async Task EnsureMigrationsTableAsync(DbConnection connection, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS _schema_migrations (
              version INTEGER PRIMARY KEY,
              applied_at TEXT NOT NULL
            );
            """,
            cancellationToken: ct)).ConfigureAwait(false);
    }
}
