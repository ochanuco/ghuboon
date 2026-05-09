using Ghuboon.Core.Abstractions;
using Ghuboon.Infrastructure.Notifications;

namespace Ghuboon.Tests.Infrastructure.Notifications;

public class NoOpDesktopNotificationServiceTests
{
    [Fact]
    public async Task ShowAsync_completes_without_throwing()
    {
        var svc = new NoOpDesktopNotificationService();

        await svc.ShowAsync(new DesktopNotification("id", "title", "body", "https://example/test"));
    }

    [Fact]
    public async Task ShowAsync_throws_on_null_notification()
    {
        var svc = new NoOpDesktopNotificationService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.ShowAsync(null!));
    }
}
