using System.Runtime.InteropServices;
using Ghuboon.App.Platform;
using Ghuboon.App.Platform.MacOS;

namespace Ghuboon.Tests.App.Platform;

public class MenuBarHostFactoryTests
{
    [Fact]
    public void Create_OnMacOS_ReturnsMacOSHost()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // skipped on non-macOS runtimes
        }

        using var host = MenuBarHostFactory.Create();

        Assert.IsType<MacOSMenuBarHost>(host);
    }

    [Fact]
    public void Create_OnNonMacOS_ReturnsNoOpHost()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // skipped on macOS runtimes
        }

        using var host = MenuBarHostFactory.Create();

        Assert.IsType<NoOpMenuBarHost>(host);
    }

    [Fact]
    public void Create_AlwaysReturnsIMenuBarHost()
    {
        using var host = MenuBarHostFactory.Create();

        Assert.NotNull(host);
        Assert.IsAssignableFrom<IMenuBarHost>(host);
    }
}
