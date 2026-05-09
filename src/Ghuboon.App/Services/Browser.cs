using System;
using System.Diagnostics;

namespace Ghuboon.App.Services;

/// <summary>
/// Abstraction over "open this URL in the user's default browser".
/// Used by the Open in GitHub action and the About settings link, mocked in tests.
/// </summary>
public interface IBrowserService
{
    void OpenUrl(string url);
}

/// <summary>
/// Default <see cref="IBrowserService"/>: shells out to the platform handler via
/// <see cref="ProcessStartInfo.UseShellExecute"/> with the URL itself as the file.
/// </summary>
public sealed class Browser : IBrowserService
{
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

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
            // Browser-launch failures must not crash the app. The caller can surface
            // an error if it wants; the helper itself swallows so notifications keep flowing.
        }
    }
}
