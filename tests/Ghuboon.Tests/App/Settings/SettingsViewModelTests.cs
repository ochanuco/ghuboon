using Ghuboon.App.Services;
using Ghuboon.App.ViewModels;
using Ghuboon.App.ViewModels.Settings;

namespace Ghuboon.Tests.App.Settings;

public class SettingsViewModelTests
{
    [Fact]
    public void DefaultConstructor_BuildsAllSubViewModels()
    {
        var vm = new SettingsViewModel();

        Assert.NotNull(vm.Account);
        Assert.NotNull(vm.Sync);
        Assert.NotNull(vm.Notifications);
        Assert.NotNull(vm.Cache);
        Assert.NotNull(vm.Security);
        Assert.NotNull(vm.About);
    }

    [Fact]
    public async Task LoadAsync_HydratesAccountAndNotifications()
    {
        var accounts = new FakeAccountRepository();
        var settings = new FakeAppSettingsRepository
        {
            Values = { [NotificationsSettingsViewModel.OsNotificationsEnabledKey] = "False" },
        };
        var credentials = new FakeCredentialStore();
        var api = new FakeGitHubApiClient();
        var notifications = new FakeNotificationRepository();
        var appSettings = new AppSettingsService(accounts, settings);
        var deps = new SettingsViewModel.SettingsViewModelDependencies(
            AppSettings: appSettings,
            CredentialStore: credentials,
            PatValidation: new PatValidationService(api),
            NotificationRepository: notifications,
            OpenBrowser: _ => { },
            Clock: TimeProvider.System);

        var vm = new SettingsViewModel(deps);
        await vm.LoadAsync();

        Assert.False(vm.Notifications.OsNotificationsEnabled);
    }

    [Fact]
    public void Sync_RaisesSyncRequestedEvent_OnCommand()
    {
        var vm = new SettingsViewModel();
        var raised = 0;
        vm.Sync.SyncRequested += (_, _) => raised++;

        vm.Sync.SyncNowCommand.Execute(null);

        Assert.Equal(1, raised);
    }
}
