using Dapper;
using Ghuboon.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

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
    public async Task ForeignKeys_are_enforced_on_each_connection()
    {
        // Issue #12: SQLite enforces FK constraints only when
        // PRAGMA foreign_keys = ON for the connection. Verify the factory
        // sets the pragma for every connection so FK declarations are live.
        await using var temp = new TempDatabase(seedAccounts: false);

        await using var connection = await temp.RawFactory.OpenAsync();
        var on = await connection.QuerySingleAsync<long>("PRAGMA foreign_keys;");
        Assert.Equal(1, on);
    }

    [Fact]
    public async Task Inserting_repository_with_unknown_account_fails_with_fk_violation()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO repositories (id, account_id, full_name, owner, name, html_url, created_at, updated_at)
                VALUES ('r1', 'no-such-account', 'octo/repo', 'octo', 'repo', NULL, '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');
                """);
        });
    }

    [Fact]
    public async Task Inserting_notification_with_unknown_account_fails_with_fk_violation()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notifications (
                    id, account_id, thread_id, repository_full_name,
                    subject_type, subject_title, subject_api_url, web_url,
                    reason, unread, updated_at, last_read_at,
                    raw_json, created_at, synced_at)
                VALUES ('n1', 'no-such-account', 't1', 'octo/repo',
                        'PullRequest', 'T', NULL, NULL,
                        'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                        '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');
                """);
        });
    }

    [Fact]
    public async Task Deleting_account_cascades_to_repositories_and_notifications()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        // Seed an account, a repo, a notification, and a local state for that
        // notification, all owned by the same account.
        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
            VALUES ('acct-x', 'github.com', 'https://api.github.com', NULL, 'k', '2026-05-01T00:00:00+00:00', NULL);

            INSERT INTO repositories (id, account_id, full_name, owner, name, html_url, created_at, updated_at)
            VALUES ('r1', 'acct-x', 'octo/repo', 'octo', 'repo', NULL,
                    '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notifications (
                id, account_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, unread, updated_at, last_read_at,
                raw_json, created_at, synced_at)
            VALUES ('n1', 'acct-x', 't1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notification_local_states (notification_id, account_id, opened_at, focused_at, last_notified_at, is_hidden)
            VALUES ('n1', 'acct-x', NULL, NULL, '2026-05-01T00:00:00+00:00', 0);

            INSERT INTO sync_states (account_id, notifications_etag, last_sync_at, last_successful_sync_at, rate_limit_remaining, rate_limit_reset_at)
            VALUES ('acct-x', NULL, NULL, NULL, NULL, NULL);
            """);

        // Sanity: rows exist.
        Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM repositories WHERE account_id = 'acct-x';"));
        Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM notifications WHERE account_id = 'acct-x';"));
        Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM notification_local_states WHERE account_id = 'acct-x';"));
        Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM sync_states WHERE account_id = 'acct-x';"));

        await connection.ExecuteAsync("DELETE FROM accounts WHERE id = 'acct-x';");

        // ON DELETE CASCADE wipes children.
        Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM repositories WHERE account_id = 'acct-x';"));
        Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM notifications WHERE account_id = 'acct-x';"));
        Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM notification_local_states WHERE account_id = 'acct-x';"));
        Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM sync_states WHERE account_id = 'acct-x';"));
    }

    [Fact]
    public async Task Deleting_notification_cascades_to_local_state()
    {
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
            VALUES ('acct-x', 'github.com', 'https://api.github.com', NULL, 'k', '2026-05-01T00:00:00+00:00', NULL);

            INSERT INTO notifications (
                id, account_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, unread, updated_at, last_read_at,
                raw_json, created_at, synced_at)
            VALUES ('n1', 'acct-x', 't1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notification_local_states (notification_id, account_id, opened_at, focused_at, last_notified_at, is_hidden)
            VALUES ('n1', 'acct-x', NULL, NULL, '2026-05-01T00:00:00+00:00', 0);
            """);

        await connection.ExecuteAsync("DELETE FROM notifications WHERE id = 'n1';");

        Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM notification_local_states WHERE notification_id = 'n1';"));
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
