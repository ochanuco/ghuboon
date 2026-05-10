using System;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.Services;
using Ghuboon.App.ViewModels.Settings;
using Ghuboon.Core.Abstractions;
using Ghuboon.Core.Domain;

namespace Ghuboon.Tests.App.Settings;

public class NotificationsSettingsViewModelTests
{
    private static (NotificationsSettingsViewModel vm, FakeAppSettingsRepository settings) Build()
    {
        var accounts = new FakeAccountRepository();
        var settings = new FakeAppSettingsRepository();
        var appSettings = new AppSettingsService(accounts, settings);
        var vm = new NotificationsSettingsViewModel(appSettings);
        return (vm, settings);
    }

    [Fact]
    public async Task Load_ReadsPersistedFalse()
    {
        var (vm, settings) = Build();
        settings.Values[NotificationsSettingsViewModel.OsNotificationsEnabledKey] = "False";

        await vm.LoadAsync();

        Assert.False(vm.OsNotificationsEnabled);
    }

    [Fact]
    public async Task Load_DefaultsToTrue_WhenAbsent()
    {
        var (vm, _) = Build();

        await vm.LoadAsync();

        Assert.True(vm.OsNotificationsEnabled);
    }

    [Fact]
    public async Task Toggle_PersistsThroughAppSettingsRepository()
    {
        var (vm, settings) = Build();
        await vm.LoadAsync();

        vm.OsNotificationsEnabled = false;

        // Setter is fire-and-forget; flush by polling the in-memory map.
        await Task.Yield();
        Assert.Equal("False", settings.Values[NotificationsSettingsViewModel.OsNotificationsEnabledKey]);

        vm.OsNotificationsEnabled = true;
        await Task.Yield();
        Assert.Equal("True", settings.Values[NotificationsSettingsViewModel.OsNotificationsEnabledKey]);
    }

    [Fact]
    public void HighPriorityReasons_MatchAdr008List()
    {
        var (vm, _) = Build();

        Assert.Equal(
            new[] { "Review requested", "Mention", "Team mention", "Assigned" },
            vm.HighPriorityReasons);
    }

    [Fact]
    public async Task Toggle_PersistFailure_RevertsAndSurfacesError()
    {
        // Issue #18: persistence failures must surface in the UI and revert
        // the toggle so the displayed state matches what's actually persisted.
        var settings = new ThrowingAppSettingsService();
        var vm = new NotificationsSettingsViewModel(settings);
        Assert.True(vm.OsNotificationsEnabled);

        vm.OsNotificationsEnabled = false;

        // The persist task is observed via a synchronous continuation, but
        // give the runtime a tick to flush the failure path.
        for (var i = 0; i < 10 && !vm.HasPersistError; i++)
        {
            await Task.Yield();
        }

        Assert.True(vm.HasPersistError, "Expected a persist error message after a failed write.");
        Assert.False(string.IsNullOrEmpty(vm.PersistErrorMessage));
        // Toggle should be reverted to its prior value (true).
        Assert.True(vm.OsNotificationsEnabled);
    }

    private sealed class ThrowingAppSettingsService : IAppSettingsService
    {
        public bool IsConfigured => false;

        public Task<string?> GetPatReferenceAsync() => Task.FromResult<string?>(null);

        public Task<Account?> GetPrimaryAccountAsync(CancellationToken ct = default)
            => Task.FromResult<Account?>(null);

        public Task UpsertPrimaryAccountAsync(Account account, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);

        public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("simulated persist failure"));
    }
}
