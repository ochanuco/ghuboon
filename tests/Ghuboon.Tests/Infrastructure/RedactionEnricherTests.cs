using Ghuboon.Infrastructure.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Ghuboon.Tests.Infrastructure;

public class RedactionEnricherTests
{
    [Fact]
    public void Enrich_redacts_string_property_value()
    {
        var token = "ghp_" + new string('C', 40);
        var enricher = new RedactionEnricher();
        var props = new[]
        {
            new LogEventProperty("Authorization", new ScalarValue("token " + token)),
            new LogEventProperty("Count", new ScalarValue(3)),
        };
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("auth={Authorization} count={Count}", new MessageTemplateToken[0]),
            props);

        enricher.Enrich(evt, new TestPropertyFactory());

        var auth = ((ScalarValue)evt.Properties["Authorization"]).Value as string;
        Assert.NotNull(auth);
        Assert.DoesNotContain(token, auth!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", auth!);
        Assert.Equal(3, ((ScalarValue)evt.Properties["Count"]).Value);
    }

    [Fact]
    public void Logger_pipeline_writes_redacted_file_output()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ghuboon-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var token = "github_pat_" + new string('D', 30);
        try
        {
            using (var logger = GhuboonLogger.Create(dir))
            {
                logger.Information("call with token {Token}", token);
                logger.Information("authorization header line: Authorization: Bearer {Token}", token);
            }

            var files = Directory.GetFiles(dir, "ghuboon-*.log");
            Assert.NotEmpty(files);
            var content = string.Join("\n", files.Select(File.ReadAllText));

            Assert.DoesNotContain(token, content);
            Assert.Contains("[REDACTED]", content);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
