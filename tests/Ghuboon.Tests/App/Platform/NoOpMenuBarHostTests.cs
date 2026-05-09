using Ghuboon.App.Platform;

namespace Ghuboon.Tests.App.Platform;

public class NoOpMenuBarHostTests
{
    private static MenuBarContext CreateContext() => new(
        ShowMainWindow: () => { },
        HideMainWindow: () => { },
        SyncNow: () => { },
        OpenSettings: () => { },
        Quit: () => { });

    [Fact]
    public void Initialize_DoesNotThrow()
    {
        var host = new NoOpMenuBarHost();

        var ex = Record.Exception(() => host.Initialize(CreateContext()));

        Assert.Null(ex);
    }

    [Fact]
    public void UpdateUnreadCount_BeforeInitialize_DoesNotThrow()
    {
        var host = new NoOpMenuBarHost();

        var ex = Record.Exception(() => host.UpdateUnreadCount(5));

        Assert.Null(ex);
    }

    [Fact]
    public void UpdateUnreadCount_AfterInitialize_DoesNotThrow()
    {
        var host = new NoOpMenuBarHost();
        host.Initialize(CreateContext());

        var ex = Record.Exception(() => host.UpdateUnreadCount(42));

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var host = new NoOpMenuBarHost();
        host.Initialize(CreateContext());

        host.Dispose();
        var ex = Record.Exception(() => host.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void Initialize_DoesNotInvokeAnyDelegate()
    {
        var calls = 0;
        var ctx = new MenuBarContext(
            ShowMainWindow: () => calls++,
            HideMainWindow: () => calls++,
            SyncNow: () => calls++,
            OpenSettings: () => calls++,
            Quit: () => calls++);
        var host = new NoOpMenuBarHost();

        host.Initialize(ctx);
        host.UpdateUnreadCount(1);
        host.Dispose();

        Assert.Equal(0, calls);
    }
}
