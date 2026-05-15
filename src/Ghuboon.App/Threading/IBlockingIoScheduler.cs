using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghuboon.App.Threading;

/// <summary>
/// Wraps known synchronous I/O work (notably Microsoft.Data.Sqlite's
/// "async" methods, which actually run on the calling thread) so a
/// UI-triggered command never blocks the dispatcher while waiting on
/// SQLite, the Keychain CLI, or similar.
///
/// Replaces the ad-hoc <c>Task.Run(() =&gt; repo.WriteAsync(...))</c>
/// wraps sprinkled through MarkAsRead / Bookmark / Unbookmark.
/// </summary>
public interface IBlockingIoScheduler
{
    /// <summary>
    /// Schedule <paramref name="work"/> on the thread pool. Returns a
    /// Task that completes when the inner sync I/O finishes.
    /// </summary>
    Task RunAsync(Action work, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="RunAsync(Action,CancellationToken)"/> but
    /// awaits an async-shaped delegate. Useful for methods whose
    /// signatures are <c>Task</c> even though the underlying work is
    /// blocking.
    /// </summary>
    Task RunAsync(Func<Task> work, CancellationToken ct = default);

    /// <summary>
    /// Strongly-typed variant for queries that return a value.
    /// </summary>
    Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default);

    /// <summary>
    /// Strongly-typed variant for queries whose signature is
    /// <c>Task&lt;T&gt;</c> with sync I/O underneath.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default);
}
