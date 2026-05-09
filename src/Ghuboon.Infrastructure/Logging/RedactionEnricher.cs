using Serilog.Core;
using Serilog.Events;

namespace Ghuboon.Infrastructure.Logging;

/// <summary>
/// Walks log event properties and rewrites string-shaped values via <see cref="SecretRedactor"/>.
/// Also redacts the rendered MessageTemplate content where a string property carried a secret.
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Snapshot keys to avoid mutating the dictionary while iterating.
        foreach (var key in logEvent.Properties.Keys.ToArray())
        {
            var original = logEvent.Properties[key];
            var redacted = RedactValue(original);
            if (!ReferenceEquals(original, redacted))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(key, redacted));
            }
        }
    }

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar when scalar.Value is string s:
            {
                var red = SecretRedactor.Redact(s);
                return ReferenceEquals(red, s) || red == s
                    ? scalar
                    : new ScalarValue(red);
            }

            case SequenceValue seq:
            {
                var changed = false;
                var items = new LogEventPropertyValue[seq.Elements.Count];
                for (var i = 0; i < seq.Elements.Count; i++)
                {
                    items[i] = RedactValue(seq.Elements[i]);
                    if (!ReferenceEquals(items[i], seq.Elements[i]))
                    {
                        changed = true;
                    }
                }
                return changed ? new SequenceValue(items) : seq;
            }

            case StructureValue str:
            {
                var changed = false;
                var props = new LogEventProperty[str.Properties.Count];
                for (var i = 0; i < str.Properties.Count; i++)
                {
                    var p = str.Properties[i];
                    var newVal = RedactValue(p.Value);
                    props[i] = ReferenceEquals(newVal, p.Value)
                        ? p
                        : new LogEventProperty(p.Name, newVal);
                    if (!ReferenceEquals(newVal, p.Value))
                    {
                        changed = true;
                    }
                }
                return changed ? new StructureValue(props, str.TypeTag) : str;
            }

            case DictionaryValue dict:
            {
                var changed = false;
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dict.Elements.Count);
                foreach (var kv in dict.Elements)
                {
                    var newVal = RedactValue(kv.Value);
                    entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key, newVal));
                    if (!ReferenceEquals(newVal, kv.Value))
                    {
                        changed = true;
                    }
                }
                return changed ? new DictionaryValue(entries) : dict;
            }

            default:
                return value;
        }
    }
}
