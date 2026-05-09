# ADR.md

# Ghuboon Architecture Decision Records

This document records architectural decisions for **Ghuboon**, a Tween-like timeline client for GitHub notifications.

## Project Context

Ghuboon is a desktop application for skimming GitHub Pull Request / Issue notifications in an ALL timeline, then opening important items on GitHub.com.

The application does **not** aim to replace GitHub.com. Detailed review, commenting, diff viewing, CI log inspection, and issue editing are delegated to GitHub.com.

## Status Legend

- Accepted: decided and used as project direction
- Deferred: intentionally postponed
- Rejected: explicitly not chosen for MVP

---

## ADR-001: Use C# and .NET 10

**Status:** Accepted

### Context

Ghuboon is intended to be a desktop application for macOS first, with future Windows support. The implementation language should fit desktop application development, cross-platform packaging, and long-term maintainability.

### Decision

Use **C#** and **.NET 10**.

### Consequences

- The application can use the modern .NET ecosystem.
- Cross-platform desktop GUI frameworks such as Avalonia are available.
- The project can remain within a single language/runtime for UI, API access, local persistence, and background sync.
- .NET 10 compatibility should be checked across Avalonia, SQLite, SQLCipher-compatible packages, and packaging tools.

---

## ADR-002: Use Avalonia for the Desktop UI

**Status:** Accepted

### Context

Ghuboon targets macOS first and Windows later. WPF and WinUI are Windows-focused. .NET MAUI is cross-platform but less attractive for a desktop-first timeline client. The app needs a dense, desktop-oriented UI.

### Decision

Use **Avalonia UI**.

### Consequences

- macOS and Windows can share most UI implementation.
- MVVM patterns are naturally supported.
- The UI can be designed as a dense desktop timeline rather than a mobile-style interface.
- Platform-specific features such as macOS menu bar residency and future Windows task tray support still require dedicated implementation.

---

## ADR-003: Use MVVM Architecture

**Status:** Accepted

### Context

The application has a stateful UI: timeline items, tabs, filters, search, sync state, read state, and notification state.

### Decision

Use **MVVM** as the UI architecture.

### Consequences

- UI state can be tested separately from Avalonia views.
- Timeline behavior, selection, filtering, and read-state transitions can be modeled in ViewModels.
- The application can separate presentation logic from GitHub API access, storage, credentials, and notifications.

---

## ADR-004: Use a Tween-like ALL Timeline as the Primary UI

**Status:** Accepted

### Context

The intended use is to skim notifications, not deeply read or process them inside the app. The user expects roughly 20 repositories and a notification velocity much lower than Twitter.

### Decision

Use a **Tween-like ALL timeline** as the primary view.

MVP tabs:

- All
- Review
- Mention
- My PRs
- Watching

### Consequences

- The application prioritizes flow and skim-read behavior.
- Repository tabs are not used as the primary navigation model.
- Repo filtering is still provided for narrowing the ALL timeline.
- Multi-column views are deferred as a future enhancement.

---

## ADR-005: Delegate Detailed PR/Issue Viewing to GitHub.com

**Status:** Accepted

### Context

Implementing full PR/Issue viewing, review submission, diff browsing, comments, and CI log inspection would significantly expand scope.

### Decision

Ghuboon opens relevant PRs/issues on **GitHub.com** for detailed work.

The app provides:

- Timeline overview
- Reason badges
- Repository and title information
- Basic expansion
- Open in GitHub
- Mark as read
- Copy URL

### Consequences

- The MVP remains focused.
- `repo` scope can be avoided for most cases.
- Some rich metadata such as labels, checks, and CI status may be unavailable in MVP.
- The product remains a notification client, not a GitHub Desktop replacement.

---

## ADR-006: Use Classic PAT Instead of OAuth, GitHub App, or 1Password Integration

**Status:** Accepted

### Context

OAuth and GitHub App authentication can be more user-friendly but add complexity and may be awkward for organization or enterprise-managed environments. The user also considered 1Password integration, but no official .NET SDK path was selected for MVP.

### Decision

Use **classic Personal Access Token** authentication for MVP.

Do not implement:

- OAuth
- GitHub App authentication
- 1Password CLI/SDK integration
- Fine-grained PAT as the primary path

### Consequences

- Authentication is simple and predictable.
- The user is responsible for creating and rotating the PAT.
- Security handling becomes critical.
- The app must clearly guide the user to use minimal scopes.

---

## ADR-007: Store PAT in the OS Credential Store

**Status:** Accepted

### Context

PAT leakage is a major concern. The token must not be stored in SQLite, plain config files, logs, or crash reports.

### Decision

Store PATs only in the OS credential store.

- macOS: Keychain
- Windows: Windows Credential Manager

SQLite stores only a credential reference key, not the PAT itself.

### Consequences

- The application avoids storing secrets in its local database.
- Token access remains platform-specific.
- Credential storage abstraction is required.
- Token values must never be logged or shown after saving.

---

## ADR-008: Use `notifications` Scope Only for MVP

**Status:** Accepted

### Context

Ghuboon is designed to list and manage notifications, then open details on GitHub.com. Broader `repo` scope increases the blast radius of PAT leakage.

### Decision

Recommend **classic PAT with `notifications` scope** for MVP.

Do not require `repo` scope in MVP.

### Consequences

- The app minimizes required token permissions.
- Private repository PR/Issue detailed metadata may not be available through API.
- MVP avoids CI/check details and rich PR/Issue metadata.
- Future enhanced metadata features may require a separate opt-in decision.

---

## ADR-009: Use HttpClient Instead of Octokit.NET

**Status:** Accepted

### Context

Ghuboon mainly needs a small subset of the GitHub REST API: notifications, notification thread read state, and user validation.

### Decision

Use **HttpClient** directly rather than Octokit.NET for MVP.

### Consequences

- API calls remain explicit and easy to audit.
- Authentication, ETag, rate limit, and error handling are controlled directly.
- More boilerplate is required.
- If the API surface grows significantly, Octokit.NET can be reconsidered.

---

## ADR-010: Use SQLite + Dapper for Local Cache

**Status:** Accepted

### Context

The app needs a local cache for notifications, repositories, accounts, sync state, ETags, UI state, and read state.

### Decision

Use **SQLite** for local persistence and **Dapper** for data access.

### Consequences

- The persistence layer remains lightweight.
- SQL remains explicit.
- Migrations must be implemented.
- The schema should support multiple accounts even though the MVP UI supports one account.

---

## ADR-011: Encrypt SQLite Database with a SQLCipher-Compatible Approach

**Status:** Accepted

### Context

The local database stores private repository names, PR/Issue titles, raw GitHub notification JSON, and local read state. Although PATs are not stored in SQLite, the notification data itself may be sensitive.

### Decision

Encrypt SQLite using a **SQLCipher-compatible approach**.

The database encryption key is generated on first launch and stored in the OS credential store.

### Consequences

- Local notification metadata is protected at rest.
- The app depends on a SQLite encryption library that must work on .NET 10, macOS, and future Windows builds.
- Startup requires retrieving the DB key from the OS credential store.
- Key rotation and recovery are deferred.

---

## ADR-012: Use Serilog for Structured Logging

**Status:** Accepted

### Context

The app needs diagnostics for sync failures, API errors, database issues, and platform-specific behavior.

### Decision

Use **Serilog** for structured logging.

### Consequences

- Logs can include structured events and error categories.
- A redaction layer is mandatory.
- Logs must never contain PATs, Authorization headers, credential references that expose secrets, or raw request headers.

---

## ADR-013: Design Database for Multiple Accounts While MVP UI Supports One Account

**Status:** Accepted

### Context

Multiple accounts are desirable, but implementing full multi-account UI in MVP adds complexity.

### Decision

Design the schema with account support from the beginning.

MVP UI supports a single configured account.

### Consequences

- Tables include `account_id`.
- Future multi-account UI can be added without major schema rewrites.
- MVP avoids account switching and aggregation complexity.
- Sync logic can be written with account boundaries in mind.

---

## ADR-014: Mark Notification as Read When Timeline Item Receives Focus/Selection

**Status:** Accepted

### Context

The desired behavior is that reading from the timeline should mark an item as read. The app is a skim-reading client, so explicit open is not required for read state.

### Decision

When a notification item receives focus or is selected in the timeline, mark it as read.

Also mark as read when:

- Opened in GitHub
- Explicitly marked as read

### Consequences

- Keyboard-driven reading becomes fast.
- Accidental selection may mark items as read.
- The UI should make the current focused item obvious.
- The read operation should be synced to GitHub.

---

## ADR-015: Use macOS Menu Bar Residency for MVP

**Status:** Accepted

### Context

The app is intended to be used during work and should remain available without occupying constant window focus.

### Decision

Implement **macOS menu bar residency** in MVP.

Windows task tray support is deferred.

### Consequences

- macOS-first usage is supported.
- Platform-specific menu bar behavior must be implemented.
- Future Windows support needs a separate task tray implementation.
- Startup and background sync behavior must account for menu bar residency.

---

## ADR-016: Exclude GitHub Enterprise Server from MVP

**Status:** Accepted

### Context

The target organization usage is `github.com/company`, which corresponds to GitHub Enterprise Cloud organization usage, not GitHub Enterprise Server with a custom host.

### Decision

MVP targets:

- GitHub.com personal account
- GitHub.com organizations

MVP does not support GitHub Enterprise Server custom hosts.

### Consequences

- API base URL can default to `https://api.github.com`.
- SAML SSO authorization errors for organizations should be displayed clearly.
- Custom enterprise host settings are deferred.
- The app can still model host/API URL internally if useful, but full compatibility testing is not required.

---

## ADR-017: Exclude CI Failed Detection from MVP

**Status:** Accepted

### Context

CI/check status detection may require additional API calls and broader permissions depending on repository visibility and metadata needs.

### Decision

Do not implement CI Failed detection in MVP.

### Consequences

- MVP remains limited to notification reasons available from the Notifications API.
- The initial tabs exclude CI Failed.
- CI-related features can be revisited if the app later opts into enhanced metadata.
- `repo` scope remains unnecessary for MVP.

---

## ADR-018: Exclude Dark Mode Requirement from MVP

**Status:** Accepted

### Context

Dark mode is not required for the initial version.

### Decision

Do not require dark mode support in MVP.

### Consequences

- UI theming complexity is reduced.
- Avalonia theme defaults may still support system appearance depending on implementation.
- Explicit dark-mode polish is deferred.

---

## ADR-019: Include Repo Filter and Search in MVP

**Status:** Accepted

### Context

The app will handle around 20 repositories. ALL timeline is primary, but repo filtering and search help locate notifications quickly.

### Decision

Include both:

- Repo filter
- Search

in MVP.

### Consequences

- Timeline data needs efficient filtering.
- Search scope should initially cover repository full name, title, reason, and subject type.
- Full-text indexing can be deferred unless simple filtering is insufficient.

---

## ADR-020: Use 5-Minute Sync Interval

**Status:** Accepted

### Context

GitHub notifications are not expected to move as fast as social timelines. Polling too frequently wastes API quota and battery.

### Decision

Use a **5-minute automatic sync interval**.

Also support:

- Sync on startup
- Manual sync

### Consequences

- API usage remains moderate.
- Notifications are near-real-time enough for the intended use.
- ETag support should be implemented to reduce unnecessary payloads.
- Startup existing unread notifications should not produce OS notifications.

---

## ADR-021: Use OS Notifications for High-Priority Reasons Only

**Status:** Accepted

### Context

The app should not be noisy during work. OS notifications should be limited to high-signal events.

### Decision

Use OS notification functionality for:

- `review_requested`
- `mention`
- `team_mention`
- `assign`

Do not notify on startup for already-existing unread items.

### Consequences

- Important notifications can surface without overwhelming the user.
- Lower-priority notifications still appear in the timeline.
- Platform-specific notification implementation is required.
- Notification deduplication is required.

---

## ADR-022: Cache Notifications for 30 Days

**Status:** Accepted

### Context

Local cache is useful for startup display, offline use, filtering, and search. Long retention is unnecessary and increases local sensitive data.

### Decision

Cache notifications for **30 days**.

### Consequences

- Local data volume stays bounded.
- Private repository names and PR/Issue titles are retained for a limited period.
- A pruning job should run after sync or on startup.
- Raw JSON older than 30 days should be deleted.

---

## ADR-023: Packaging Is Desired but Distribution Strategy Is Deferred

**Status:** Deferred

### Context

The user wants installer/dmg eventually, but initial distribution and update strategy are undecided.

### Decision

Defer distribution strategy.

Desired future outputs:

- macOS `.dmg` or app bundle
- Windows installer

Undecided:

- Automatic updates
- Code signing
- macOS notarization
- Release channel

### Consequences

- Initial development can use local builds.
- Packaging investigation is a planned phase.
- Public OSS development does not imply production-grade distribution on day one.

---

## ADR-024: Develop in a Public Repository

**Status:** Accepted

### Context

The project starts as self-use but may become OSS.

### Decision

Develop in a **public repository**.

### Consequences

- No secrets, PATs, private organization names, or sample private notification payloads may be committed.
- Fixtures must be synthetic or sanitized.
- Logs and raw JSON samples used in tests must be redacted.
- Security-related defaults should assume public scrutiny.

