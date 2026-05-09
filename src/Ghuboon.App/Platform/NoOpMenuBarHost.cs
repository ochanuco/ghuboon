namespace Ghuboon.App.Platform;

/// <summary>
/// No-op menu-bar host used on platforms where Phase 12 does not yet provide
/// a tray/menu-bar implementation (Windows, Linux). Per ADR-015, Windows tray
/// support is deferred to a later phase.
/// </summary>
public sealed class NoOpMenuBarHost : IMenuBarHost
{
    public void Initialize(MenuBarContext context)
    {
        // Intentionally no-op: the platform has no menu-bar surface in MVP.
    }

    public void UpdateUnreadCount(int count)
    {
        // Intentionally no-op.
    }

    public void Dispose()
    {
        // Intentionally no-op.
    }
}
