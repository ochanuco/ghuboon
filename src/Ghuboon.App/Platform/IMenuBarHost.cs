using System;

namespace Ghuboon.App.Platform;

/// <summary>
/// Cross-platform abstraction for an OS menu-bar / tray host.
///
/// Phase 12 (ADR-015) ships only the macOS menu-bar implementation.
/// Windows task-tray support is intentionally deferred and will get its own
/// implementation behind this interface.
/// </summary>
public interface IMenuBarHost : IDisposable
{
    /// <summary>
    /// Initialize the menu-bar host with delegates the host can invoke when
    /// the user picks an item from the OS menu. Must be called from the UI
    /// thread (the implementation is responsible for marshalling back to the
    /// UI thread when invoking the delegates).
    /// </summary>
    void Initialize(MenuBarContext context);

    /// <summary>
    /// Update the unread-count indicator surfaced by the menu bar (e.g. the
    /// "Unread: N" menu item). Safe to call before <see cref="Initialize"/>;
    /// implementations should treat that as a no-op.
    /// </summary>
    void UpdateUnreadCount(int count);
}

/// <summary>
/// Delegates the menu-bar host invokes in response to user interaction.
/// All callbacks are invoked from native callbacks; the App layer is
/// responsible for any UI-thread marshalling required.
/// </summary>
/// <param name="ShowMainWindow">Bring the main window to the foreground.</param>
/// <param name="HideMainWindow">Hide the main window without quitting.</param>
/// <param name="SyncNow">Trigger an immediate sync.</param>
/// <param name="OpenSettings">Open the in-app settings surface.</param>
/// <param name="Quit">Terminate the application.</param>
public sealed record MenuBarContext(
    Action ShowMainWindow,
    Action HideMainWindow,
    Action SyncNow,
    Action OpenSettings,
    Action Quit);
