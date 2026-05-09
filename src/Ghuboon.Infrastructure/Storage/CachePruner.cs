using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Removes notifications cached longer than the retention window (ADR-022,
/// default 30 days). Implemented as a thin helper rather than baked into the
/// repository so the data layer stays purely about CRUD.
/// </summary>
public sealed class CachePruner
{
    /// <summary>
    /// Default cache retention window for notifications (ADR-022).
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    private readonly INotificationRepository _notifications;
    private readonly TimeSpan _retention;
    private readonly Func<DateTimeOffset> _clock;

    public CachePruner(INotificationRepository notifications)
        : this(notifications, DefaultRetention, () => DateTimeOffset.UtcNow)
    {
    }

    public CachePruner(INotificationRepository notifications, TimeSpan retention)
        : this(notifications, retention, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Test-only constructor with an injectable clock.
    /// </summary>
    public CachePruner(INotificationRepository notifications, TimeSpan retention, Func<DateTimeOffset> clock)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }

        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _retention = retention;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public TimeSpan Retention => _retention;

    /// <summary>
    /// Delete cached notifications older than the retention window. Returns the
    /// number of rows deleted.
    /// </summary>
    public Task<int> PruneAsync(CancellationToken ct = default)
    {
        var cutoff = _clock() - _retention;
        return _notifications.DeleteOlderThanAsync(cutoff, ct);
    }
}
