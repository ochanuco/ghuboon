using System;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels.Settings;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Settings;

/// <summary>
/// Phase 15: failure-path coverage for <see cref="AccountSettingsViewModel"/>.
/// </summary>
public class AccountSettingsViewModelExtraTests
{
    private const string ValidPat = "ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA00000";

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("keychain unavailable");

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task Save_WhenCredentialStoreThrows_DoesNotClearPatInput_AndDoesNotPersistAccount()
    {
        var accounts = new FakeAccountRepository();
        var settings = new FakeAppSettingsRepository();
        var credentials = new ThrowingCredentialStore();
        var api = new FakeGitHubApiClient
        {
            ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null),
        };
        var appSettings = new AppSettingsService(accounts, settings);
        var validator = new PatValidationService(api);
        var vm = new AccountSettingsViewModel(appSettings, credentials, validator, TimeProvider.System);
        vm.PatInput = ValidPat;

        // Current behavior: the credential-store throw escapes SaveAsync — there's
        // no try/catch around the keychain call. The PAT input must NOT have been
        // cleared (clearing happens only after a successful upsert).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.SaveCommand.ExecuteAsync(null));

        Assert.Equal(ValidPat, vm.PatInput);
        // No account row was upserted because the throw happened before the
        // account write.
        Assert.Empty(accounts.Accounts);
    }
}
