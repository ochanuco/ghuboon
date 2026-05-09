using Serilog.Core;
using Serilog.Events;

namespace Ghuboon.Tests.Infrastructure.GitHub;

/// <summary>
/// Thread-safe Serilog sink that captures rendered messages for assertions.
/// Used to verify the GitHub API client never logs Authorization headers or PAT values.
/// </summary>
internal sealed class TestLogSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var rendered = logEvent.RenderMessage();
        var combined = rendered;

        // Also dump raw scalar property values to make any accidental Authorization header
        // capture observable to tests.
        if (logEvent.Properties.Count > 0)
        {
            var props = string.Join(",", logEvent.Properties.Select(kv => $"{kv.Key}={kv.Value}"));
            combined = rendered + " | " + props;
        }

        lock (_gate)
        {
            _events.Add(combined);
        }
    }
}
