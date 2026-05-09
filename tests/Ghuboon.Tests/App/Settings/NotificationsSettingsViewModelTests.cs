using Ghuboon.App.Services;
using Ghuboon.App.ViewModels.Settings;

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
}
