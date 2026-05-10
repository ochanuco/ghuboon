using System;
using System.Runtime.InteropServices;
using Ghuboon.App.Platform.MacOS;

namespace Ghuboon.App.Platform;

/// <summary>
/// Resolves the appropriate <see cref="IMenuBarHost"/> implementation for the
/// current OS. Per ADR-015, MVP plans to ship the macOS menu-bar host on macOS.
///
/// The macOS host is opt-in via the <c>GHUBOON_MACOS_MENUBAR=1</c> environment
/// variable while issue #51 is unresolved (the P/Invoke trampoline deadlocks
/// the main thread inside <c>NSMenuItem initWithTitle:</c>, blocking
/// Avalonia's NSWindow creation). Default is <see cref="NoOpMenuBarHost"/> so
/// the app always shows its window.
/// </summary>
public static class MenuBarHostFactory
{
    public const string MacOSEnableEnvVar = "GHUBOON_MACOS_MENUBAR";

    public static IMenuBarHost Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            string.Equals(Environment.GetEnvironmentVariable(MacOSEnableEnvVar), "1", StringComparison.Ordinal))
        {
            return new MacOSMenuBarHost();
        }

        return new NoOpMenuBarHost();
    }
}
