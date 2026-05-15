using System;
using System.Threading.Tasks;

namespace Ghuboon.App.Threading;

/// <summary>
/// Test / design-time <see cref="IUiScheduler"/>. Runs everything
/// inline on the calling thread so unit tests don't need a running
/// Avalonia dispatcher loop. Replaces the previous
/// <c>Avalonia.Application.Current is null</c> fallback used by
/// <see cref="ViewModels.TimelineItemViewModel.RunOnUi"/>.
/// </summary>
public sealed class ImmediateUiScheduler : IUiScheduler
{
    public bool CheckAccess() => true;

    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
