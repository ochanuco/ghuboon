using Ghuboon.Core.Abstractions;

namespace Ghuboon.Tests.Infrastructure.Sync;

/// <summary>
/// Test-only mutable <see cref="IClock"/>.
/// </summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } =
        new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
