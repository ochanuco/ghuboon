using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ghuboon.App.ViewModels;

namespace Ghuboon.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Read-on-focus/selection (Phase 10): mark a freshly-selected unread item as read.
    /// Idempotent — the command itself no-ops on already-read items.
    /// </summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is null || e.AddedItems.Count == 0)
        {
            return;
        }

        if (e.AddedItems[0] is TimelineItemViewModel item && item.Unread)
        {
            // Async fire-and-forget; the command itself swallows recoverable errors.
            if (item.MarkAsReadCommand.CanExecute(null))
            {
                item.MarkAsReadCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// Double-tap on the title row toggles the expansion panel (action buttons).
    /// </summary>
    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TimelineItemViewModel vm })
        {
            vm.ToggleExpandCommand.Execute(null);
        }
    }
}
