using System.Runtime.InteropServices;
using Ghuboon.Infrastructure.Storage;

namespace Ghuboon.Tests.Infrastructure.Storage;

[CollectionDefinition("EnvVarSensitive", DisableParallelization = true)]
public class EnvVarSensitiveCollection { }

/// <summary>
/// Issue #12: validate that <see cref="StoragePaths"/> resolves a non-empty,
/// platform-appropriate directory under the runtime we're executing on. We
/// don't pin an exact path because it depends on the host's environment, but
/// we can verify the result is non-empty, ends with the app folder name, and
/// matches the OS convention.
/// </summary>
[Collection("EnvVarSensitive")]
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

    /// <summary>
    /// Issue #42: USERPROFILE may be set to an empty string on misconfigured
    /// Windows hosts. The resolver must treat empty/whitespace exactly like
    /// null — fall through to <see cref="Environment.SpecialFolder.UserProfile"/>
    /// (and ultimately <see cref="Path.GetTempPath"/>) instead of producing
    /// a corrupt path like <c>"\AppData\Local\Ghuboon"</c>.
    ///
    /// The Windows fallback chain is unreachable from non-Windows hosts, so
    /// this test runs only when actually executed on Windows. On macOS/Linux
    /// CI it is skipped. The non-Windows hosts still exercise the same
    /// IsUsablePath predicate via the <c>XDG_DATA_HOME</c> path below.
    /// </summary>
    [Fact]
    public void GetAppDataDirectory_on_windows_treats_empty_or_whitespace_userprofile_as_unusable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip silently on non-Windows runners. Asserting nothing is
            // preferable to faking the platform: SpecialFolder.LocalApplicationData
            // resolves on macOS/Linux and the Windows branch never runs.
            return;
        }

        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            // Force the cascade past LOCALAPPDATA so the USERPROFILE branch
            // executes. SpecialFolder.LocalApplicationData on Windows reads
            // %LOCALAPPDATA% under the hood, so clearing the env var also
            // collapses the SpecialFolder result.
            Environment.SetEnvironmentVariable("LOCALAPPDATA", string.Empty);
            Environment.SetEnvironmentVariable("USERPROFILE", "   ");

            var dir = StoragePaths.GetAppDataDirectory();

            Assert.False(string.IsNullOrWhiteSpace(dir));
            Assert.EndsWith(StoragePaths.AppFolderName, dir, StringComparison.Ordinal);
            // The corrupt path the bug produced started with the bare
            // separator (e.g. "\AppData\Local\Ghuboon"). The fixed resolver
            // either uses SpecialFolder.UserProfile or falls through to
            // %TEMP%; both are absolute paths that do not start with the
            // separator alone.
            Assert.False(
                dir.StartsWith(@"\AppData", StringComparison.Ordinal),
                $"Resolver produced a path rooted at the separator: {dir}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
        }
    }

    /// <summary>
    /// Issue #42: cross-platform exercise of the same IsUsablePath predicate
    /// the Windows branch relies on. On Linux the resolver consults
    /// <c>XDG_DATA_HOME</c>; setting it to whitespace must be treated as
    /// unusable so the resolver cascades to <c>HOME/.local/share</c> (or
    /// SpecialFolder.LocalApplicationData, which the implementation prefers
    /// before XDG). The point is that whitespace input never produces a
    /// corrupt path.
    /// </summary>
    [Fact]
    public void GetAppDataDirectory_treats_whitespace_environment_as_unusable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The dedicated Windows test above covers this branch.
            return;
        }

        var originalXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "   ");

            var dir = StoragePaths.GetAppDataDirectory();

            Assert.False(string.IsNullOrWhiteSpace(dir));
            Assert.EndsWith(StoragePaths.AppFolderName, dir, StringComparison.Ordinal);
            // Whitespace XDG must not surface as the literal "   /Ghuboon"
            // prefix.
            Assert.DoesNotContain("   /", dir, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdg);
        }
    }

    /// <summary>
    /// Issue #47: <c>ResolveHome</c> previously used <c>??</c>, which only
    /// fell through on a null HOME. A whitespace HOME would skip
    /// <see cref="Environment.SpecialFolder.UserProfile"/> entirely and yield
    /// a corrupt path like <c>"   /Library/Application Support/Ghuboon"</c> on
    /// macOS or <c>"   /.local/share/Ghuboon"</c> on Linux.
    ///
    /// Cross-platform shape: clear LOCALAPPDATA / XDG_DATA_HOME so the
    /// SpecialFolder.LocalApplicationData (Windows / Linux) and XDG (Linux)
    /// branches collapse, then set HOME to whitespace. The resolver must
    /// either pick up SpecialFolder.UserProfile or fall through to
    /// <see cref="Path.GetTempPath"/> — never compose a path that begins with
    /// the whitespace prefix.
    /// </summary>
    [Fact]
    public void GetAppDataDirectory_treats_whitespace_home_as_unusable()
    {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            // Force the cascade to reach ResolveHome on every platform.
            Environment.SetEnvironmentVariable("LOCALAPPDATA", string.Empty);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", string.Empty);
            Environment.SetEnvironmentVariable("HOME", "   ");

            var dir = StoragePaths.GetAppDataDirectory();

            Assert.False(string.IsNullOrWhiteSpace(dir));
            Assert.EndsWith(StoragePaths.AppFolderName, dir, StringComparison.Ordinal);
            Assert.False(
                dir.StartsWith("   ", StringComparison.Ordinal),
                $"Resolver produced a path rooted at whitespace: {dir}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdg);
        }
    }
}
