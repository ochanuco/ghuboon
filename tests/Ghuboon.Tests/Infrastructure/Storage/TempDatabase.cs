using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Test fixture that creates a fresh temp-file SQLite DB and a wired-up
/// <see cref="SqliteConnectionFactory"/> backed by an in-memory credential store.
/// Disposing the fixture deletes the file. Tests use a temp file (not :memory:)
/// because SQLCipher pragmas behave more predictably against real files.
/// </summary>
internal sealed class TempDatabase : IDisposable, IAsyncDisposable
{
    public string DatabasePath { get; }
    public InMemoryCredentialStore CredentialStore { get; }
    public SqliteConnectionFactory Factory { get; }

    public TempDatabase()
    {
        DatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"ghuboon-test-{Guid.NewGuid():N}.db");

        CredentialStore = new InMemoryCredentialStore();
        Factory = new SqliteConnectionFactory(
            CredentialStore,
            DatabasePath,
            "ghuboon.test.db.key");
    }

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
}
