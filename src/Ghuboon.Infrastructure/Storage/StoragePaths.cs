using System.Diagnostics.CodeAnalysis;
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
    /// <remarks>
    /// Issue #37: every candidate is validated with
    /// <see cref="IsUsablePath(string?)"/> so empty/whitespace strings (which
    /// SpecialFolder occasionally returns on misconfigured environments) are
    /// rejected and we keep cascading instead of producing a path like
    /// <c>"\Ghuboon"</c>.
    /// </remarks>
    public static string GetAppDataDirectory()
    {
        // Issue #12: prefer SpecialFolder.LocalApplicationData on every
        // platform. It maps to the right OS convention and degrades cleanly
        // when env vars are missing.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (IsUsablePath(localAppData))
        {
            return Path.Combine(localAppData, AppFolderName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = ResolveHome();
            if (IsUsablePath(home))
            {
                return Path.Combine(home, "Library", "Application Support", AppFolderName);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Issue #37: cascade through every Windows fallback before giving
            // up to %TEMP%. Order matters — LOCALAPPDATA env var first
            // (matches what SpecialFolder would have returned), then USERPROFILE
            // + AppData\Local (the canonical layout when LOCALAPPDATA is unset
            // but USERPROFILE is, e.g. fresh service accounts).
            var winLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (IsUsablePath(winLocal))
            {
                return Path.Combine(winLocal, AppFolderName);
            }

            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsUsablePath(userProfile))
            {
                return Path.Combine(userProfile, "AppData", "Local", AppFolderName);
            }
        }
        else
        {
            // Linux / other Unix. XDG spec wins when set, then HOME-based
            // fallback.
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (IsUsablePath(xdg))
            {
                return Path.Combine(xdg, AppFolderName);
            }

            var home = ResolveHome();
            if (IsUsablePath(home))
            {
                return Path.Combine(home, ".local", "share", AppFolderName);
            }
        }

        // Last-resort: temp directory. Better than throwing in a sandboxed
        // context where neither LOCALAPPDATA nor HOME resolves.
        return Path.Combine(Path.GetTempPath(), AppFolderName);
    }

    /// <summary>
    /// Treats null / empty / whitespace-only strings as unusable so the
    /// resolver does not produce paths like <c>"\Ghuboon"</c> when an env var
    /// is set to an empty value.
    /// </summary>
    private static bool IsUsablePath([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value);

    private static string ResolveHome() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetAppDataDirectory(), DatabaseFileName);
}
