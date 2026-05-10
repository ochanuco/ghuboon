using System;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels.Settings;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Settings;

public class AccountSettingsViewModelTests
{
    private const string ValidPat = "ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA00000";

    private static (AccountSettingsViewModel vm,
                    FakeAccountRepository accounts,
                    FakeAppSettingsRepository settings,
                    FakeCredentialStore credentials,
                    FakeGitHubApiClient api) Build()
    {
        var accounts = new FakeAccountRepository();
        var settings = new FakeAppSettingsRepository();
        var credentials = new FakeCredentialStore();
        var api = new FakeGitHubApiClient();
        var appSettings = new AppSettingsService(accounts, settings);
        var validator = new PatValidationService(api);
        var clock = TimeProvider.System;
        var vm = new AccountSettingsViewModel(appSettings, credentials, validator, clock);
        return (vm, accounts, settings, credentials, api);
    }

    [Fact]
    public async Task Validate_WithValidToken_SetsValidStatus()
    {
        var (vm, _, _, _, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null);
        vm.PatInput = ValidPat;

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(PatValidationStatus.Valid, vm.ValidationStatus);
        Assert.Contains("@octocat", vm.ValidationMessage);
        Assert.True(vm.IsValidationValid);
        Assert.False(vm.IsValidationInvalid);
    }

    [Fact]
    public async Task Validate_WithRejectedToken_MapsAuthMessage()
    {
        var (vm, _, _, _, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(false, null, ErrorCategory.Auth, "401");
        vm.PatInput = ValidPat;

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(PatValidationStatus.Invalid, vm.ValidationStatus);
        Assert.Contains("Token rejected", vm.ValidationMessage);
        Assert.Contains("notifications", vm.ValidationMessage);
    }

    [Fact]
    public async Task Validate_WithRateLimit_MapsRateLimitMessage()
    {
        var (vm, _, _, _, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(false, null, ErrorCategory.RateLimit, "403");
        vm.PatInput = ValidPat;

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(PatValidationStatus.Invalid, vm.ValidationStatus);
        Assert.Contains("Rate limit", vm.ValidationMessage);
    }

    [Fact]
    public async Task Save_PersistsCredential_UpsertsAccount_AndClearsInput()
    {
        var (vm, accounts, _, credentials, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null);
        vm.PatInput = ValidPat;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(credentials.Contains(AccountSettingsViewModel.PrimaryCredentialKey));
        Assert.Equal(ValidPat, credentials.Peek(AccountSettingsViewModel.PrimaryCredentialKey));

        Assert.True(accounts.Accounts.TryGetValue(AccountSettingsViewModel.PrimaryAccountId, out var saved));
        Assert.Equal("octocat", saved!.Login);
        Assert.Equal(AccountSettingsViewModel.PrimaryCredentialKey, saved.CredentialKey);
        Assert.NotNull(saved.LastValidatedAt);

        Assert.Equal(string.Empty, vm.PatInput);
        Assert.Equal("octocat", vm.CurrentLogin);
        Assert.True(vm.HasAccount);
    }

    [Fact]
    public async Task Save_WithInvalidToken_DoesNotPersist()
    {
        var (vm, accounts, _, credentials, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(false, null, ErrorCategory.Auth, "bad");
        vm.PatInput = ValidPat;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(credentials.Contains(AccountSettingsViewModel.PrimaryCredentialKey));
        Assert.Empty(accounts.Accounts);
        Assert.Equal(PatValidationStatus.Invalid, vm.ValidationStatus);
        // Input is preserved on failure so the user can correct/retry.
        Assert.Equal(ValidPat, vm.PatInput);
    }

    [Fact]
    public async Task Save_WithEmptyInput_ReportsInvalid()
    {
        var (vm, accounts, _, credentials, _) = Build();
        vm.PatInput = string.Empty;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(PatValidationStatus.Invalid, vm.ValidationStatus);
        Assert.False(credentials.Contains(AccountSettingsViewModel.PrimaryCredentialKey));
        Assert.Empty(accounts.Accounts);
    }

    [Fact]
    public async Task Remove_DeletesCredential_AndClearsLastValidatedAt()
    {
        var (vm, accounts, _, credentials, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null);
        vm.PatInput = ValidPat;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(credentials.Contains(AccountSettingsViewModel.PrimaryCredentialKey));

        await vm.RemoveCommand.ExecuteAsync(null);

        Assert.False(credentials.Contains(AccountSettingsViewModel.PrimaryCredentialKey));
        Assert.True(accounts.Accounts.TryGetValue(AccountSettingsViewModel.PrimaryAccountId, out var stored));
        Assert.Null(stored!.LastValidatedAt);
        Assert.Null(vm.CurrentLogin);
    }

    [Fact]
    public async Task Remove_SurfacesStatusMessage_EvenAfterStatusReset()
    {
        // Issue #18: removing the token resets ValidationStatus to None which
        // hides ValidationMessage. The "Token removed." confirmation must
        // still surface to the user via the dedicated StatusMessage property.
        var (vm, _, _, _, api) = Build();
        api.ValidateImpl = _ => new UserValidationResult(true, "octocat", null, null);
        vm.PatInput = ValidPat;
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(PatValidationStatus.None, vm.ValidationStatus);
        Assert.False(vm.IsValidationVisible, "Validation block must be hidden when status==None.");
        Assert.True(vm.IsStatusMessageVisible);
        Assert.Contains("removed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_LoadsExistingAccount()
    {
        var (vm, accounts, _, _, _) = Build();
        accounts.Accounts[AccountSettingsViewModel.PrimaryAccountId] = new Account(
            AccountSettingsViewModel.PrimaryAccountId,
            "github.com",
            "octocat",
            AccountSettingsViewModel.PrimaryCredentialKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await vm.RefreshAsync();

        Assert.Equal("octocat", vm.CurrentLogin);
        Assert.Equal(AccountSettingsViewModel.PrimaryCredentialKey, vm.CredentialKey);
        Assert.True(vm.HasAccount);
    }
}
