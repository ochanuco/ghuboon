using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghuboon.App.Threading;

/// <summary>
/// Production <see cref="IBlockingIoScheduler"/>. Forces work onto a
/// <see cref="Task.Run(Action,CancellationToken)"/> so the UI thread
/// doesn't sit on top of a synchronous SQLite call when the public
/// API was shaped as <c>Task</c>.
/// </summary>
public sealed class ThreadPoolBlockingIoScheduler : IBlockingIoScheduler
{
    public Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work, ct);
    }

    public Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work, ct);
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work, ct);
    }

    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work, ct);
    }
}
