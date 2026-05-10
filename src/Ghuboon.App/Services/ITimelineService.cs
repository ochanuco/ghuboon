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
/// Issue #8: the service returns the domain type
/// (<see cref="GitHubNotification"/>) so the service contract has no dependency
/// on the presentation layer. The Presentation layer
/// (<c>TimelineViewModel</c>) maps domain rows to view-models.
/// </summary>
public interface ITimelineService
{
    /// <summary>
    /// Loads notifications matching <paramref name="filter"/> as domain
    /// <see cref="GitHubNotification"/> instances. Mapping to view-models is the
    /// caller's responsibility.
    /// </summary>
    Task<IReadOnlyList<GitHubNotification>> LoadAsync(TimelineFilter filter, CancellationToken ct = default);

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
    IReadOnlyList<GitHubNotification> GetPlaceholderItems();
}
