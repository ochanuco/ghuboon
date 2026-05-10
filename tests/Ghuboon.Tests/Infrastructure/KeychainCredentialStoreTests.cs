using Ghuboon.Infrastructure.Credentials;

namespace Ghuboon.Tests.Infrastructure;

[Trait("Category", "RequiresKeychain")]
public class KeychainCredentialStoreTests
{
    private const string TestService = "com.ghuboon.test";

    /// <summary>
    /// Unified inconclusive-failure detector for headless CI keychains.
    /// CI hosts often have a locked Keychain or no GUI session, which surfaces as
    /// either "interaction is not allowed" or "could not be authenticated" from
    /// <c>/usr/bin/security</c>. Both should be treated as inconclusive in tests.
    /// </summary>
    private static bool IsInconclusiveKeychainFailure(Exception ex) =>
        ex.Message.Contains("interaction is not allowed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("could not be authenticated", StringComparison.OrdinalIgnoreCase);

    [MacOnlyFact]
    public async Task Roundtrip_set_get_delete_returns_null_after_delete()
    {
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
        catch (InvalidOperationException ex) when (IsInconclusiveKeychainFailure(ex))
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

    [MacOnlyFact]
    public async Task GetAsync_returns_null_for_missing_key()
    {
        var store = new KeychainCredentialStore(TestService);
        var key = "ghuboon.test.missing." + Guid.NewGuid().ToString("N");

        try
        {
            var result = await store.GetAsync(key);
            Assert.Null(result);
        }
        catch (InvalidOperationException ex) when (IsInconclusiveKeychainFailure(ex))
        {
            return;
        }
    }

    [MacOnlyFact]
    public async Task DeleteAsync_is_idempotent_for_missing_key()
    {
        var store = new KeychainCredentialStore(TestService);
        var key = "ghuboon.test.missing." + Guid.NewGuid().ToString("N");

        try
        {
            await store.DeleteAsync(key); // should not throw
        }
        catch (InvalidOperationException ex) when (IsInconclusiveKeychainFailure(ex))
        {
            return;
        }
    }
}
