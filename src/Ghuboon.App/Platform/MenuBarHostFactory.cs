using System.Runtime.InteropServices;
using Ghuboon.App.Platform.MacOS;

namespace Ghuboon.App.Platform;

/// <summary>
/// Resolves the appropriate <see cref="IMenuBarHost"/> implementation for the
/// current OS. Per ADR-015, MVP ships the macOS menu-bar host and a no-op
/// host elsewhere.
/// </summary>
public static class MenuBarHostFactory
{
    public static IMenuBarHost Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSMenuBarHost();
        }

        return new NoOpMenuBarHost();
    }
}
