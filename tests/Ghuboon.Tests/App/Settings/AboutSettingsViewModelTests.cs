using Ghuboon.App.ViewModels.Settings;

namespace Ghuboon.Tests.App.Settings;

public class AboutSettingsViewModelTests
{
    [Fact]
    public void Version_IsNotEmpty()
    {
        var vm = new AboutSettingsViewModel(_ => { });
        Assert.False(string.IsNullOrWhiteSpace(vm.Version));
    }

    [Fact]
    public void OpenRepository_InvokesBrowser_WithRepoUrl()
    {
        string? captured = null;
        var vm = new AboutSettingsViewModel(url => captured = url);

        vm.OpenRepositoryCommand.Execute(null);

        Assert.Equal(AboutSettingsViewModel.RepositoryUrl, captured);
    }
}
