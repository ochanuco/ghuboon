using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Loads timeline rows for the UI. Production builds resolve this from
/// <see cref="DbBackedTimelineService"/>; tests/previewer use
/// <see cref="StubTimelineService"/>.
/// </summary>
public interface ITimelineService
{
    /// <summary>
    /// Loads notifications matching <paramref name="filter"/> as
    /// <see cref="TimelineItemViewModel"/> instances ready to bind.
    /// </summary>
    Task<IReadOnlyList<TimelineItemViewModel>> LoadAsync(TimelineFilter filter, CancellationToken ct = default);

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
    IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems();
}
