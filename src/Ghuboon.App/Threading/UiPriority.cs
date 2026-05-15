namespace Ghuboon.App.Threading;

/// <summary>
/// Stable priority labels for UI-thread work, decoupled from Avalonia's
/// own <c>DispatcherPriority</c>. The production
/// <see cref="AvaloniaUiScheduler"/> maps these to the corresponding
/// Avalonia priority; test fakes can ignore the value entirely. Used by
/// <see cref="IUiScheduler"/> Post / InvokeAsync overloads so call
/// sites don't need to import Avalonia.Threading just to pick a
/// priority constant.
/// </summary>
public enum UiPriority
{
    /// <summary>
    /// Lowest priority. UI thread drains pending Input events first.
    /// Used by selection debounce so rapid A/S navigation doesn't pay
    /// per-row DetailItem/markdown render costs.
    /// </summary>
    Background,

    /// <summary>
    /// Default priority. Most VM property writes that need to land on
    /// the UI thread go here.
    /// </summary>
    Normal,

    /// <summary>
    /// Higher than Normal. Reserved for visible UI state that should
    /// not be deferred behind data updates (e.g. status text updates
    /// during sync).
    /// </summary>
    Send,
}
