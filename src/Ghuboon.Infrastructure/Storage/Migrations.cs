namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// All schema migrations the app knows about, in ascending version order.
/// Migration #1 creates the schema described in PLAN.md (Phase 5).
/// Migration #2 (issues #32, #37, #42) re-creates the FK-constrained tables to
/// guarantee that pre-#12 installations gain the ON DELETE CASCADE foreign
/// keys, switches <c>notification_local_states</c> to a composite primary key
/// on <c>(account_id, notification_id)</c> so the same notification id from
/// two different accounts can be tracked independently, and tightens the
/// local-state FK to <c>(account_id, notification_id) → notifications(account_id, id)</c>
/// so a local-state row cannot point at a notification owned by a different
/// account.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> All = new[]
    {
        new Migration(
            Version: 1,
            Name: "initial_schema",
            Sql: """
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
                   updated_at TEXT NOT NULL,
                   CONSTRAINT fk_repositories_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 CREATE UNIQUE INDEX ux_repositories_account_full_name
                   ON repositories(account_id, full_name);

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
                   synced_at TEXT NOT NULL,
                   CONSTRAINT fk_notifications_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 CREATE INDEX ix_notifications_account_updated
                   ON notifications(account_id, updated_at DESC);

                 CREATE INDEX ix_notifications_synced_at
                   ON notifications(synced_at);

                 CREATE TABLE notification_local_states (
                   notification_id TEXT PRIMARY KEY,
                   account_id TEXT NOT NULL,
                   opened_at TEXT,
                   focused_at TEXT,
                   last_notified_at TEXT,
                   is_hidden INTEGER NOT NULL DEFAULT 0,
                   CONSTRAINT fk_notification_local_states_notification
                     FOREIGN KEY (notification_id) REFERENCES notifications(id) ON DELETE CASCADE,
                   CONSTRAINT fk_notification_local_states_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 CREATE TABLE sync_states (
                   account_id TEXT PRIMARY KEY,
                   notifications_etag TEXT,
                   last_sync_at TEXT,
                   last_successful_sync_at TEXT,
                   rate_limit_remaining INTEGER,
                   rate_limit_reset_at TEXT,
                   CONSTRAINT fk_sync_states_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 CREATE TABLE app_settings (
                   key TEXT PRIMARY KEY,
                   value TEXT NOT NULL,
                   updated_at TEXT NOT NULL
                 );
                 """),

        // ----------------------------------------------------------------
        // Migration v2 (issues #32, #37, #42):
        //   * Add composite PK (account_id, notification_id) to
        //     notification_local_states so a notification id observed under
        //     two accounts can be tracked independently. v1's PK on
        //     notification_id alone allowed cross-account suppression.
        //   * Re-create repositories / notifications / sync_states /
        //     notification_local_states with the same ON DELETE CASCADE FKs
        //     that v1 already declares — this is a no-op for DBs created
        //     after #12 landed, but upgrades any pre-#12 v1 install that was
        //     bootstrapped before the FK tightening.
        //   * Issue #42: tighten the notification_local_states → notifications
        //     FK from a single-column reference on notifications(id) to a
        //     composite reference on notifications(account_id, id). Without
        //     the composite reference, a local-state row could legally point
        //     at a notification owned by a different account (the FK only
        //     checked that *some* notification with that id existed). The
        //     replacement notifications table therefore declares
        //     UNIQUE (account_id, id), which the composite FK requires per
        //     SQLite's foreign-key spec. id is already PRIMARY KEY so the
        //     extra unique index is redundant for row identity but serves as
        //     the FK target SQLite needs.
        //   * SQLite has no ALTER TABLE ADD CONSTRAINT, so each affected
        //     table follows the rename / create / copy / drop dance from
        //     https://www.sqlite.org/lang_altertable.html#otheralter.
        //
        // FK enforcement during the migration:
        //   PRAGMA foreign_keys = ON is set on every connection by
        //   SqliteConnectionFactory. Inside this migration we set
        //   PRAGMA defer_foreign_keys = ON for the duration of the
        //   transaction so the rename/create/copy/drop sequence does not
        //   trip the constraints mid-flight. defer_foreign_keys resets to
        //   OFF at COMMIT, so it does not leak past this migration.
        //
        // Indexes are recreated explicitly because SQLite drops indexes when
        // their table is dropped, even if we kept the same name on the
        // replacement table.
        //
        // Pre-release note: this migration was authored as part of the same
        // pre-release Phase as the FK upgrade above. There is no shipped data
        // to preserve, so editing v2 in place (rather than authoring a v3) is
        // acceptable and keeps the upgrade story to a single hop. Any DB on
        // disk has been created in the same dev cycle as this code.
        new Migration(
            Version: 2,
            Name: "composite_local_state_key_and_fk_upgrade",
            Sql: """
                 PRAGMA defer_foreign_keys = ON;

                 -- repositories ------------------------------------------------
                 ALTER TABLE repositories RENAME TO repositories_v1;

                 CREATE TABLE repositories (
                   id TEXT PRIMARY KEY,
                   account_id TEXT NOT NULL,
                   full_name TEXT NOT NULL,
                   owner TEXT NOT NULL,
                   name TEXT NOT NULL,
                   html_url TEXT,
                   created_at TEXT NOT NULL,
                   updated_at TEXT NOT NULL,
                   CONSTRAINT fk_repositories_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 INSERT INTO repositories (
                     id, account_id, full_name, owner, name, html_url,
                     created_at, updated_at)
                 SELECT id, account_id, full_name, owner, name, html_url,
                        created_at, updated_at
                 FROM repositories_v1;

                 DROP TABLE repositories_v1;

                 CREATE UNIQUE INDEX ux_repositories_account_full_name
                   ON repositories(account_id, full_name);

                 -- notifications ----------------------------------------------
                 ALTER TABLE notifications RENAME TO notifications_v1;

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
                   synced_at TEXT NOT NULL,
                   CONSTRAINT fk_notifications_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE,
                   -- Issue #42: notification_local_states declares a composite
                   -- FK on (account_id, notification_id). SQLite requires the
                   -- referenced columns to carry a UNIQUE or PRIMARY KEY
                   -- constraint, so expose (account_id, id) as a candidate
                   -- key here. id alone is already PRIMARY KEY; this extra
                   -- constraint just gives the FK its target.
                   CONSTRAINT ux_notifications_account_id
                     UNIQUE (account_id, id)
                 );

                 INSERT INTO notifications (
                     id, account_id, thread_id, repository_full_name,
                     subject_type, subject_title, subject_api_url, web_url,
                     reason, unread, updated_at, last_read_at,
                     raw_json, created_at, synced_at)
                 SELECT id, account_id, thread_id, repository_full_name,
                        subject_type, subject_title, subject_api_url, web_url,
                        reason, unread, updated_at, last_read_at,
                        raw_json, created_at, synced_at
                 FROM notifications_v1;

                 -- notification_local_states is dropped and recreated below;
                 -- its FK references notifications(id) so we must keep the
                 -- old table alive until the data copy completes.

                 -- notification_local_states ----------------------------------
                 ALTER TABLE notification_local_states
                   RENAME TO notification_local_states_v1;

                 CREATE TABLE notification_local_states (
                   notification_id TEXT NOT NULL,
                   account_id TEXT NOT NULL,
                   opened_at TEXT,
                   focused_at TEXT,
                   last_notified_at TEXT,
                   is_hidden INTEGER NOT NULL DEFAULT 0,
                   PRIMARY KEY (account_id, notification_id),
                   -- Issue #42: composite FK on (account_id, notification_id)
                   -- → notifications(account_id, id) prevents a local-state
                   -- row from attaching to a notification owned by a
                   -- different account. The previous single-column FK only
                   -- required the notification id to exist *somewhere* in
                   -- notifications, which let cross-account state leak
                   -- through if two accounts ever observed the same id.
                   CONSTRAINT fk_notification_local_states_notification
                     FOREIGN KEY (account_id, notification_id)
                       REFERENCES notifications(account_id, id) ON DELETE CASCADE,
                   CONSTRAINT fk_notification_local_states_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 INSERT INTO notification_local_states (
                     notification_id, account_id, opened_at, focused_at,
                     last_notified_at, is_hidden)
                 SELECT notification_id, account_id, opened_at, focused_at,
                        last_notified_at, is_hidden
                 FROM notification_local_states_v1;

                 DROP TABLE notification_local_states_v1;
                 DROP TABLE notifications_v1;

                 CREATE INDEX ix_notifications_account_updated
                   ON notifications(account_id, updated_at DESC);

                 CREATE INDEX ix_notifications_synced_at
                   ON notifications(synced_at);

                 -- sync_states ------------------------------------------------
                 ALTER TABLE sync_states RENAME TO sync_states_v1;

                 CREATE TABLE sync_states (
                   account_id TEXT PRIMARY KEY,
                   notifications_etag TEXT,
                   last_sync_at TEXT,
                   last_successful_sync_at TEXT,
                   rate_limit_remaining INTEGER,
                   rate_limit_reset_at TEXT,
                   CONSTRAINT fk_sync_states_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
                 );

                 INSERT INTO sync_states (
                     account_id, notifications_etag, last_sync_at,
                     last_successful_sync_at, rate_limit_remaining,
                     rate_limit_reset_at)
                 SELECT account_id, notifications_etag, last_sync_at,
                        last_successful_sync_at, rate_limit_remaining,
                        rate_limit_reset_at
                 FROM sync_states_v1;

                 DROP TABLE sync_states_v1;
                 """),

        // ----------------------------------------------------------------
        // Migration v3 (event log timeline):
        //   Adds the notification_events table: append-only event log rows
        //   that back the timeline UI. Each per-fetch observation that sees
        //   a different upstream updated_at on a thread becomes one row, so
        //   a PR going Open -> Draft -> Open over multiple sync windows
        //   produces three rows rather than overwriting a single per-thread
        //   row in the existing notifications table.
        //
        //   The composite FK (account_id, notification_id) -> notifications
        //   (account_id, id) leans on the UNIQUE (account_id, id) constraint
        //   that v2 added to notifications, and an event row is dropped via
        //   ON DELETE CASCADE when its parent account or notification row is
        //   removed.
        //
        //   Indexes:
        //     ix_events_account_observed -> ListByAccountAsync paging.
        //     ux_events_dedup -> guarantees TryAppend returns false for a
        //                       repeat observation at the same upstream
        //                       updated_at (no duplicate rows on retry).
        new Migration(
            Version: 3,
            Name: "notification_events",
            Sql: """
                 CREATE TABLE notification_events (
                   id INTEGER PRIMARY KEY AUTOINCREMENT,
                   account_id TEXT NOT NULL,
                   notification_id TEXT NOT NULL,
                   thread_id TEXT NOT NULL,
                   repository_full_name TEXT NOT NULL,
                   subject_type TEXT NOT NULL,
                   subject_title TEXT NOT NULL,
                   subject_api_url TEXT,
                   web_url TEXT,
                   reason TEXT NOT NULL,
                   source_updated_at TEXT NOT NULL,
                   observed_at TEXT NOT NULL,
                   unread INTEGER NOT NULL,
                   last_read_at TEXT,
                   raw_json TEXT NOT NULL,
                   CONSTRAINT fk_events_account
                     FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE,
                   CONSTRAINT fk_events_notification
                     FOREIGN KEY (account_id, notification_id)
                       REFERENCES notifications(account_id, id) ON DELETE CASCADE
                 );

                 CREATE INDEX ix_events_account_observed
                   ON notification_events(account_id, observed_at DESC);

                 CREATE UNIQUE INDEX ux_events_dedup
                   ON notification_events(account_id, notification_id, source_updated_at);
                 """),

        // ----------------------------------------------------------------
        // Migration v4 (event log backfill):
        //   v3 introduced notification_events but only newly upserted
        //   notifications append events. Existing cached notifications (from
        //   pre-v3 fetches) had no event rows, so the timeline rendered
        //   empty until a fresh upstream change came in. This migration
        //   seeds one event per existing notification using its latest
        //   updated_at as the source_updated_at and synced_at (or
        //   updated_at as a fallback) as the observed_at. The UNIQUE
        //   (account_id, notification_id, source_updated_at) index makes
        //   the INSERT OR IGNORE idempotent — if v3 already wrote an
        //   identical row it is skipped here.
        new Migration(
            Version: 4,
            Name: "notification_events_backfill",
            Sql: """
                 INSERT OR IGNORE INTO notification_events (
                   account_id, notification_id, thread_id, repository_full_name,
                   subject_type, subject_title, subject_api_url, web_url, reason,
                   source_updated_at, observed_at, unread, last_read_at, raw_json
                 )
                 SELECT
                   account_id,
                   id AS notification_id,
                   thread_id,
                   repository_full_name,
                   subject_type,
                   subject_title,
                   subject_api_url,
                   web_url,
                   reason,
                   updated_at AS source_updated_at,
                   COALESCE(synced_at, updated_at) AS observed_at,
                   unread,
                   last_read_at,
                   raw_json
                 FROM notifications;
                 """),
    };
}
