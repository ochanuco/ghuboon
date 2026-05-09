using System;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;

namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// About section of the Settings screen. Displays the app version and a button
/// that opens the project repository in the user's default browser.
/// </summary>
public partial class AboutSettingsViewModel : ViewModelBase
{
    /// <summary>Project repository URL opened by the "Open repository" button.</summary>
    public const string RepositoryUrl = "https://github.com/ochanuco/ghuboon";

    private readonly Action<string> _openBrowser;

    public AboutSettingsViewModel()
        : this(DefaultOpenBrowser)
    {
    }

    public AboutSettingsViewModel(Action<string> openBrowser)
    {
        _openBrowser = openBrowser;
        Version = ResolveVersion();
    }

    public string Version { get; }

    public string RepositoryDisplay => RepositoryUrl;

    [RelayCommand]
    private void OpenRepository()
    {
        _openBrowser(RepositoryUrl);
    }

    /// <summary>
    /// Default browser-open helper used in production.
    /// </summary>
    public static void DefaultOpenBrowser(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser-launch failures are non-fatal. Phase 14 surfaces UI status
            // messages; for now we swallow rather than crash the Settings screen.
        }
    }

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return info!;
        }

        var version = asm.GetName().Version?.ToString();
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version!;
        }

        return "0.1.0-dev";
    }
}
