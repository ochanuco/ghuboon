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
        // Every migration in Migrations.All applies on a fresh open. Pin the
        // exact sequence so a missing or reordered migration is caught here
        // before it can ship: v1+v2 = base schema and FKs; v3 adds
        // notification_events for the event-log timeline; v4 backfills
        // historical event rows; v5 adds actor_login; v6 adds
        // latest_comment_url for the EventKind classification; v7 adds the
        // (account_id, notification_id) index that MarkThreadAsRead leans on.
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, versions);

        var tableNames = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"))
            .ToList();

        Assert.Contains("accounts", tableNames);
        Assert.Contains("repositories", tableNames);
        Assert.Contains("notifications", tableNames);
        Assert.Contains("notification_local_states", tableNames);
        Assert.Contains("notification_events", tableNames);
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

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, versions);
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
    public async Task Notification_local_states_uses_composite_primary_key_after_v2()
    {
        // Issue #32 / #42: after migration v2 the local-state primary key is
        // (account_id, notification_id), not notification_id alone, and the
        // FK now points at notifications(account_id, id). Each (account, id)
        // pair must have its own matching notifications row before the
        // local-state insert succeeds, but the composite PK still allows the
        // table to carry independent rows per account — pinned below.
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        // notifications.id is still a global PRIMARY KEY (Issue #42 only
        // adds a per-account UNIQUE for FK targeting), so two notification
        // rows cannot share the same id even under different accounts. Use
        // distinct ids per account and exercise the local-state composite
        // PK by writing one row per (account_id, notification_id) pair.
        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
            VALUES ('acct-1', 'github.com', 'https://api.github.com', NULL, 'k1', '2026-05-01T00:00:00+00:00', NULL),
                   ('acct-2', 'github.com', 'https://api.github.com', NULL, 'k2', '2026-05-01T00:00:00+00:00', NULL);

            INSERT INTO notifications (
                id, account_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, unread, updated_at, last_read_at,
                raw_json, created_at, synced_at)
            VALUES ('shared-1', 'acct-1', 't1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00'),
                   ('shared-2', 'acct-2', 't1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');
            """);

        // Two local-state rows with distinct notification_ids but each
        // attached to the matching parent notification — exercises the
        // composite PK on local-state and the composite FK to notifications.
        await connection.ExecuteAsync(
            """
            INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
            VALUES ('shared-1', 'acct-1', '2026-05-01T00:00:00+00:00', 0),
                   ('shared-2', 'acct-2', '2026-05-02T00:00:00+00:00', 0);
            """);

        var count = await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM notification_local_states;");
        Assert.Equal(2, count);

        // Re-inserting an existing (account_id, notification_id) pair must
        // fail — the composite key still enforces uniqueness within an
        // account.
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
                VALUES ('shared-1', 'acct-1', '2026-06-01T00:00:00+00:00', 0);
                """);
        });
    }

    [Fact]
    public async Task TempDatabase_seeds_thread_id_as_bare_thread_when_id_is_namespaced()
    {
        // Issue #39: TempDatabase's seed loop binds notifications.thread_id
        // from the seeded id. Lane N's invariant is that id has the shape
        // "{accountId}:{threadId}" — so the thread_id column must store only
        // the substring after the colon, never the full composite. Bare ids
        // (no colon) stay as-is.
        await using var temp = new TempDatabase();
        await using var connection = await temp.Factory.OpenAsync();

        // Composite id like "acct-1:n1" → thread_id should be "n1".
        var composite = await connection.QuerySingleAsync<string>(
            "SELECT thread_id FROM notifications WHERE id = 'acct-1:n1';");
        Assert.Equal("n1", composite);

        // Bare id like "n-1" → thread_id stays "n-1".
        var bare = await connection.QuerySingleAsync<string>(
            "SELECT thread_id FROM notifications WHERE id = 'n-1';");
        Assert.Equal("n-1", bare);
    }

    [Fact]
    public async Task Local_state_cannot_attach_to_notification_owned_by_different_account()
    {
        // Issue #42: notification_local_states declares a composite FK on
        // (account_id, notification_id) → notifications(account_id, id). A row
        // that names an existing notification id but a different account_id
        // must fail FK enforcement, otherwise cross-account local-state
        // attachment is possible (the previous single-column FK only checked
        // that *some* notification with that id existed).
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
            VALUES ('acct-1', 'github.com', 'https://api.github.com', NULL, 'k1', '2026-05-01T00:00:00+00:00', NULL),
                   ('acct-2', 'github.com', 'https://api.github.com', NULL, 'k2', '2026-05-01T00:00:00+00:00', NULL);

            INSERT INTO notifications (
                id, account_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, unread, updated_at, last_read_at,
                raw_json, created_at, synced_at)
            VALUES ('n1', 'acct-1', 't1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');
            """);

        // Inserting under acct-1 succeeds (the matching parent exists).
        await connection.ExecuteAsync(
            """
            INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
            VALUES ('n1', 'acct-1', '2026-05-01T00:00:00+00:00', 0);
            """);

        // Inserting under acct-2 with the same notification_id must fail —
        // there is no notifications row with (account_id='acct-2', id='n1').
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
                VALUES ('n1', 'acct-2', '2026-05-02T00:00:00+00:00', 0);
                """);
        });
    }

    [Fact]
    public async Task Migration_v2_upgrades_pre_fk_schema_in_place_preserving_data()
    {
        // Issue #37: hypothetical existing installs were bootstrapped before
        // FK declarations were added to v1, so the live tables on disk have
        // no FK CASCADE. Simulate that by manually creating a v1-without-FK
        // schema and a row of seed data, then running the migration runner.
        // After upgrade the live FK enforcement and composite PK must be in
        // effect, and the seed row must still be present.
        await using var temp = new TempDatabase(seedAccounts: false);

        // Hand-roll a pre-FK v1 schema by opening the connection through the
        // raw factory (which still applies v1+v2 by default), so we instead
        // bypass the runner: open directly via Microsoft.Data.Sqlite, set the
        // encryption pragma, drop everything, and re-create v1 without FKs.
        await using (var raw = await temp.RawFactory.OpenAsync())
        {
            // Wipe whatever the runner just applied.
            await raw.ExecuteAsync(
                """
                DROP TABLE IF EXISTS notification_events;
                DROP TABLE IF EXISTS notification_local_states;
                DROP TABLE IF EXISTS notifications;
                DROP TABLE IF EXISTS sync_states;
                DROP TABLE IF EXISTS repositories;
                DROP TABLE IF EXISTS accounts;
                DROP TABLE IF EXISTS app_settings;
                DROP TABLE IF EXISTS _schema_migrations;
                """);

            // Recreate a stripped-down v1: same columns, no FK constraints,
            // notification_local_states with a single-column PK on
            // notification_id (the legacy shape).
            await raw.ExecuteAsync(
                """
                CREATE TABLE accounts (
                  id TEXT PRIMARY KEY,
                  host_url TEXT NOT NULL,
                  api_base_url TEXT NOT NULL,
                  login TEXT,
                  credential_key TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  last_validated_at TEXT
                );
                CREATE TABLE repositories (
                  id TEXT PRIMARY KEY,
                  account_id TEXT NOT NULL,
                  full_name TEXT NOT NULL,
                  owner TEXT NOT NULL,
                  name TEXT NOT NULL,
                  html_url TEXT,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE notifications (
                  id TEXT PRIMARY KEY,
                  account_id TEXT NOT NULL,
                  thread_id TEXT NOT NULL,
                  repository_full_name TEXT NOT NULL,
                  subject_type TEXT NOT NULL,
                  subject_title TEXT NOT NULL,
                  subject_api_url TEXT,
                  web_url TEXT,
                  reason TEXT NOT NULL,
                  unread INTEGER NOT NULL,
                  updated_at TEXT NOT NULL,
                  last_read_at TEXT,
                  raw_json TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  synced_at TEXT NOT NULL
                );
                CREATE TABLE notification_local_states (
                  notification_id TEXT PRIMARY KEY,
                  account_id TEXT NOT NULL,
                  opened_at TEXT,
                  focused_at TEXT,
                  last_notified_at TEXT,
                  is_hidden INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE sync_states (
                  account_id TEXT PRIMARY KEY,
                  notifications_etag TEXT,
                  last_sync_at TEXT,
                  last_successful_sync_at TEXT,
                  rate_limit_remaining INTEGER,
                  rate_limit_reset_at TEXT
                );
                CREATE TABLE app_settings (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE _schema_migrations (
                  version INTEGER PRIMARY KEY,
                  applied_at TEXT NOT NULL
                );
                INSERT INTO _schema_migrations (version, applied_at)
                VALUES (1, '2026-01-01T00:00:00+00:00');

                INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
                VALUES ('acct-x', 'github.com', 'https://api.github.com', NULL, 'k', '2026-05-01T00:00:00+00:00', NULL);

                INSERT INTO notifications (
                    id, account_id, thread_id, repository_full_name,
                    subject_type, subject_title, subject_api_url, web_url,
                    reason, unread, updated_at, last_read_at,
                    raw_json, created_at, synced_at)
                VALUES ('legacy-1', 'acct-x', 't1', 'octo/repo',
                        'PullRequest', 'T', NULL, NULL,
                        'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                        '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

                INSERT INTO notification_local_states (notification_id, account_id, last_notified_at, is_hidden)
                VALUES ('legacy-1', 'acct-x', '2026-05-01T00:00:00+00:00', 0);
                """);
        }

        // Now run the full migration runner against the legacy DB. It should
        // detect that v1 is already recorded and apply v2, v3, v4, and v5.
        // v3 adds the event-log table; v4 backfills events for already-cached
        // notifications so the timeline isn't empty after the schema change;
        // v5 adds the actor_login column used by the lazy detail-pane
        // backfill.
        var runner = new MigrationRunner();
        await using (var conn = await temp.RawFactory.OpenAsync())
        {
            var applied = await runner.RunAsync(conn);
            Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, applied.ToArray());
        }

        // Verify post-migration state.
        await using (var conn = await temp.RawFactory.OpenAsync())
        {
            // The legacy row survived the rename/copy/drop dance.
            var localStateCount = await conn.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM notification_local_states WHERE notification_id = 'legacy-1';");
            Assert.Equal(1, localStateCount);

            // FK is now live: deleting the parent account cascades to the
            // notification and to the local state.
            await conn.ExecuteAsync("DELETE FROM accounts WHERE id = 'acct-x';");
            Assert.Equal(0, await conn.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM notifications WHERE account_id = 'acct-x';"));
            Assert.Equal(0, await conn.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM notification_local_states WHERE account_id = 'acct-x';"));
        }
    }

    [Fact]
    public async Task Inserting_event_with_unknown_account_fails_with_fk_violation()
    {
        // Migration v3 declares fk_events_account on (account_id) → accounts(id).
        // A row referencing a non-existent account must be rejected so a sync
        // accident cannot leave orphaned event rows behind.
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_events (
                    account_id, notification_id, thread_id, repository_full_name,
                    subject_type, subject_title, subject_api_url, web_url,
                    reason, source_updated_at, observed_at,
                    unread, last_read_at, raw_json)
                VALUES ('no-such-account', 'no-such-account:t1', 't1', 'octo/repo',
                        'PullRequest', 'T', NULL, NULL,
                        'Mention', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00',
                        1, NULL, '{}');
                """);
        });
    }

    [Fact]
    public async Task Inserting_event_with_unknown_notification_fails_with_fk_violation()
    {
        // The composite FK on (account_id, notification_id) → notifications
        // (account_id, id) prevents an event from referencing a notification
        // that does not exist for the account, even when the account itself
        // is valid.
        await using var temp = new TempDatabase(seedAccounts: false);
        await using var connection = await temp.RawFactory.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
            VALUES ('acct-x', 'github.com', 'https://api.github.com', NULL, 'k', '2026-05-01T00:00:00+00:00', NULL);
            """);

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_events (
                    account_id, notification_id, thread_id, repository_full_name,
                    subject_type, subject_title, subject_api_url, web_url,
                    reason, source_updated_at, observed_at,
                    unread, last_read_at, raw_json)
                VALUES ('acct-x', 'acct-x:n-missing', 'n-missing', 'octo/repo',
                        'PullRequest', 'T', NULL, NULL,
                        'Mention', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00',
                        1, NULL, '{}');
                """);
        });
    }

    [Fact]
    public async Task Deleting_account_cascades_to_notification_events()
    {
        // Migration v3 declares ON DELETE CASCADE on the account FK; deleting
        // the account row must wipe its event rows the same way it already
        // wipes notifications and local-state rows.
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
            VALUES ('acct-x:n1', 'acct-x', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notification_events (
                account_id, notification_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, source_updated_at, observed_at,
                unread, last_read_at, raw_json)
            VALUES ('acct-x', 'acct-x:n1', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00',
                    1, NULL, '{}');
            """);

        Assert.Equal(1, await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM notification_events WHERE account_id = 'acct-x';"));

        await connection.ExecuteAsync("DELETE FROM accounts WHERE id = 'acct-x';");

        Assert.Equal(0, await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM notification_events WHERE account_id = 'acct-x';"));
    }

    [Fact]
    public async Task Deleting_notification_cascades_to_notification_events()
    {
        // Composite FK on the notification side cascades event-log rows when
        // the parent notification is removed (e.g. by the 30-day cache prune
        // that targets the notifications table).
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
            VALUES ('acct-x:n1', 'acct-x', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notification_events (
                account_id, notification_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, source_updated_at, observed_at,
                unread, last_read_at, raw_json)
            VALUES ('acct-x', 'acct-x:n1', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00',
                    1, NULL, '{}');
            """);

        await connection.ExecuteAsync("DELETE FROM notifications WHERE id = 'acct-x:n1';");

        Assert.Equal(0, await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM notification_events WHERE notification_id = 'acct-x:n1';"));
    }

    [Fact]
    public async Task Notification_events_dedup_index_blocks_duplicate_observations()
    {
        // ux_events_dedup is a UNIQUE index on
        // (account_id, notification_id, source_updated_at). Re-observing a
        // thread at the same upstream updated_at must be rejected at the
        // index level so TryAppendAsync can rely on it for idempotence.
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
            VALUES ('acct-x:n1', 'acct-x', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', 1, '2026-05-01T00:00:00+00:00', NULL,
                    '{}', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00');

            INSERT INTO notification_events (
                account_id, notification_id, thread_id, repository_full_name,
                subject_type, subject_title, subject_api_url, web_url,
                reason, source_updated_at, observed_at,
                unread, last_read_at, raw_json)
            VALUES ('acct-x', 'acct-x:n1', 'n1', 'octo/repo',
                    'PullRequest', 'T', NULL, NULL,
                    'Mention', '2026-05-01T00:00:00+00:00', '2026-05-01T00:00:00+00:00',
                    1, NULL, '{}');
            """);

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_events (
                    account_id, notification_id, thread_id, repository_full_name,
                    subject_type, subject_title, subject_api_url, web_url,
                    reason, source_updated_at, observed_at,
                    unread, last_read_at, raw_json)
                VALUES ('acct-x', 'acct-x:n1', 'n1', 'octo/repo',
                        'PullRequest', 'T', NULL, NULL,
                        'Mention', '2026-05-01T00:00:00+00:00', '2026-05-09T00:00:00+00:00',
                        1, NULL, '{}');
                """);
        });
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
