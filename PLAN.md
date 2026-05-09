# PLAN.md

# Ghuboon Implementation Plan

Ghuboon is a macOS-first desktop timeline client for GitHub notifications.

The plan is written for incremental implementation and is suitable for use with coding agents. Each phase includes goals, tasks, and acceptance criteria.

## MVP Summary

### Product Goal

Build a Tween-like ALL timeline client that lets the user skim GitHub notifications and open important items on GitHub.com.

### MVP Includes

- macOS-first Avalonia desktop app
- C# / .NET 10
- MVVM structure
- classic PAT authentication
- PAT storage in OS credential store
- encrypted SQLite cache using a SQLCipher-compatible approach
- Dapper-based data access
- HttpClient-based GitHub Notifications API client
- Serilog logging with secret redaction
- ALL timeline
- Tabs: All / Review / Mention / My PRs / Watching
- Repo filter
- Search
- 2-line notification rows with expansion
- Read-on-focus/selection behavior
- Open in GitHub
- Mark as read
- Copy URL
- 5-minute sync interval
- startup sync
- manual sync
- OS notifications for high-priority reasons
- macOS menu bar residency
- 30-day cache retention

### MVP Excludes

- GitHub Enterprise Server custom host support
- OAuth
- GitHub App authentication
- 1Password integration
- `repo` scope requirement
- CI Failed detection
- PR review posting
- Issue comment posting
- diff display
- GitHub Actions log display
- full GitHub.com replacement UI
- dark mode requirement
- multi-column UI
- Windows task tray support
- automatic update
- finalized installer/dmg distribution
- code signing/notarization

---

## Phase 0: Repository and Project Skeleton

### Goal

Create the initial repository structure and baseline .NET solution.

### Tasks

- Create public repository.
- Create `.gitignore`.
- Create `README.md`.
- Add `ADR.md` and `PLAN.md`.
- Create .NET solution.
- Create main Avalonia application project.
- Create test project.
- Add basic directory structure:
  - `src/Ghuboon.App`
  - `src/Ghuboon.Core`
  - `src/Ghuboon.Infrastructure`
  - `tests/Ghuboon.Tests`
- Add formatting/editor config.
- Add initial CI workflow for build and test if desired.

### Acceptance Criteria

- Repository builds locally.
- Avalonia app starts and displays a placeholder window.
- Tests run successfully.
- No secrets or real GitHub notification payloads are committed.

---

## Phase 1: Application Architecture and MVVM Shell

### Goal

Establish the basic application structure and UI shell.

### Tasks

- Add MVVM foundation.
- Create main application services:
  - `IAppSettingsService`
  - `INavigationService` if needed
  - `ITimelineService`
- Create main ViewModels:
  - `MainWindowViewModel`
  - `TimelineViewModel`
  - `TimelineItemViewModel`
  - `SettingsViewModel`
- Create placeholder views:
  - `MainWindow`
  - `TimelineView`
  - `SettingsView`
- Add top bar placeholder:
  - app name
  - sync button
  - unread count placeholder
  - search box placeholder
  - settings button
- Add tab row:
  - All
  - Review
  - Mention
  - My PRs
  - Watching
- Add timeline placeholder data.

### Acceptance Criteria

- App shows the main window with a placeholder timeline.
- Tabs can be selected.
- Search input is visible.
- The app has no GitHub API dependency yet.
- UI logic is represented in ViewModels rather than code-behind where practical.

---

## Phase 2: Domain Model

### Goal

Define the core domain entities and enums.

### Tasks

- Define `Account`.
- Define `RepositoryRef`.
- Define `GitHubNotification`.
- Define `NotificationSubject`.
- Define `NotificationReason`.
- Define `NotificationReadState`.
- Define `TimelineFilter`.
- Define `SyncState`.
- Define reason mapping:
  - `review_requested` -> Review
  - `mention` -> Mention
  - `team_mention` -> Mention or TeamMention
  - `assign` -> Assigned
  - `author` -> MyPr
  - `comment` -> Comment
  - `state_change` -> State
  - `subscribed` -> Watching
- Add unit tests for reason mapping.

### Acceptance Criteria

- Domain models do not depend on Avalonia, SQLite, or HttpClient.
- Reason mapping is tested.
- Unknown reasons are handled safely.

---

## Phase 3: Secure Credential Storage

### Goal

Store PAT and database encryption key using OS credential storage.

### Tasks

- Define `ICredentialStore`.
- Implement macOS credential storage using Keychain-compatible access.
- Add placeholder or interface for future Windows Credential Manager implementation.
- Store PAT under a stable account-specific key.
- Store DB encryption key under an app-specific key.
- Add credential redaction utilities.
- Add settings UI for entering a PAT.
- Validate that saved tokens cannot be displayed again after saving.
- Add token deletion/reset flow.

### Acceptance Criteria

- PAT is not stored in SQLite, config files, or logs.
- PAT can be saved and retrieved from OS credential storage on macOS.
- DB encryption key can be generated and retrieved.
- Logs do not show PAT values.
- Settings UI allows entering/replacing the PAT.

---

## Phase 4: Serilog and Secret Redaction

### Goal

Add structured logging safely.

### Tasks

- Add Serilog.
- Configure file logging to an app data directory.
- Define log retention.
- Implement redaction for:
  - Authorization headers
  - classic PAT-like values
  - fine-grained PAT-like values
  - credential material
- Ensure raw HTTP requests are not logged by default.
- Add error categories:
  - AuthError
  - NetworkError
  - RateLimitError
  - ApiCompatibilityError
  - DatabaseError
  - UnknownError

### Acceptance Criteria

- Logs are written locally.
- Logs contain useful sync and error information.
- Logs never include PATs or Authorization headers.
- Redaction unit tests cover representative token-like strings.

---

## Phase 5: Encrypted SQLite Storage

### Goal

Create the local encrypted database.

### Tasks

- Select SQLCipher-compatible SQLite package compatible with .NET 10, macOS, and future Windows.
- Initialize database with encryption key from credential store.
- Add Dapper.
- Add migration mechanism.
- Create tables:
  - `accounts`
  - `repositories`
  - `notifications`
  - `notification_local_states`
  - `sync_states`
  - `app_settings`
- Include `account_id` in account-scoped tables.
- Store `raw_json`.
- Store ETag and last sync state.
- Add cache pruning for data older than 30 days.

### Suggested Schema

```sql
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
```

### Acceptance Criteria

- Database is encrypted.
- Database opens with key from OS credential storage.
- Tables are created by migration.
- Dapper repositories can insert and query sample notification data.
- Cache pruning deletes records older than 30 days.
- Schema supports multiple accounts even though UI supports one account.

---

## Phase 6: GitHub API Client

### Goal

Implement the GitHub Notifications API client using HttpClient.

### Tasks

- Define `IGitHubApiClient`.
- Implement token-based API requests.
- Add user validation endpoint.
- Add notification listing.
- Add notification thread read operation.
- Add mark all/read operations only if needed.
- Add ETag support using `If-None-Match`.
- Capture response ETag.
- Capture rate limit headers.
- Handle HTTP statuses:
  - 200
  - 304
  - 401
  - 403
  - 404
  - 422
  - 5xx
- Add SAML SSO / organization authorization error messaging when detected.
- Convert API DTOs to domain models.
- Do not require `repo` scope.

### Acceptance Criteria

- PAT can be validated.
- Notifications can be fetched from GitHub.com.
- 304 responses do not overwrite existing cached data.
- Rate limit information is captured.
- Read operation marks a notification thread as read on GitHub.
- Authorization headers are never logged.

---

## Phase 7: Sync Pipeline

### Goal

Build the sync process that connects credentials, GitHub API, database, and UI.

### Tasks

- Define `INotificationSyncService`.
- On startup, load cached notifications first.
- On startup, perform sync.
- Add manual sync command.
- Add 5-minute periodic sync.
- Use ETag from `sync_states`.
- Upsert notifications.
- Preserve local state.
- Detect new notifications since last successful sync.
- Do not trigger OS notifications for already-existing unread items on startup.
- Prune cache older than 30 days after successful sync or startup.
- Update status bar:
  - last sync
  - unread count
  - rate limit remaining
  - sync error state

### Acceptance Criteria

- Cached timeline appears before network sync completes.
- Manual sync works.
- Automatic sync runs every 5 minutes.
- ETag is reused.
- Existing startup unread items do not produce OS notifications.
- Sync failures preserve cached data in the UI.
- Cache pruning works.

---

## Phase 8: Timeline UI

### Goal

Implement the main Tween-like timeline.

### Tasks

- Render notification rows as 2-line items.
- Show unread marker.
- Show reason badge.
- Show repository full name.
- Show subject title.
- Show relative updated time.
- Show second-line context from reason/subject data.
- Implement item expansion.
- Add action buttons on expansion:
  - Open GitHub
  - Mark as read
  - Copy URL
- Add keyboard navigation.
- Mark item as read when it receives focus or selection.
- Keep focused item visually obvious.
- Avoid marking read repeatedly if already read.

### Acceptance Criteria

- Timeline displays real cached notifications.
- Selecting/focusing an unread item marks it as read.
- Mark-as-read syncs to GitHub.
- Opening GitHub opens the correct browser URL.
- Copy URL copies the correct URL.
- Expanded row shows actions.
- Basic keyboard navigation works.

---

## Phase 9: Tabs, Repo Filter, and Search

### Goal

Add timeline filtering behavior.

### Tasks

- Implement tabs:
  - All
  - Review
  - Mention
  - My PRs
  - Watching
- Implement repo filter dropdown.
- Implement search box.
- Search fields:
  - repository full name
  - title
  - reason
  - subject type
- Ensure filters combine predictably:
  - selected tab
  - selected repo
  - search text
- Add empty-state messages.

### Acceptance Criteria

- Tabs filter the timeline.
- Repo filter narrows the selected tab.
- Search narrows the current result set.
- Clearing filters returns to expected ALL view.
- Filtering is fast enough for expected local cache size.

---

## Phase 10: Read State Behavior

### Goal

Finalize read-state semantics.

### Tasks

- On focus/selection:
  - update local focused timestamp
  - mark notification read through GitHub API
  - update local unread state
- On Open in GitHub:
  - open browser
  - mark read if not already read
- On Mark as read:
  - mark read without opening browser
- Add retry behavior for failed read sync.
- Make read operation idempotent.
- Avoid blocking UI during read sync.
- Surface failures unobtrusively.

### Acceptance Criteria

- Focused unread item becomes read.
- Opened item becomes read.
- Explicit mark read works.
- UI remains responsive.
- Failed read operation does not crash the app.
- Subsequent sync reconciles with GitHub state.

---

## Phase 11: OS Notifications

### Goal

Show OS notifications for high-priority new notifications.

### Tasks

- Define `IDesktopNotificationService`.
- Implement macOS notification support.
- Notify only for:
  - `review_requested`
  - `mention`
  - `team_mention`
  - `assign`
- Deduplicate notifications using `last_notified_at`.
- Do not notify for existing unread items on startup.
- Clicking notification should open GitHub.com if feasible.
- If notification click handling is difficult, show notification only and defer click behavior.

### Acceptance Criteria

- New high-priority notifications after initial sync produce OS notifications.
- Lower-priority notifications do not produce OS notifications.
- Existing startup unread notifications do not produce OS notifications.
- Duplicate notifications are suppressed.
- Notification failures do not break sync.

---

## Phase 12: macOS Menu Bar Residency

### Goal

Allow the app to remain available from the macOS menu bar.

### Tasks

- Add macOS menu bar icon.
- Add menu items:
  - Show Ghuboon
  - Sync now
  - Unread count
  - Settings
  - Quit
- Keep sync running while window is hidden if appropriate.
- Ensure app can hide/show main window.
- Define close-window behavior:
  - close hides window rather than quitting, if menu bar mode is enabled.

### Acceptance Criteria

- App appears in macOS menu bar.
- User can show/hide main window from menu bar.
- User can trigger sync from menu bar.
- App can quit from menu bar.
- Main window close behavior is predictable.

---

## Phase 13: Settings UI

### Goal

Create a minimal settings screen.

### Tasks

- Account section:
  - PAT input
  - validate token
  - replace token
  - remove token
- Sync section:
  - show sync interval as 5 minutes
  - manual sync
- Notifications section:
  - enable/disable OS notifications
  - show high-priority reasons
- Cache section:
  - show cache retention as 30 days
  - clear local cache
- Security section:
  - explain credential storage
  - explain encrypted database
- About section:
  - version
  - repository link

### Acceptance Criteria

- User can configure PAT.
- Token validation gives useful feedback.
- Token is not displayed after save.
- User can clear cache.
- User can disable OS notifications.
- Settings do not expose secrets.

---

## Phase 14: Error Handling and Status UX

### Goal

Make failures understandable and non-destructive.

### Tasks

- Implement error categories:
  - AuthError
  - NetworkError
  - RateLimitError
  - ApiCompatibilityError
  - DatabaseError
  - UnknownError
- Show status bar messages:
  - Last sync
  - Sync failed
  - Token invalid
  - Rate limit remaining
  - Offline or network failure
- Add retry affordance.
- Add clear action for invalid token.
- Ensure cached data remains visible during failures.

### Acceptance Criteria

- Invalid token shows a clear message.
- Network failure does not clear timeline.
- Rate limit state is visible.
- Database errors are logged and surfaced safely.
- No error display exposes PAT or raw Authorization headers.

---

## Phase 15: Testing

### Goal

Add meaningful tests around core behavior.

### Tasks

- Unit tests:
  - reason mapping
  - filtering
  - search
  - read-state transitions
  - redaction
  - cache pruning
- Integration tests:
  - SQLite migrations
  - encrypted database open
  - repository CRUD
- HTTP tests:
  - mock GitHub API responses
  - ETag 304 handling
  - 401/403/5xx handling
- UI smoke tests if practical.

### Acceptance Criteria

- Tests cover critical domain and infrastructure behavior.
- Tests do not require real PATs.
- Test fixtures contain synthetic or redacted data.
- CI can run tests without secrets.

---

## Phase 16: Packaging Investigation

### Goal

Investigate distribution path.

### Tasks

- Investigate macOS app bundle.
- Investigate `.dmg` creation.
- Investigate Windows installer options.
- Investigate code signing and notarization requirements.
- Investigate future auto-update options.
- Document findings in `docs/packaging.md`.

### Acceptance Criteria

- Packaging options are documented.
- MVP can still be run locally without installer.
- No premature auto-update implementation is added.

---

## Future Work

### Multi-account UI

- Add multiple account settings.
- Allow account switching or merged timeline.
- Show account badge on notification rows.

### Windows Support

- Implement Windows Credential Manager.
- Implement Windows task tray.
- Implement Windows notifications.
- Package Windows installer.

### GitHub Enterprise Server

- Add custom host URL.
- Add API base URL setting.
- Validate server API compatibility.
- Handle Enterprise Server notification API differences.

### Enhanced Metadata

- Optional `repo` scope mode.
- Fetch labels.
- Fetch PR/Issue state.
- Fetch review status.
- Fetch check status.
- Add CI Failed tab.

### Multi-column UI

- Add TweetDeck-like columns.
- Allow saved column filters.
- Persist column layout.

### Distribution

- macOS `.dmg`
- Windows installer
- Code signing
- Notarization
- Auto-update strategy

---

## Implementation Guardrails

- Do not store PAT in SQLite.
- Do not store PAT in config files.
- Do not log PAT.
- Do not commit real notification payloads.
- Do not require `repo` scope for MVP.
- Do not implement GitHub.com replacement features in MVP.
- Do not add CI Failed detection until enhanced metadata is explicitly planned.
- Do not implement OAuth/GitHub App/1Password in MVP.
- Keep the app focused on skim-reading notifications.

