using System;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Minimal contract the timeline projection needs from a row. Implemented
/// by <see cref="ViewModels.TimelineItemViewModel"/> in production; tests
/// can implement it directly so the projection can be exercised without
/// constructing a real VM (markdown / dispatcher dependencies).
/// </summary>
public interface ITimelineProjectionItem
{
    string Id { get; }
    string NotificationId { get; }
    string? LatestCommentApiUrl { get; }
    NotificationEventKind EventKind { get; }
    NotificationReason Reason { get; }
    DateTimeOffset UpdatedAt { get; }
    string RepositoryFullName { get; }
    string Title { get; }
    string SubjectType { get; }
    bool IsBookmarked { get; }
}
