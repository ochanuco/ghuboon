using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Ghuboon.App.Threading;

/// <summary>
/// Production <see cref="IUiScheduler"/> backed by Avalonia's UI
/// dispatcher. Maps <see cref="UiPriority"/> to the equivalent
/// <see cref="DispatcherPriority"/> values.
/// </summary>
public sealed class AvaloniaUiScheduler : IUiScheduler
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action, MapPriority(priority));
    }

    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Dispatcher.UIThread.InvokeAsync(action, MapPriority(priority)).GetTask();
    }

    private static DispatcherPriority MapPriority(UiPriority priority) => priority switch
    {
        UiPriority.Background => DispatcherPriority.Background,
        UiPriority.Send => DispatcherPriority.Send,
        _ => DispatcherPriority.Normal,
    };
}
