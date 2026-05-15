using System;
using System.Collections.Generic;
using System.Linq;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Pure ordering / filtering / dedup pipeline for the timeline list.
/// Extracted from <see cref="ViewModels.TimelineViewModel.ApplyCurrentFilter"/>
/// so the logic can be unit-tested without spinning up a VM (markdown
/// renderer, dispatcher, bookmark repository, …). The VM keeps the per-tab
/// cache and the diff-apply against the bound <c>ObservableCollection</c>;
/// this module is the work in between.
/// </summary>
public static class TimelineProjection
{
    /// <summary>
    /// Filters, dedups, and thread-anchor-sorts <paramref name="items"/> for
    /// the given <paramref name="filter"/>. Input order is preserved on
    /// items the filter matches; the result is in the timeline's natural
    /// display order (oldest first, Tween-style, with PR/Issue rows
    /// anchoring their own comment rows below).
    /// </summary>
    public static List<T> Build<T>(IReadOnlyList<T> items, TimelineFilter filter)
        where T : ITimelineProjectionItem
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(filter);

        var matched = new List<T>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (Matches(item, filter)) matched.Add(item);
        }

        // Dedup duplicate observations of the same logical event.
        // Comment kind: key on (NotificationId, LatestCommentApiUrl) so
        // the same /comments/{id} URL collapsed across re-bumps, while
        // distinct comments on the same thread survive.
        // Other kinds: key on NotificationId so push/CI/state bumps that
        // produced visually identical PR-mode rows collapse to the latest
        // observation per thread.
        // Iterate newest -> oldest so the latest event in each group wins,
        // then reverse for display.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coalesced = new List<T>(matched.Count);
        for (var i = matched.Count - 1; i >= 0; i--)
        {
            var item = matched[i];
            var key = item.EventKind == NotificationEventKind.Comment
                ? $"C|{item.NotificationId}|{item.LatestCommentApiUrl ?? string.Empty}"
                : $"T|{item.NotificationId}";

            if (string.IsNullOrEmpty(item.NotificationId) || seen.Add(key))
            {
                coalesced.Add(item);
            }
        }
        coalesced.Reverse();

        // Anchor each thread at its earliest UpdatedAt so cross-thread
        // chronology is preserved even when the first observed row in a
        // thread happened to be a comment notification whose
        // SourceUpdatedAt is later than the parent PR's observation.
        var threadAnchor = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var item in coalesced)
        {
            if (string.IsNullOrEmpty(item.NotificationId)) continue;
            if (!threadAnchor.TryGetValue(item.NotificationId, out var existing)
                || item.UpdatedAt < existing)
            {
                threadAnchor[item.NotificationId] = item.UpdatedAt;
            }
        }

        return coalesced
            .OrderBy(i => string.IsNullOrEmpty(i.NotificationId)
                ? i.UpdatedAt
                : (threadAnchor.TryGetValue(i.NotificationId, out var anchor) ? anchor : i.UpdatedAt))
            .ThenBy(i => i.NotificationId, StringComparer.Ordinal)
            .ThenBy(i => KindPriority(i.EventKind))
            .ThenBy(i => i.UpdatedAt)
            .ToList();
    }

    /// <summary>
    /// Tab + multi-repo + free-text predicate. Mirrors the SQL-side rules
    /// in <see cref="DbBackedTimelineService.LoadAsync"/> so swapping
    /// between server-side and client-side filtering is a no-op for the
    /// caller.
    /// </summary>
    public static bool Matches<T>(T item, TimelineFilter filter)
        where T : ITimelineProjectionItem
    {
        if (!DbBackedTimelineService.MatchesTab(item.Reason, filter.Tab))
        {
            return false;
        }
        if (filter.Tab == TimelineTab.MyPrs
            && item.EventKind != NotificationEventKind.PullRequest)
        {
            return false;
        }
        if (filter.Tab == TimelineTab.Bookmarks && !item.IsBookmarked)
        {
            return false;
        }
        if (!filter.MatchesAllRepositories)
        {
            if (string.IsNullOrEmpty(item.RepositoryFullName)) return false;
            var allowed = filter.RepositoryFullNames!;
            var anyMatch = false;
            foreach (var name in allowed)
            {
                if (string.Equals(name, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase))
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch) return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var needle = filter.SearchText.Trim();
            if (!Contains(item.RepositoryFullName, needle)
                && !Contains(item.Title, needle)
                && !Contains(item.Reason.ToString(), needle)
                && !Contains(item.SubjectType, needle))
            {
                return false;
            }
        }
        return true;
    }

    private static int KindPriority(NotificationEventKind k) => k switch
    {
        // PR / Issue / Discussion are thread-parent rows: pin them above
        // their comment children so a thread reads top-down PR -> comments.
        NotificationEventKind.PullRequest => 0,
        NotificationEventKind.Issue => 0,
        NotificationEventKind.Discussion => 0,
        _ => 1,
    };

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
