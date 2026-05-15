using System;
using System.Threading.Tasks;

namespace Ghuboon.App.Threading;

/// <summary>
/// Abstraction over Avalonia's UI dispatcher. ViewModels that need to
/// fire INotifyPropertyChanged on the UI thread (Avalonia 12 doesn't
/// gracefully marshal cross-thread notifications) post through this
/// instead of taking a hard dependency on
/// <c>Avalonia.Threading.Dispatcher.UIThread</c> directly.
///
/// Goals:
/// * Replaces the ad-hoc <c>RunOnUi</c> helpers and
///   <c>Application.Current is null</c> test fallbacks scattered across
///   the codebase.
/// * Lets unit tests inject a synchronous fake without booting
///   Avalonia (the previous workaround silently dropped property
///   updates when no <c>Application</c> was active).
/// </summary>
public interface IUiScheduler
{
    /// <summary>
    /// True if the calling thread IS the UI thread. Callers can use
    /// this to short-circuit a marshalled post when they're already
    /// where they need to be.
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// Fire-and-forget: queue <paramref name="action"/> on the UI
    /// thread at the requested priority and return immediately. Use
    /// when the caller does not need to await completion.
    /// </summary>
    void Post(Action action, UiPriority priority = UiPriority.Normal);

    /// <summary>
    /// Queue <paramref name="action"/> on the UI thread and return a
    /// Task that completes after the action has run. Awaiting this
    /// keeps the caller's sync context out of the picture.
    /// </summary>
    Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal);
}
