using Ghuboon.App.Platform;

namespace Ghuboon.Tests.App.Platform;

/// <summary>
/// Phase 15: ensure <see cref="MenuBarHostFactory.Create"/> returns the same
/// concrete type across repeat invocations on the current platform — i.e. the
/// factory is a stable, deterministic dispatch and not a one-shot construct.
/// </summary>
public class MenuBarHostFactoryExtraTests
{
    [Fact]
    public void Create_RepeatedCalls_ReturnSameConcreteType()
    {
        using var first = MenuBarHostFactory.Create();
        using var second = MenuBarHostFactory.Create();
        using var third = MenuBarHostFactory.Create();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(first.GetType(), second.GetType());
        Assert.Equal(first.GetType(), third.GetType());
    }

    [Fact]
    public void Create_ReturnsDistinctInstances_NotASingleton()
    {
        using var first = MenuBarHostFactory.Create();
        using var second = MenuBarHostFactory.Create();

        // Each invocation builds a fresh disposable host so callers can manage
        // their own lifecycle without sharing state with siblings.
        Assert.NotSame(first, second);
    }
}
