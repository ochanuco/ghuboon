using Ghuboon.App.Platform;

namespace Ghuboon.Tests.App.Platform;

public class MenuBarContextTests
{
    [Fact]
    public void Record_ExposesAllDelegates()
    {
        var counter = 0;
        var ctx = new MenuBarContext(
            ShowMainWindow: () => counter |= 1 << 0,
            HideMainWindow: () => counter |= 1 << 1,
            SyncNow: () => counter |= 1 << 2,
            OpenSettings: () => counter |= 1 << 3,
            Quit: () => counter |= 1 << 4);

        ctx.ShowMainWindow();
        ctx.HideMainWindow();
        ctx.SyncNow();
        ctx.OpenSettings();
        ctx.Quit();

        Assert.Equal(0b11111, counter);
    }
}
