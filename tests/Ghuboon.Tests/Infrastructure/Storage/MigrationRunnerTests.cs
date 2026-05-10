using Dapper;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public async Task First_open_applies_initial_schema()
    {
        await using var temp = new TempDatabase();

        await using var connection = await temp.Factory.OpenAsync();

        var versions = await connection.QueryAsync<int>(
            "SELECT version FROM _schema_migrations ORDER BY version;");
        Assert.Equal(new[] { 1 }, versions);

        var tableNames = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"))
            .ToList();

        Assert.Contains("accounts", tableNames);
        Assert.Contains("repositories", tableNames);
        Assert.Contains("notifications", tableNames);
        Assert.Contains("notification_local_states", tableNames);
        Assert.Contains("sync_states", tableNames);
        Assert.Contains("app_settings", tableNames);
        Assert.Contains("_schema_migrations", tableNames);
    }

    [Fact]
    public async Task Second_open_is_idempotent()
    {
        await using var temp = new TempDatabase();

        await using (var connection1 = await temp.Factory.OpenAsync())
        {
        }

        // A fresh factory simulates "next process startup": the in-memory cached
        // _migrationsApplied flag is reset, so the runner inspects the DB again
        // and must not duplicate any migrations.
        var factory2 = new SqliteConnectionFactory(
            temp.CredentialStore,
            temp.DatabasePath,
            "ghuboon.test.db.key");

        await using var connection2 = await factory2.OpenAsync();

        var versions = (await connection2.QueryAsync<int>(
                "SELECT version FROM _schema_migrations ORDER BY version;"))
            .ToList();

        Assert.Equal(new[] { 1 }, versions);
    }

    [Fact]
    public void Constructor_throws_when_migrations_share_version()
    {
        // Issue #12: duplicate Version values would silently shadow each other,
        // so the runner must refuse to construct. The exception message names
        // the offending versions to make diagnosis trivial.
        var migrations = new[]
        {
            new Migration(1, "first", "CREATE TABLE a (id INTEGER);"),
            new Migration(1, "second", "CREATE TABLE b (id INTEGER);"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigrationRunner(migrations));
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void Constructor_lists_all_duplicate_versions()
    {
        var migrations = new[]
        {
            new Migration(1, "a", "SELECT 1;"),
            new Migration(2, "b", "SELECT 1;"),
            new Migration(2, "b2", "SELECT 1;"),
            new Migration(3, "c", "SELECT 1;"),
            new Migration(3, "c2", "SELECT 1;"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigrationRunner(migrations));
        Assert.Contains("2", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public async Task Encryption_key_is_persisted_to_credential_store()
    {
        await using var temp = new TempDatabase();

        Assert.False(temp.CredentialStore.Contains("ghuboon.test.db.key"));

        await using var connection = await temp.Factory.OpenAsync();

        Assert.True(temp.CredentialStore.Contains("ghuboon.test.db.key"));
        var key = await temp.CredentialStore.GetAsync("ghuboon.test.db.key");
        Assert.NotNull(key);
        // 32 bytes Base64 is 44 chars (with padding).
        Assert.Equal(44, key!.Length);
    }
}
