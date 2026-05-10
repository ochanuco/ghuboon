using System.Runtime.InteropServices;

namespace Ghuboon.Infrastructure.Storage;

/// <summary>
/// Resolves the on-disk location of the encrypted SQLite database.
/// macOS: <c>~/Library/Application Support/Ghuboon/ghuboon.db</c>.
/// Windows: <c>%LOCALAPPDATA%\Ghuboon\ghuboon.db</c>.
/// </summary>
public static class StoragePaths
{
    public const string AppFolderName = "Ghuboon";

    public const string DatabaseFileName = "ghuboon.db";

    /// <summary>
    /// Returns the directory the app stores its database in. Does not create it.
    /// Resolution order is:
    ///   1) Environment.SpecialFolder.LocalApplicationData (the cross-platform
    ///      .NET API; on macOS this is ~/Library/Application Support, on
    ///      Windows it is %LOCALAPPDATA%, on Linux it is ~/.local/share).
    ///   2) Linux-only XDG_DATA_HOME if set and SpecialFolder did not resolve.
    ///   3) HOME / SpecialFolder.UserProfile + the platform-conventional suffix.
    /// Falls through to <see cref="Path.GetTempPath"/> as a last resort so we
    /// never throw — a sandbox without HOME or LOCALAPPDATA is a degraded but
    /// recoverable state.
    /// </summary>
    public static string GetAppDataDirectory()
    {
        // Issue #12: prefer SpecialFolder.LocalApplicationData on every
        // platform. It maps to the right OS convention and degrades cleanly
        // when env vars are missing.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, AppFolderName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = ResolveHome();
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, "Library", "Application Support", AppFolderName);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // SpecialFolder above already handles %LOCALAPPDATA%. If it came
            // back empty (e.g. sandboxed user profile), try the env var
            // directly before falling through.
            var winLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(winLocal))
            {
                return Path.Combine(winLocal, AppFolderName);
            }
        }
        else
        {
            // Linux / other Unix. XDG spec wins when set, then HOME-based
            // fallback.
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg))
            {
                return Path.Combine(xdg, AppFolderName);
            }

            var home = ResolveHome();
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, ".local", "share", AppFolderName);
            }
        }

        // Last-resort: temp directory. Better than throwing in a sandboxed
        // context where neither LOCALAPPDATA nor HOME resolves.
        return Path.Combine(Path.GetTempPath(), AppFolderName);
    }

    private static string ResolveHome() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetAppDataDirectory(), DatabaseFileName);
}
