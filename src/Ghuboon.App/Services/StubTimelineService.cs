using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// In-memory <see cref="ITimelineService"/> used by the design-time previewer and
/// older tests. Mirrors the legacy placeholder shape so the existing UI snapshot
/// matches what Phase 1 produced.
/// </summary>
public sealed class StubTimelineService : ITimelineService
{
    public Task<IReadOnlyList<TimelineItemViewModel>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<TimelineItemViewModel>>(GetPlaceholderItems());
    }

    public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());
    }

    public IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems()
    {
        var now = DateTimeOffset.UtcNow;
        return new List<TimelineItemViewModel>
        {
            TimelineItemViewModel.Placeholder("1", "octocat/hello-world", "Add CONTRIBUTING.md", "review_requested", now.AddMinutes(-3), true),
            TimelineItemViewModel.Placeholder("2", "ochanuco/ghuboon", "Phase 1 shell scaffolding", "mention", now.AddMinutes(-25), true),
            TimelineItemViewModel.Placeholder("3", "dotnet/runtime", "Investigate AOT regression on macOS", "subscribed", now.AddHours(-2), false),
            TimelineItemViewModel.Placeholder("4", "AvaloniaUI/Avalonia", "ListBox virtualization issue", "author", now.AddHours(-9), true),
            TimelineItemViewModel.Placeholder("5", "ghuboon/playground", "Tune cache pruning to 30 days", "assign", now.AddDays(-1), false),
        };
    }
}
