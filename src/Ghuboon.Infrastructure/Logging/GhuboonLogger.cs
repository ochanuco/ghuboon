using System.Runtime.InteropServices;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ghuboon.Infrastructure.Logging;

/// <summary>
/// Builds a Serilog <see cref="Logger"/> wired with the redaction enricher and a file sink in
/// the per-platform application data directory (ADR-012).
/// </summary>
public static class GhuboonLogger
{
    public const string AppFolderName = "Ghuboon";
    public const string LogFileNamePrefix = "ghuboon-.log";
    public const int RetainedFileCountLimit = 14;
    public static readonly TimeSpan RetainedFileTimeLimit = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates a configured logger. Caller owns disposal.
    /// </summary>
    /// <param name="logDirectory">
    /// Optional override for the log directory; primarily for tests.
    /// </param>
    /// <param name="includeConsole">
    /// When true, also writes to console — useful for development.
    /// </param>
    public static Logger Create(string? logDirectory = null, bool includeConsole = false)
    {
        var dir = logDirectory ?? GetDefaultLogDirectory();
        Directory.CreateDirectory(dir);

        var logPath = Path.Combine(dir, LogFileNamePrefix);

        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.With(new RedactionEnricher())
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                retainedFileTimeLimit: RetainedFileTimeLimit,
                shared: true,
                restrictedToMinimumLevel: LogEventLevel.Information);

        if (includeConsole)
        {
            config = config.WriteTo.Console();
        }

        return config.CreateLogger();
    }

    /// <summary>
    /// Default log directory:
    /// <list type="bullet">
    /// <item>macOS: <c>~/Library/Application Support/Ghuboon/logs</c></item>
    /// <item>Windows: <c>%LOCALAPPDATA%\Ghuboon\logs</c></item>
    /// <item>Other: <c>$XDG_STATE_HOME/Ghuboon/logs</c> or <c>~/.local/state/Ghuboon/logs</c></item>
    /// </list>
    /// </summary>
    public static string GetDefaultLogDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppFolderName, "logs");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, AppFolderName, "logs");
        }

        var stateBase = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(stateBase))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stateBase = Path.Combine(home, ".local", "state");
        }
        return Path.Combine(stateBase, AppFolderName, "logs");
    }
}
