using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghuboon.App.Threading;

/// <summary>
/// Test <see cref="IBlockingIoScheduler"/>. Runs work inline on the
/// caller's thread — fine for unit tests that drive
/// <see cref="ViewModels.TimelineItemViewModel"/> commands directly,
/// avoids spinning up a thread pool task scheduler in test runs.
/// </summary>
public sealed class InlineBlockingIoScheduler : IBlockingIoScheduler
{
    public Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        try { work(); return Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    public Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        try { return work() ?? Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        try { return Task.FromResult(work()); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }

    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        try { return work() ?? Task.FromResult<T>(default!); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }
}
