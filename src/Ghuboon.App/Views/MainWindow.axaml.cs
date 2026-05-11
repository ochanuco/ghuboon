using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ghuboon.App.ViewModels;

namespace Ghuboon.App.Views;

public partial class MainWindow : Window
{
    // Cache the timeline ListBox / detail ScrollViewer so hot-path key
    // handlers don't rescan the visual tree on every press. The window
    // template is stable, so a single lookup on first use is enough.
    private ListBox? _timelineList;
    private ScrollViewer? _detailScroll;

    private ListBox? TimelineList()
        => _timelineList ??= this.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(b => b.Name == "ItemsList");

    private ScrollViewer? DetailScroll()
        => _detailScroll ??= this.FindControl<ScrollViewer>("DetailScrollViewer");

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // Space activates a focused ToggleButton/Button on KeyUp, so a
        // bare Tunnel KeyDown handler that flips Unread is not enough —
        // we have to also swallow the matching KeyUp or the popup will
        // open right after our jump-to-unread runs. Same gating as
        // OnKeyDown: skip when a TextBox owns focus, otherwise eat it.
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        // Debounce live position / size changes so dragging the window
        // doesn't write to the encrypted DB on every pixel.
        PositionChanged += (_, _) => ScheduleSaveBounds();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WidthProperty || e.Property == HeightProperty)
            {
                ScheduleSaveBounds();
            }
        };
    }

    private CancellationTokenSource? _boundsSaveCts;

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var bounds = await vm.TryLoadWindowBoundsAsync().ConfigureAwait(true);
        if (bounds is null) return;
        var (x, y, w, h) = bounds.Value;
        Width = w;
        Height = h;
        Position = new Avalonia.PixelPoint((int)x, (int)y);
    }

    private void OnWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Synchronous-style fire so the save lands before process exit;
        // SetAsync is fast (a single SQLite UPSERT).
        _ = vm.SaveWindowBoundsAsync(Position.X, Position.Y, Width, Height);
    }

    private void ScheduleSaveBounds()
    {
        if (DataContext is not MainWindowViewModel vm || vm.AppSettingsStore is null) return;
        // Coalesce live drag / resize events into a single save at the
        // tail of a quiet 600 ms window. The save still runs on the
        // thread pool so the UI thread isn't blocked on the DB write.
        var previous = _boundsSaveCts;
        var cts = new CancellationTokenSource();
        _boundsSaveCts = cts;
        previous?.Cancel();
        previous?.Dispose();

        var capturedPos = Position;
        var capturedW = Width;
        var capturedH = Height;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            try
            {
                await vm.SaveWindowBoundsAsync(capturedPos.X, capturedPos.Y, capturedW, capturedH).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        });
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox || focused is AutoCompleteBox)
        {
            return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Vim / OpenTween-style keyboard shortcuts:
    ///   * <c>i</c>          TL → focus the detail body (read-mode)
    ///   * <c>Esc</c>         detail → focus back to the TL row list
    ///   * <c>j</c> / <c>k</c>   next / previous TL row (arrow keys still work)
    ///   * <c>n</c> / Alt+→  next tab; <c>p</c> / Alt+←  previous tab
    /// We register on the tunnel pass so single-letter keys don't get
    /// consumed by ListBox arrow handling first, and we no-op when a
    /// <see cref="TextBox"/> has focus so the search box still accepts
    /// every key.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Never hijack keys while a text input has focus — that would break
        // search / settings forms. Also skip when the user is interacting
        // with the repo-filter popup (CheckBoxes own arrow / Space) so the
        // popup's checkbox list stays usable.
        var focused = FocusManager?.GetFocusedElement();
        var inText = focused is TextBox || focused is AutoCompleteBox;
        var inPopupControl = focused is CheckBox || focused is RadioButton;
        var bareKey = e.KeyModifiers == KeyModifiers.None;

        if (inPopupControl)
        {
            return;
        }

        if (e.Key == Key.Escape && !inText)
        {
            FocusTimeline();
            e.Handled = true;
            return;
        }

        if (inText)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.I when bareKey:
                FocusDetail();
                e.Handled = true;
                break;
            case Key.J when bareKey:
            case Key.Down when bareKey:
                MoveTimelineSelection(+1);
                e.Handled = true;
                break;
            case Key.K when bareKey:
            case Key.Up when bareKey:
                MoveTimelineSelection(-1);
                e.Handled = true;
                break;
            case Key.N when bareKey:
            case Key.S when bareKey:
            case Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                CycleTab(+1);
                e.Handled = true;
                break;
            case Key.P when bareKey:
            case Key.A when bareKey:
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                CycleTab(-1);
                e.Handled = true;
                break;
            case Key.Space when bareKey:
                // Browser-like contextual Space: page-down the detail pane
                // when the user is reading a body, otherwise jump to the
                // oldest unread row.
                if (IsDetailFocused())
                {
                    ScrollDetail(+1);
                }
                else
                {
                    JumpToUnread();
                }
                e.Handled = true;
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.Shift:
                // Shift+Space: page-up while reading the detail pane;
                // otherwise jump to the newest row (timeline tail).
                if (IsDetailFocused())
                {
                    ScrollDetail(-1);
                    e.Handled = true;
                }
                break;
            case Key.Right when bareKey:
                JumpToKind(+1, Core.Domain.NotificationEventKind.PullRequest, Core.Domain.NotificationEventKind.Comment);
                e.Handled = true;
                break;
            case Key.Left when bareKey:
                JumpToKind(-1, Core.Domain.NotificationEventKind.PullRequest, Core.Domain.NotificationEventKind.Comment);
                e.Handled = true;
                break;
            // Bookmarks (mirrors OpenTween's Ctrl+S = Fav add,
            // Ctrl+Shift+S = Fav remove):
            //   * Cmd+S (macOS) / Ctrl+S (Win, Linux): bookmark
            //   * Cmd+Shift+S (macOS) / Ctrl+Shift+S (Win, Linux): clear
            // KeyModifiers.Meta is Command on macOS; KeyModifiers.Control
            // is Ctrl cross-platform. We accept either modifier family so
            // the same chord works on both Mac and Windows.
            case Key.S when e.KeyModifiers == KeyModifiers.Meta:
            case Key.S when e.KeyModifiers == KeyModifiers.Control:
                BookmarkSelected();
                e.Handled = true;
                break;
            case Key.S when e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Meta):
            case Key.S when e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Control):
                UnbookmarkSelected();
                e.Handled = true;
                break;
        }
    }

    private void BookmarkSelected()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Timeline.SelectedItem is not { } item) return;
        if (item.BookmarkCommand.CanExecute(null))
        {
            item.BookmarkCommand.Execute(null);
        }
    }

    private void UnbookmarkSelected()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Timeline.SelectedItem is not { } item) return;
        if (item.UnbookmarkCommand.CanExecute(null))
        {
            item.UnbookmarkCommand.Execute(null);
        }
    }

    private void FocusTimeline()
    {
        TimelineList()?.Focus();
    }

    private void FocusDetail()
    {
        DetailScroll()?.Focus();
    }

    private bool IsDetailFocused()
    {
        var focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
        var scroll = DetailScroll();
        if (scroll is null || focused is null)
        {
            return false;
        }
        // Detail body is "in focus" when the focused control is the
        // ScrollViewer itself or one of its visual descendants — covers
        // the case where the user clicked a markdown block / Expander
        // header inside the body.
        return focused == scroll
            || focused.GetVisualAncestors().Any(a => a == scroll);
    }

    private void ScrollDetail(int direction)
    {
        var scroll = DetailScroll();
        if (scroll is null)
        {
            return;
        }
        // Page-by-viewport, leaving a small overlap so the user's eyes
        // can pick up where they left off — mirrors browser Space/Shift+Space.
        var step = scroll.Viewport.Height * 0.9;
        var current = scroll.Offset.Y;
        var next = current + direction * step;
        var max = scroll.Extent.Height - scroll.Viewport.Height;
        if (max < 0) max = 0;
        if (next < 0) next = 0;
        if (next > max) next = max;
        scroll.Offset = new Avalonia.Vector(scroll.Offset.X, next);
    }

    private void MoveTimelineSelection(int delta)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var items = vm.Timeline.Items;
        if (items.Count == 0)
        {
            return;
        }
        var current = vm.Timeline.SelectedItem is null ? -1 : items.IndexOf(vm.Timeline.SelectedItem);
        // Clamp at the boundaries instead of wrapping. Wrapping made
        // "I just landed on the newest row, now press ↓" jump back to the
        // oldest, which felt random — Tween-style stop-at-edge is what
        // the user actually expected.
        int next;
        if (current < 0)
        {
            next = delta > 0 ? 0 : items.Count - 1;
        }
        else
        {
            next = current + delta;
            if (next < 0 || next >= items.Count)
            {
                return; // already at the edge in that direction
            }
        }
        vm.Timeline.SelectedItem = items[next];

        // Keep the row visible after a move and pull focus to the TL so
        // subsequent Up/Down arrow presses route here too rather than
        // triggering Avalonia's directional focus traversal (which used
        // to land on the repo-filter toggle from the toolbar).
        var list = TimelineList();
        list?.ScrollIntoView(items[next]);
        list?.Focus();
    }

    /// <summary>
    /// Jump to the oldest unread row in the current timeline (so the user
    /// can clear the backlog top-down). When no unread rows remain, jump
    /// to the newest row instead — gives the keyboard-only user a quick
    /// "go to the end of the timeline" shortcut once everything is read.
    /// Mirrors OpenTween's Space behavior.
    /// </summary>
    private void JumpToUnread()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var items = vm.Timeline.Items;
        if (items.Count == 0)
        {
            return;
        }

        // Items are ordered oldest-first (Tween-style: newest at bottom),
        // so the first match is the oldest unread.
        var target = items.FirstOrDefault(i => i.Unread)
                     ?? items[^1];
        vm.Timeline.SelectedItem = target;

        var list = TimelineList();
        list?.ScrollIntoView(target);
        list?.Focus();
    }

    /// <summary>
    /// Jump to the next/previous timeline row of the SAME thread
    /// (<see cref="TimelineItemViewModel.NotificationId"/>) whose
    /// <see cref="TimelineItemViewModel.EventKind"/> matches one of
    /// <paramref name="kinds"/>. Used to walk PR/comment events of one
    /// PR without sliding into a neighboring thread's noise. With no
    /// current selection or no matching sibling event, the call is a
    /// no-op so a stray key press doesn't yank focus to a different PR.
    /// </summary>
    private void JumpToKind(int delta, params Core.Domain.NotificationEventKind[] kinds)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var items = vm.Timeline.Items;
        var current = vm.Timeline.SelectedItem;
        if (current is null || items.Count == 0)
        {
            return;
        }
        var threadId = current.NotificationId;
        if (string.IsNullOrEmpty(threadId))
        {
            return;
        }

        var startIdx = items.IndexOf(current);
        for (var step = 1; step <= items.Count; step++)
        {
            var idx = startIdx + step * delta;
            if (idx < 0 || idx >= items.Count)
            {
                break;
            }
            var item = items[idx];
            if (!string.Equals(item.NotificationId, threadId, System.StringComparison.Ordinal))
            {
                continue;
            }
            if (System.Array.IndexOf(kinds, item.EventKind) >= 0)
            {
                vm.Timeline.SelectedItem = item;
                TimelineList()?.ScrollIntoView(item);
                return;
            }
        }
    }

    private void CycleTab(int delta)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var tabs = vm.Tabs;
        if (tabs.Count == 0)
        {
            return;
        }
        var current = tabs.ToList().IndexOf(vm.SelectedTab);
        var next = current < 0 ? 0 : current + delta;
        next = ((next % tabs.Count) + tabs.Count) % tabs.Count;
        vm.SelectedTab = tabs[next];

        // Pull focus to the timeline ListBox. Otherwise focus stays on
        // the bottom Tabs ListBox after a mouse click on a tab name,
        // and that ListBox's incremental letter-search swallows
        // subsequent A/S key presses (or fights with our tunnel handler
        // on every press), which the user perceives as a lag specific
        // to whichever tab they last clicked into.
        TimelineList()?.Focus();
    }
}
