using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ghuboon.App.ViewModels;

namespace Ghuboon.App.Views;

public partial class TimelineView : UserControl
{
    private INotifyCollectionChanged? _trackedItems;

    public TimelineView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        DataContextChanged += (_, _) => HookItemsCollection();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookItemsCollection();
        // Scroll to bottom on first paint.
        Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_trackedItems is not null)
        {
            _trackedItems.CollectionChanged -= OnItemsCollectionChanged;
            _trackedItems = null;
        }
    }

    private void HookItemsCollection()
    {
        if (_trackedItems is not null)
        {
            _trackedItems.CollectionChanged -= OnItemsCollectionChanged;
            _trackedItems = null;
        }

        if (DataContext is TimelineViewModel vm && vm.Items is INotifyCollectionChanged inc)
        {
            _trackedItems = inc;
            inc.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Always follow the tail — user wants newest activity to remain
        // visible without manual scrolling.
        Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        if (this.FindControl<ListBox>("ItemsList") is not { } list || list.ItemCount == 0)
        {
            return;
        }
        list.ScrollIntoView(list.ItemCount - 1);
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
