using System;
using Ghuboon.App.Platform;

namespace Ghuboon.Tests.App.Platform;

/// <summary>
/// Phase 15: <see cref="MenuBarContext"/> is a positional record of <c>Action</c>
/// delegates with non-nullable annotations. Construction does not run any
/// validation, so passing null is allowed and only fails on invocation. We pin
/// down both halves of that contract.
/// </summary>
public class MenuBarContextExtraTests
{
    [Fact]
    public void Constructor_AcceptsNullDelegates_WithoutThrowing()
    {
        var ex = Record.Exception(() =>
        {
            _ = new MenuBarContext(
                ShowMainWindow: null!,
                HideMainWindow: null!,
                SyncNow: null!,
                OpenSettings: null!,
                Quit: null!);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void NullDelegateInvocation_ThrowsNullReferenceException()
    {
        // This documents that the record does not provide a graceful fallback —
        // callers are expected to populate every action. If we ever add no-op
        // defaults the test must be updated.
        var ctx = new MenuBarContext(
            ShowMainWindow: null!,
            HideMainWindow: () => { },
            SyncNow: () => { },
            OpenSettings: () => { },
            Quit: () => { });

        Assert.Throws<NullReferenceException>(() => ctx.ShowMainWindow());
    }

    [Fact]
    public void RecordEquality_IgnoresDelegateIdentity_OnlyComparesByReference()
    {
        // C# record equality on Action delegates uses reference equality.
        // Two contexts built with distinct lambdas are NOT equal even if the
        // lambdas have the same body.
        Action a = () => { };
        Action b = () => { };
        var first = new MenuBarContext(a, a, a, a, a);
        var second = new MenuBarContext(b, b, b, b, b);

        Assert.NotEqual(first, second);
    }
}
