using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace Ghuboon.App.Services;

/// <summary>
/// Abstraction over the platform clipboard. Used by the Copy URL action so the
/// timeline can be unit-tested without a running Avalonia application.
/// </summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>
/// Default <see cref="IClipboardService"/> backed by the Avalonia
/// top-level clipboard. Falls back to a no-op if no main window is available
/// (e.g. design-time previews).
/// </summary>
public sealed class AvaloniaClipboard : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (text is null)
        {
            return;
        }

        var app = Application.Current;
        if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } cb })
        {
            try
            {
                await cb.SetTextAsync(text).ConfigureAwait(false);
            }
            catch
            {
                // Clipboard failures must not crash the app.
            }
        }
    }
}
