using System;
using System.Collections.Generic;
using Ghuboon.App.ViewModels;

namespace Ghuboon.App.Services;

public sealed class StubTimelineService : ITimelineService
{
    public IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems()
    {
        var now = DateTimeOffset.UtcNow;
        return new List<TimelineItemViewModel>
        {
            new("1", "octocat/hello-world", "Add CONTRIBUTING.md", "review_requested", now.AddMinutes(-3), true),
            new("2", "ochanuco/ghuboon", "Phase 1 shell scaffolding", "mention", now.AddMinutes(-25), true),
            new("3", "dotnet/runtime", "Investigate AOT regression on macOS", "subscribed", now.AddHours(-2), false),
            new("4", "AvaloniaUI/Avalonia", "ListBox virtualization issue", "author", now.AddHours(-9), true),
            new("5", "ghuboon/playground", "Tune cache pruning to 30 days", "assign", now.AddDays(-1), false),
        };
    }
}
