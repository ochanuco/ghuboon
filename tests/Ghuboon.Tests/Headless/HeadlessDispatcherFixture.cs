using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Ghuboon.Tests.Headless;

/// <summary>
/// One-shot bootstrap for the Avalonia headless platform. The first
/// access spins up the headless renderer + dispatcher on a dedicated
/// thread; subsequent accesses reuse it. Boot is process-wide because
/// Avalonia's <c>AppBuilder</c> can only be started once per AppDomain.
/// <para>
/// Tests use the static <see cref="RunOnDispatcher"/> wrapper to push
/// their body onto the Avalonia UI thread. This is what
/// <c>Avalonia.Headless.XUnit</c>'s <c>[AvaloniaFact]</c> does
/// internally — replicating it here keeps the rest of the suite on
/// xunit v2 (the XUnit adapter package pulls in xunit v3 and conflicts
/// with the existing 600+ tests).
/// </para>
/// <para>
/// IMPORTANT: tests using this fixture must run in a process that has
/// not already touched Avalonia statics. Booting Avalonia headless
/// claims <c>Dispatcher.UIThread</c> for our dedicated thread; if a
/// prior test in the same process already accessed Avalonia (e.g. via
/// <c>Application.Current</c>), the claim races and
/// <c>SetupUnsafe</c> fails on the Compositor's <c>VerifyAccess</c>.
/// Tag headless tests with <c>[Trait("Category", "Headless")]</c> and
/// run them in a separate <c>dotnet test --filter</c> invocation.
/// </para>
/// </summary>
internal static class HeadlessDispatcherFixture
{
    private static readonly object Gate = new();
    private static bool _started;

    /// <summary>
    /// Run <paramref name="body"/> on the Avalonia UI thread inside a
    /// headless app session. Booting Avalonia is lazy and one-shot per
    /// process. The Task returned by <paramref name="body"/> is awaited
    /// inside the dispatcher invocation so failures propagate cleanly.
    /// </summary>
    public static Task RunOnDispatcher(Func<Task> body)
    {
        EnsureStarted();
        // Avalonia 12's InvokeAsync(Func<Task>, …) already returns the
        // unwrapped Task — no .GetTask().Unwrap() needed.
        return Dispatcher.UIThread.InvokeAsync(body, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Variant for tests that just need to assert synchronous state on
    /// the UI thread (no await).
    /// </summary>
    public static Task RunOnDispatcher(Action body)
    {
        EnsureStarted();
        return Dispatcher.UIThread.InvokeAsync(body, DispatcherPriority.Normal).GetTask();
    }

    private static void EnsureStarted()
    {
        if (_started) return;
        lock (Gate)
        {
            if (_started) return;

            // AppBuilder.StartWithHeadlessVncPlatform / Start spins the
            // dispatcher loop synchronously. We need it non-blocking so
            // the test thread can keep marshalling work in — launch on a
            // dedicated thread and wait until the dispatcher is alive.
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var builder = AppBuilder.Configure<HeadlessTestApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        // No pixel assertions, so the SkiaSharp back-buffer
                        // is dead weight — saves ~30% per test.
                        UseHeadlessDrawing = true,
                    });
                builder.SetupWithoutStarting();

                // Signal as soon as the dispatcher is wired but BEFORE we
                // start the message loop, so RunOnDispatcher.Invoke can
                // proceed even while we sit in Run().
                ready.Set();
                Dispatcher.UIThread.MainLoop(CancellationToken.None);
            })
            {
                IsBackground = true,
                Name = "Ghuboon.HeadlessDispatcher",
            };
            thread.Start();
            ready.Wait();

            _started = true;
        }
    }
}
