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

        return items.Count(i => i.Unread && TabMatches(i.Reason, tab));
    }

    private static bool TabMatches(NotificationReason reason, TimelineTab tab) => tab switch
    {
        TimelineTab.All => true,
        TimelineTab.Review => reason == NotificationReason.Review,
        TimelineTab.Mention => reason is NotificationReason.Mention or NotificationReason.TeamMention,
        TimelineTab.MyPrs => reason == NotificationReason.MyPr,
        TimelineTab.Watching => reason == NotificationReason.Watching,
        _ => true,
    };
}
