using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Sync;

/// <summary>
/// Default <see cref="IClock"/> backed by <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
