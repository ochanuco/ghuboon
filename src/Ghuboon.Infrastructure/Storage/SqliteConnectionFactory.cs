using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using Ghuboon.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Opens encrypted SQLite connections using the SQLCipher-compatible
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c> bundle (ADR-011). The encryption key
/// is generated on first use and persisted via <see cref="ICredentialStore"/>
/// (ADR-007). Schema migrations are applied lazily on the first open per process.
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    /// <summary>
    /// Stable credential-store key holding the database encryption key.
    /// </summary>
    public const string DbKeyCredentialKey = "ghuboon.db.key";

    private static int _bundleInitialized;

    private readonly ICredentialStore _credentialStore;
    private readonly string _databasePath;
    private readonly string _credentialKey;
    private readonly MigrationRunner _migrationRunner;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _migrationsApplied;

    /// <summary>
    /// Production constructor: uses the platform default DB path under the
    /// app data directory.
    /// </summary>
    public SqliteConnectionFactory(ICredentialStore credentialStore)
        : this(credentialStore, StoragePaths.GetDefaultDatabasePath(), DbKeyCredentialKey, new MigrationRunner())
    {
    }

    /// <summary>
    /// Test/advanced constructor: caller supplies an explicit DB path,
    /// credential key, and migration set. Used by integration tests so they can
    /// run against a temp file with an in-memory credential store.
    /// </summary>
    public SqliteConnectionFactory(
        ICredentialStore credentialStore,
        string databasePath,
        string credentialKey = DbKeyCredentialKey,
        MigrationRunner? migrationRunner = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _databasePath = !string.IsNullOrWhiteSpace(databasePath)
            ? databasePath
            : throw new ArgumentException("Database path must be provided.", nameof(databasePath));
        _credentialKey = !string.IsNullOrWhiteSpace(credentialKey)
            ? credentialKey
            : throw new ArgumentException("Credential key must be provided.", nameof(credentialKey));
        _migrationRunner = migrationRunner ?? new MigrationRunner();

        EnsureBundleInitialized();
    }

    public string DatabasePath => _databasePath;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        EnsureDirectoryExists(_databasePath);

        var key = await ResolveOrCreateKeyAsync(ct).ConfigureAwait(false);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        try
        {
            await ApplyEncryptionPragmasAsync(connection, key, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await EnsureMigrationsAsync(connection, ct).ConfigureAwait(false);

        return connection;
    }

    private async Task EnsureMigrationsAsync(DbConnection connection, CancellationToken ct)
    {
        if (_migrationsApplied)
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_migrationsApplied)
            {
                return;
            }

            await _migrationRunner.RunAsync(connection, ct).ConfigureAwait(false);
            _migrationsApplied = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> ResolveOrCreateKeyAsync(CancellationToken ct)
    {
        var existing = await _credentialStore.GetAsync(_credentialKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(bytes);
        await _credentialStore.SetAsync(_credentialKey, encoded, ct).ConfigureAwait(false);
        return encoded;
    }

    private static async Task ApplyEncryptionPragmasAsync(DbConnection connection, string key, CancellationToken ct)
    {
        // Pass the key as a quoted string literal. Use SQL string-escaping (double single
        // quotes). Parameter binding is intentionally not used here because PRAGMA does not
        // support parameters in SQLite.
        var escaped = key.Replace("'", "''");
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA key = '{escaped}';",
            cancellationToken: ct)).ConfigureAwait(false);

        // Default cipher_compatibility for bundle_e_sqlcipher is 4; setting it explicitly
        // documents intent and guards against future default changes.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA cipher_compatibility = 4;",
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static void EnsureDirectoryExists(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void EnsureBundleInitialized()
    {
        if (Interlocked.Exchange(ref _bundleInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }
}
