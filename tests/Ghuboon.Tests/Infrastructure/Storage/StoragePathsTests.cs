using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

/// <summary>
/// Issue #12: validate that <see cref="StoragePaths"/> resolves a non-empty,
/// platform-appropriate directory under the runtime we're executing on. We
/// don't pin an exact path because it depends on the host's environment, but
/// we can verify the result is non-empty, ends with the app folder name, and
/// matches the OS convention.
/// </summary>
public class StoragePathsTests
{
    [Fact]
    public void GetAppDataDirectory_returns_non_empty_path_with_app_folder_suffix()
    {
        var dir = StoragePaths.GetAppDataDirectory();

        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.EndsWith(StoragePaths.AppFolderName, dir, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefaultDatabasePath_concatenates_directory_and_filename()
    {
        var path = StoragePaths.GetDefaultDatabasePath();
        var dir = StoragePaths.GetAppDataDirectory();

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.StartsWith(dir, path, StringComparison.Ordinal);
        Assert.EndsWith(StoragePaths.DatabaseFileName, path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAppDataDirectory_uses_platform_convention()
    {
        var dir = StoragePaths.GetAppDataDirectory();

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
        {
            // macOS: SpecialFolder.LocalApplicationData → ~/Library/Application Support.
            Assert.Contains("Application Support", dir, StringComparison.Ordinal);
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                     System.Runtime.InteropServices.OSPlatform.Linux))
        {
            // Linux: SpecialFolder.LocalApplicationData → ~/.local/share or
            // XDG_DATA_HOME if set.
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                Assert.Contains(".local", dir, StringComparison.Ordinal);
            }
        }
        // Windows: harder to assert without reproducing the env-var lookup.
        // The non-empty + suffix check above is enough.
    }
}
