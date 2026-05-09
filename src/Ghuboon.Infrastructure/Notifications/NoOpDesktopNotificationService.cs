using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Notifications;

/// <summary>
/// Fallback <see cref="IDesktopNotificationService"/> for platforms without an
/// MVP implementation (Windows, Linux). Calls complete successfully without
/// surfacing any UI, so wiring code can remain platform-agnostic.
/// </summary>
public sealed class NoOpDesktopNotificationService : IDesktopNotificationService
{
    public Task ShowAsync(DesktopNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.CompletedTask;
    }
}
