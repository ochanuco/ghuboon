using System.Collections.Generic;
using System.Linq;
using Ghuboon.App.ViewModels;
using Ghuboon.Core.Domain;

namespace Ghuboon.App.Services;

/// <summary>
/// Pure helpers that aggregate unread counts across a notification collection.
/// Used by <see cref="MainWindowViewModel"/> for the status-bar badge and by the
/// menu-bar host's "Unread: N" indicator.
/// </summary>
public static class UnreadCounter
{
    public static int CountUnread(IEnumerable<TimelineItemViewModel> items)
    {
        if (items is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in items)
        {
            if (item.Unread)
            {
                count++;
            }
        }
        return count;
    }

    public static int CountUnreadInTab(IEnumerable<TimelineItemViewModel> items, TimelineTab tab)
    {
        if (items is null)
        {
            return 0;
        }

        // Reuse the canonical reason-to-tab mapping defined alongside the
        // DB-backed timeline filter (Issue #22). Keeping a single implementation
        // avoids drift between the timeline list and the menu-bar / status-bar
        // unread badge.
        return items.Count(i => i.Unread && DbBackedTimelineService.MatchesTab(i.Reason, tab));
    }
}
