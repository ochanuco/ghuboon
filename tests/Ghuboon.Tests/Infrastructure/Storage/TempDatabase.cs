using System.Data.Common;
using Dapper;
using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Test fixture that creates a fresh temp-file SQLite DB and a wired-up
/// <see cref="SqliteConnectionFactory"/> backed by an in-memory credential store.
/// Disposing the fixture deletes the file. Tests use a temp file (not :memory:)
/// because SQLCipher pragmas behave more predictably against real files.
///
/// Issue #12: Migrations now declare ON DELETE CASCADE foreign keys against
/// <c>accounts(id)</c>. To keep both in-scope storage tests and out-of-scope
/// downstream tests green without forcing every test to seed accounts by hand,
/// <see cref="Factory"/> exposes a thin wrapper that lazily seeds a baseline set
/// of fixture account IDs the first time a connection is opened. Tests that
/// want to verify FK enforcement directly can call <see cref="OpenRawAsync"/> to
/// bypass auto-seeding.
/// </summary>
internal sealed class TempDatabase : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Account IDs the seeding wrapper inserts into <c>accounts</c> on first
    /// open. Covers every account ID referenced by both the Storage tests and
    /// the downstream tests that exercise <c>notification_local_states</c>.
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultSeedAccountIds = new[]
    {
        "acct-1",
        "acct-2",
        "primary",
        "a", "b",
        "a1", "a2",
    };

    /// <summary>
    /// Notification IDs the seeding wrapper inserts into <c>notifications</c>.
    /// Phase 11's <c>HighPriorityNotificationGate</c> writes into
    /// <c>notification_local_states.notification_id</c> for whichever IDs the
    /// caller supplies, without first upserting them as full notifications.
    /// To keep that out-of-scope flow honest under the new FK
    /// (<c>fk_notification_local_states_notification</c>), seed the IDs
    /// those tests use, tied to <c>acct-1</c>.
    ///
    /// Two flavours of IDs are seeded:
    /// <list type="bullet">
    ///   <item><description>
    ///     Full <c>{accountId}:{threadId}</c> form (Lane N's
    ///     <see cref="GitHubNotification"/> invariant) used by
    ///     <c>HighPriorityNotificationGateTests</c> and any flow that goes
    ///     through the gate. The gate writes <c>candidate.Id</c>, which is
    ///     <c>{accountId}:{threadId}</c>, into
    ///     <c>notification_local_states.notification_id</c>.
    ///   </description></item>
    ///   <item><description>
    ///     Bare IDs (<c>n-1</c>, <c>n-2</c>, ...) that
    ///     <c>LastNotifiedTrackerTests</c> writes directly through the
    ///     internal tracker API, bypassing the gate's id construction.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultSeedNotificationIds = new[]
    {
        // Gate tests: full {accountId}:{threadId} form (Lane N invariant).
        "acct-1:n1", "acct-1:n2", "acct-1:n3", "acct-1:n4",
        // Tracker tests: bare IDs the tracker is exercised against directly.
        "n-1", "n-2", "n-3", "n-race", "n-existing", "n-bad",
    };

    private readonly SqliteConnectionFactory _inner;
    private readonly SeedingFactory _seedingFactory;

    public string DatabasePath { get; }
    public InMemoryCredentialStore CredentialStore { get; }
    public IDbConnectionFactory Factory => _seedingFactory;
    public SqliteConnectionFactory RawFactory => _inner;

    /// <param name="seedAccounts">
    /// When <c>true</c> (default), <see cref="Factory"/> auto-seeds the accounts
    /// referenced by most tests so FK constraints don't fail. Set to
    /// <c>false</c> in tests that need to assert against an empty accounts
    /// table (e.g. <see cref="AccountRepository.ListAsync"/> count assertions
    /// or FK-violation tests).
    /// </param>
    /// <param name="seedNotifications">
    /// When <c>true</c> (default), also seeds a small fixture set of
    /// <c>notifications</c> rows under <c>acct-1</c>. Out-of-scope tests rely
    /// on this so <c>notification_local_states</c> inserts pass FK enforcement.
    /// Tests that count rows in <c>notifications</c> should opt out.
    /// Ignored when <paramref name="seedAccounts"/> is <c>false</c>.
    /// </param>
    public TempDatabase(bool seedAccounts = true, bool seedNotifications = true)
    {
        DatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"ghuboon-test-{Guid.NewGuid():N}.db");

        CredentialStore = new InMemoryCredentialStore();
        _inner = new SqliteConnectionFactory(
            CredentialStore,
            DatabasePath,
            "ghuboon.test.db.key");
        _seedingFactory = new SeedingFactory(
            _inner,
            seedAccounts ? DefaultSeedAccountIds : Array.Empty<string>(),
            (seedAccounts && seedNotifications) ? DefaultSeedNotificationIds : Array.Empty<string>());
    }

    /// <summary>
    /// Open a connection without auto-seeding accounts. Use from tests that
    /// need to assert FK violations or inspect the empty schema.
    /// </summary>
    public Task<DbConnection> OpenRawAsync(CancellationToken ct = default) =>
        _inner.OpenAsync(ct);

    public void Dispose()
    {
        TryDelete(DatabasePath);
        // SQLite may produce -wal and -shm sidecars in WAL mode.
        TryDelete(DatabasePath + "-wal");
        TryDelete(DatabasePath + "-shm");
        TryDelete(DatabasePath + "-journal");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    /// <summary>
    /// Wraps an <see cref="IDbConnectionFactory"/> and inserts a fixture set of
    /// <c>accounts</c> rows on first connection. Subsequent opens go straight
    /// through. Idempotent on the underlying file: rows are inserted with
    /// <c>INSERT OR IGNORE</c>.
    /// </summary>
    private sealed class SeedingFactory : IDbConnectionFactory
    {
        private readonly SqliteConnectionFactory _inner;
        private readonly IReadOnlyList<string> _accountIds;
        private readonly IReadOnlyList<string> _notificationIds;
        private readonly SemaphoreSlim _seedLock = new(1, 1);
        private bool _seeded;

        public SeedingFactory(
            SqliteConnectionFactory inner,
            IReadOnlyList<string> accountIds,
            IReadOnlyList<string> notificationIds)
        {
            _inner = inner;
            _accountIds = accountIds;
            _notificationIds = notificationIds;
        }

        public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
        {
            var connection = await _inner.OpenAsync(ct).ConfigureAwait(false);
            if (_seeded)
            {
                return connection;
            }

            await _seedLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_seeded)
                {
                    return connection;
                }

                var nowIso = DateTimeOffset.UtcNow.ToString("O");
                const string accountSql = """
                                          INSERT OR IGNORE INTO accounts
                                              (id, host_url, api_base_url, login, credential_key, created_at, last_validated_at)
                                          VALUES (@id, 'github.com', 'https://api.github.com', NULL, @credentialKey, @createdAt, NULL);
                                          """;

                foreach (var id in _accountIds)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        accountSql,
                        new
                        {
                            id,
                            credentialKey = $"ghuboon.test.{id}.pat",
                            createdAt = nowIso,
                        },
                        cancellationToken: ct)).ConfigureAwait(false);
                }

                if (_notificationIds.Count > 0 && _accountIds.Contains("acct-1"))
                {
                    const string notifSql = """
                                            INSERT OR IGNORE INTO notifications
                                                (id, account_id, thread_id, repository_full_name,
                                                 subject_type, subject_title, subject_api_url, web_url,
                                                 reason, unread, updated_at, last_read_at,
                                                 raw_json, created_at, synced_at)
                                            VALUES (@id, 'acct-1', @thread_id, 'octo/repo',
                                                    'PullRequest', 'Seeded', NULL, NULL,
                                                    'Mention', 1, @now, NULL,
                                                    '{}', @now, @now);
                                            """;

                    foreach (var id in _notificationIds)
                    {
                        // Issue #39: Lane N's invariant is that
                        // GitHubNotification.Id is "{accountId}:{threadId}" while
                        // notifications.thread_id stores the bare upstream thread
                        // id. When the seed id contains ":", bind only the
                        // substring after the colon to thread_id; bare ids stay
                        // as-is. Without this, the seeded thread_id was the full
                        // composite (e.g. "acct-1:n1") rather than "n1".
                        var threadId = id.Contains(':', StringComparison.Ordinal)
                            ? id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..]
                            : id;

                        await connection.ExecuteAsync(new CommandDefinition(
                            notifSql,
                            new { id, thread_id = threadId, now = nowIso },
                            cancellationToken: ct)).ConfigureAwait(false);
                    }
                }

                _seeded = true;
            }
            finally
            {
                _seedLock.Release();
            }

            return connection;
        }
    }
}
