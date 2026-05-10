using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// In-memory <see cref="ITimelineService"/> used by the design-time previewer and
/// older tests. Mirrors the legacy placeholder shape so the existing UI snapshot
/// matches what Phase 1 produced.
///
/// Event-log timeline: returns one <see cref="NotificationEvent"/> per
/// placeholder row.
/// </summary>
public sealed class StubTimelineService : ITimelineService
{
    public Task<IReadOnlyList<NotificationEvent>> LoadAsync(TimelineFilter filter, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<NotificationEvent>>(GetPlaceholderItems());
    }

    public Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<RepositoryRef>>(Array.Empty<RepositoryRef>());
    }

    public IReadOnlyList<NotificationEvent> GetPlaceholderItems()
    {
        var now = DateTimeOffset.UtcNow;
        return new List<NotificationEvent>
        {
            BuildPlaceholder(1, "1", "octocat/hello-world", "Add CONTRIBUTING.md", NotificationReason.Review, now.AddMinutes(-3), unread: true, "PullRequest"),
            BuildPlaceholder(2, "2", "ochanuco/ghuboon", "Phase 1 shell scaffolding", NotificationReason.Mention, now.AddMinutes(-25), unread: true, "PullRequest"),
            BuildPlaceholder(3, "3", "dotnet/runtime", "Investigate AOT regression on macOS", NotificationReason.Watching, now.AddHours(-2), unread: false, "Issue"),
            BuildPlaceholder(4, "4", "AvaloniaUI/Avalonia", "ListBox virtualization issue", NotificationReason.MyPr, now.AddHours(-9), unread: true, "PullRequest"),
            BuildPlaceholder(5, "5", "ghuboon/playground", "Tune cache pruning to 30 days", NotificationReason.Assigned, now.AddDays(-1), unread: false, "Issue"),
        };
    }

    private static NotificationEvent BuildPlaceholder(
        long eventId,
        string threadId,
        string repo,
        string title,
        NotificationReason reason,
        DateTimeOffset updatedAt,
        bool unread,
        string subjectType)
    {
        return new NotificationEvent(
            Id: eventId,
            AccountId: "placeholder",
            NotificationId: $"placeholder:{threadId}",
            ThreadId: threadId,
            RepositoryFullName: repo,
            Subject: new NotificationSubject(subjectType, title, null, null),
            Reason: reason,
            SourceUpdatedAt: updatedAt,
            ObservedAt: updatedAt,
            Unread: unread,
            LastReadAt: null,
            RawJson: "{}");
    }
}
