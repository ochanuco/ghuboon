using System.Runtime.InteropServices;
using Ghuboon.Infrastructure.Notifications;
using Serilog;
using Serilog.Core;

namespace Ghuboon.Tests.Infrastructure.Notifications;

public class DesktopNotificationFactoryTests
{
    [Fact]
    public void Create_on_macOS_returns_macOS_implementation()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // platform-conditional assertion
        }

        var svc = DesktopNotificationFactory.Create(SilentLogger());

        Assert.IsType<MacOSDesktopNotificationService>(svc);
    }

    [Fact]
    public void Create_on_non_macOS_returns_noop_implementation()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // platform-conditional assertion
        }

        var svc = DesktopNotificationFactory.Create(SilentLogger());

        Assert.IsType<NoOpDesktopNotificationService>(svc);
    }

    [Fact]
    public void Create_throws_on_null_logger()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopNotificationFactory.Create(null!));
    }

    private static ILogger SilentLogger() =>
        new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
}
