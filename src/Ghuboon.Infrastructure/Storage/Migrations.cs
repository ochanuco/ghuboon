namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// All schema migrations the app knows about, in ascending version order.
/// Migration #1 creates the schema described in PLAN.md (Phase 5).
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
                   updated_at TEXT NOT NULL
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
                   synced_at TEXT NOT NULL
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
                 """),
    };
}
