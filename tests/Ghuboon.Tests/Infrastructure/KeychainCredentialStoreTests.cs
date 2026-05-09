using System.Runtime.InteropServices;
using Ghuboon.Infrastructure.Credentials;

namespace Ghuboon.Tests.Infrastructure;

[Trait("Category", "RequiresKeychain")]
public class KeychainCredentialStoreTests
{
    private static bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private const string TestService = "com.ghuboon.test";

    [Fact]
    public async Task Roundtrip_set_get_delete_returns_null_after_delete()
    {
        if (!IsMacOs)
        {
            return; // Skip on non-macOS hosts; Keychain backend is macOS-only.
        }

        var store = new KeychainCredentialStore(TestService);
        var key = "ghuboon.test." + Guid.NewGuid().ToString("N");
        const string value = "synthetic-test-value-not-a-real-secret";

        try
        {
            await store.SetAsync(key, value);

            var read = await store.GetAsync(key);
            Assert.Equal(value, read);

            await store.DeleteAsync(key);

            var afterDelete = await store.GetAsync(key);
            Assert.Null(afterDelete);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("interaction is not allowed", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("could not be authenticated", StringComparison.OrdinalIgnoreCase))
        {
            // CI keychain is locked / no GUI session; treat as inconclusive.
            return;
        }
        finally
        {
            try
            {
                await store.DeleteAsync(key);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task GetAsync_returns_null_for_missing_key()
    {
        if (!IsMacOs)
        {
            return;
        }

        var store = new KeychainCredentialStore(TestService);
        var key = "ghuboon.test.missing." + Guid.NewGuid().ToString("N");

        try
        {
            var result = await store.GetAsync(key);
            Assert.Null(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("interaction is not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_for_missing_key()
    {
        if (!IsMacOs)
        {
            return;
        }

        var store = new KeychainCredentialStore(TestService);
        var key = "ghuboon.test.missing." + Guid.NewGuid().ToString("N");

        try
        {
            await store.DeleteAsync(key); // should not throw
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("interaction is not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
    }
}
