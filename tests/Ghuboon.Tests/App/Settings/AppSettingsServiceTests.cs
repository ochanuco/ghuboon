using System;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Settings;

/// <summary>
/// Issue #18: ctor null-checks and primary-id normalization on
/// <see cref="AppSettingsService"/>.
/// </summary>
public class AppSettingsServiceTests
{
    [Fact]
    public void Ctor_NullAccountRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AppSettingsService(null!, new FakeAppSettingsRepository()));
    }

    [Fact]
    public void Ctor_NullSettingsRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AppSettingsService(new FakeAccountRepository(), null!));
    }

    [Fact]
    public async Task UpsertPrimaryAccountAsync_NullAccount_Throws()
    {
        var svc = new AppSettingsService(new FakeAccountRepository(), new FakeAppSettingsRepository());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => svc.UpsertPrimaryAccountAsync(null!));
    }

    [Fact]
    public async Task UpsertPrimaryAccountAsync_WrongId_Throws()
    {
        // Issue #18: the MVP single-account UI requires the canonical "primary"
        // id; a mismatched id is a programming error and must throw.
        var svc = new AppSettingsService(new FakeAccountRepository(), new FakeAppSettingsRepository());
        var bad = new Account(
            Id: "not-primary",
            Host: "github.com",
            Login: "octocat",
            CredentialKey: "ghuboon.primary",
            CreatedAt: DateTimeOffset.UtcNow,
            LastValidatedAt: null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.UpsertPrimaryAccountAsync(bad));
    }

    [Fact]
    public async Task UpsertPrimaryAccountAsync_CorrectId_Succeeds()
    {
        var accounts = new FakeAccountRepository();
        var svc = new AppSettingsService(accounts, new FakeAppSettingsRepository());
        var ok = new Account(
            Id: AppSettingsService.PrimaryAccountId,
            Host: "github.com",
            Login: "octocat",
            CredentialKey: "ghuboon.primary",
            CreatedAt: DateTimeOffset.UtcNow,
            LastValidatedAt: null);

        await svc.UpsertPrimaryAccountAsync(ok);

        Assert.True(accounts.Accounts.ContainsKey(AppSettingsService.PrimaryAccountId));
    }
}
