using System.Diagnostics;
using Ghuboon.Core.Abstractions;
using Serilog;

namespace Ghuboon.Infrastructure.Notifications;

/// <summary>
/// macOS implementation of <see cref="IDesktopNotificationService"/> backed by
/// <c>osascript -e 'display notification ...'</c>. Chosen for the MVP because
/// it works without a signed bundle id or notification entitlement, which a
/// native <c>UNUserNotificationCenter</c> integration would require.
/// </summary>
/// <remarks>
/// <para>
/// Known limitation: <c>osascript</c>'s <c>display notification</c> verb does not
/// expose click callbacks, so <see cref="DesktopNotification.Url"/> is currently
/// ignored at the OS level. Follow-up work (PLAN.md Phase 11 backlog) should
/// migrate to <c>UNUserNotificationCenter</c> once the app has a signed bundle
/// id, which would enable click handling and richer presentation.
/// </para>
/// <para>
/// The process is launched with stderr redirected and consumed asynchronously
/// before <see cref="Process.WaitForExitAsync(System.Threading.CancellationToken)"/>
/// so the child cannot block writing to a full stderr pipe. stdout is left
/// inheriting the parent (we do not need its output) to avoid the same
/// deadlock class on the stdout pipe. Failures are logged and swallowed so
/// that a malfunctioning notification path cannot break sync (PLAN.md
/// Phase 11 acceptance criteria).
/// </para>
/// </remarks>
public sealed class MacOSDesktopNotificationService : IDesktopNotificationService
{
    private const string OsaScriptExecutable = "/usr/bin/osascript";

    private readonly ILogger _log;

    public MacOSDesktopNotificationService(ILogger log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task ShowAsync(DesktopNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            var script = BuildAppleScript(notification.Title, notification.Body);

            var psi = new ProcessStartInfo
            {
                FileName = OsaScriptExecutable,
                UseShellExecute = false,
                // stdout is intentionally not redirected: we never read it, so
                // redirecting + leaving it unread is a deadlock risk if the
                // child were to write more than the pipe buffer.
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                _log.Warning("osascript failed to start for notification {Id}", notification.Id);
                return;
            }

            // Start draining stderr before WaitForExitAsync so the child can
            // never block on a full stderr pipe (classic Process deadlock).
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _log.Warning(
                    "osascript exited with code {Code} for notification {Id}: {Stderr}",
                    process.ExitCode,
                    notification.Id,
                    stderr);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to show OS notification {Id}", notification.Id);
        }
    }

    /// <summary>
    /// Built-in macOS notification sound played when a banner appears.
    /// Resolved against <c>/System/Library/Sounds/&lt;name&gt;.aiff</c> by
    /// AppleScript; "Glass" is the default macOS notification chime and
    /// is available on every macOS install we target.
    /// </summary>
    internal const string DefaultSoundName = "Glass";

    /// <summary>
    /// Builds the AppleScript expression passed to <c>osascript -e</c>. Both the
    /// title and body strings are escaped via <see cref="EscapeForAppleScript"/>.
    /// A <c>sound name</c> clause is appended so the banner is announced
    /// audibly — without it, AppleScript notifications are silent regardless
    /// of the user's macOS notification settings.
    /// </summary>
    internal static string BuildAppleScript(string title, string body, string soundName = DefaultSoundName)
    {
        var safeTitle = EscapeForAppleScript(title ?? string.Empty);
        var safeBody = EscapeForAppleScript(body ?? string.Empty);
        var safeSound = EscapeForAppleScript(soundName ?? DefaultSoundName);
        return $"display notification \"{safeBody}\" with title \"{safeTitle}\" sound name \"{safeSound}\"";
    }

    /// <summary>
    /// Escapes a string for use inside an AppleScript double-quoted literal.
    /// Backslashes must be doubled first so subsequent escapes do not get
    /// re-escaped, then double-quotes are escaped. Other characters &mdash;
    /// including line breaks &mdash; are passed through; AppleScript treats raw
    /// newlines inside quoted literals as part of the string.
    /// </summary>
    internal static string EscapeForAppleScript(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
