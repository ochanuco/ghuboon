using System.Runtime.InteropServices;
using Ghuboon.Core.Abstractions;
using Serilog;

namespace Ghuboon.Infrastructure.Notifications;

/// <summary>
/// Selects an <see cref="IDesktopNotificationService"/> implementation for the
/// host platform. macOS gets the <see cref="MacOSDesktopNotificationService"/>;
/// every other platform falls back to <see cref="NoOpDesktopNotificationService"/>
/// (PLAN.md Phase 11 ships macOS-only).
/// </summary>
public static class DesktopNotificationFactory
{
    /// <summary>
    /// Build the notification service appropriate to the current OS.
    /// </summary>
    public static IDesktopNotificationService Create(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSDesktopNotificationService(log);
        }

        return new NoOpDesktopNotificationService();
    }
}
