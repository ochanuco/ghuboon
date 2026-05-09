using System.Text;
using Ghuboon.Core.Domain;
using Ghuboon.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Ghuboon.Tests.Infrastructure.Storage;

public class EncryptionAtRestTests
{
    [Fact]
    public async Task Database_file_does_not_contain_plaintext_subject_title()
    {
        // Sanity check that PRAGMA key actually engages SQLCipher encryption: a
        // distinctive marker we wrote should not appear as plaintext in the
        // raw bytes of the DB file.
        var marker = "GHUBOON_PLAINTEXT_MARKER_" + Guid.NewGuid().ToString("N");

        await using var temp = new TempDatabase();
        var notifications = new NotificationRepository(temp.Factory);

        var notif = new GitHubNotification(
            "acct-1:thread-marker", "acct-1", "thread-marker", "ochanuco/ghuboon",
            new NotificationSubject("PullRequest", marker, null, null),
            NotificationReason.Mention, true,
            DateTimeOffset.UtcNow, null);

        await notifications.UpsertAsync(notif, marker, DateTimeOffset.UtcNow);

        // Force a checkpoint and close to ensure data is flushed to disk.
        await using (var conn = await temp.Factory.OpenAsync())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(temp.DatabasePath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_cannot_be_opened_with_a_different_key()
    {
        await using var temp = new TempDatabase();

        // Write something so the DB is initialized.
        await using (var conn = await temp.Factory.OpenAsync())
        {
        }
        SqliteConnection.ClearAllPools();

        // Build a second factory pointing at the same file but with a fresh,
        // empty credential store. It will generate a *different* random key,
        // and opening must fail.
        var rogueStore = new InMemoryCredentialStore();
        var rogueFactory = new SqliteConnectionFactory(
            rogueStore,
            temp.DatabasePath,
            "ghuboon.test.rogue.key");

        await Assert.ThrowsAnyAsync<SqliteException>(async () =>
        {
            await using var conn = await rogueFactory.OpenAsync();
        });
    }
}
