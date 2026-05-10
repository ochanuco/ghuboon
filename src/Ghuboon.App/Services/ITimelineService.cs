using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Loads timeline rows for the UI. Production builds resolve this from
/// <see cref="DbBackedTimelineService"/>; tests/previewer use
/// <see cref="StubTimelineService"/>.
///
/// Event-log timeline: rows are <see cref="NotificationEvent"/> instances —
/// one row per observed thread update. A thread that goes Open -&gt; Draft -&gt;
/// Open over multiple sync windows shows up as three rows, not one.
/// </summary>
public interface ITimelineService
{
    /// <summary>
    /// Loads timeline events matching <paramref name="filter"/> as
    /// <see cref="NotificationEvent"/> instances, newest-first. Mapping to
    /// view-models is the caller's responsibility.
    /// </summary>
    Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Lists repositories observed in the current cache, used to populate the repo
    /// filter dropdown. Result is sorted by full name.
    /// </summary>
    Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Synchronous placeholder used by the design-time previewer and legacy tests.
    /// Implementations may return an empty list when they have no synchronous
    /// snapshot.
    /// </summary>
    IReadOnlyList<NotificationEvent> GetPlaceholderItems();
}
