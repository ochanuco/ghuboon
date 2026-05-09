namespace Ghuboon.Core.Abstractions;

/// <summary>
/// Testable abstraction over the current wall-clock time. The sync service uses this
/// instead of <see cref="System.DateTimeOffset.UtcNow"/> directly so tests can pin
/// time to deterministic values.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Current UTC time. Implementations must return a value in UTC offset.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
