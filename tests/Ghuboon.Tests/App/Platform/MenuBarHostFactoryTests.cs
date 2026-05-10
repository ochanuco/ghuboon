using System;
using System.Runtime.InteropServices;
using Ghuboon.App.Platform;
using Ghuboon.App.Platform.MacOS;

namespace Ghuboon.Tests.App.Platform;

[Collection("EnvVarSensitive")]
public class MenuBarHostFactoryTests
{
    [Fact]
    public void Create_OnMacOS_WithEnvOptIn_ReturnsMacOSHost()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // skipped on non-macOS runtimes
        }

        var prior = Environment.GetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar, "1");

            using var host = MenuBarHostFactory.Create();

            Assert.IsType<MacOSMenuBarHost>(host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar, prior);
        }
    }

    [Fact]
    public void Create_OnMacOS_WithoutEnvOptIn_ReturnsNoOpHost()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // skipped on non-macOS runtimes
        }

        var prior = Environment.GetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar, null);

            using var host = MenuBarHostFactory.Create();

            Assert.IsType<NoOpMenuBarHost>(host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MenuBarHostFactory.MacOSEnableEnvVar, prior);
        }
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
