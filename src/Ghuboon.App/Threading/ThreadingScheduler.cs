namespace Ghuboon.App.Threading;

/// <summary>
/// Bundles the two scheduler abstractions ViewModels typically need:
/// <see cref="IUiScheduler"/> for marshalling property writes onto the
/// UI dispatcher and <see cref="IBlockingIoScheduler"/> for moving
/// sync-underneath I/O off the UI thread.
///
/// Pass a single <c>ThreadingScheduler</c> through to ViewModels
/// instead of two interface refs each. Call-sites read more like
/// <c>_threading.Ui.Post(...)</c> / <c>_threading.BlockingIo.RunAsync(...)</c>.
/// </summary>
public sealed class ThreadingScheduler
{
    public ThreadingScheduler(IUiScheduler ui, IBlockingIoScheduler blockingIo)
    {
        Ui = ui;
        BlockingIo = blockingIo;
    }

    public IUiScheduler Ui { get; }
    public IBlockingIoScheduler BlockingIo { get; }

    /// <summary>
    /// Convenience for unit tests: inline schedulers that don't
    /// require Avalonia or a thread pool task scheduler.
    /// </summary>
    public static ThreadingScheduler ForTests() =>
        new(new ImmediateUiScheduler(), new InlineBlockingIoScheduler());

    /// <summary>
    /// Convenience for production: Avalonia dispatcher + Task.Run pool.
    /// </summary>
    public static ThreadingScheduler ForProduction() =>
        new(new AvaloniaUiScheduler(), new ThreadPoolBlockingIoScheduler());
}
