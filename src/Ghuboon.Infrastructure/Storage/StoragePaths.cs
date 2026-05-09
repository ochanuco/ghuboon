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
    /// </summary>
    public static string GetAppDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppFolderName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, AppFolderName);
        }

        // Linux / fallback. Not officially supported by MVP but we still resolve
        // a reasonable location to make debugging possible.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, AppFolderName);
        }

        var fallbackHome = Environment.GetEnvironmentVariable("HOME")
                           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(fallbackHome, ".local", "share", AppFolderName);
    }

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetAppDataDirectory(), DatabaseFileName);
}
