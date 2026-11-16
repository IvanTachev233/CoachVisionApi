using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CoachVision.Api.Logging;

/// <summary>
/// Writes each log entry as one line of JSON that Cloud Logging reads as a structured entry:
/// <c>severity</c> in its names, <c>message</c>, <c>time</c>, the logger's category, the event id,
/// the message template's values under <c>fields</c>, and an exception under <c>stack_trace</c>.
/// </summary>
public sealed class CloudLoggingJsonFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName   =  "cloud-json";

    private const string TemplateKey = "{OriginalFormat}";

    // Log text is never put in HTML, so leave characters such as < and ' readable.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("severity", Severity(logEntry.LogLevel));
            json.WriteString("message", message ?? "");
            json.WriteString("time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("logger", logEntry.Category);
            if (logEntry.EventId.Id != 0)
            {
                json.WriteNumber("eventId", logEntry.EventId.Id);
            }

            WriteFields(json, logEntry.State);
            if (logEntry.Exception is not null)
            {
                json.WriteString("stack_trace", logEntry.Exception.ToString());
            }

            json.WriteEndObject();
        }

        textWriter.Write(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        textWriter.Write('\n');
    }

    private static string Severity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };

    // Values are written as strings, so a field keeps one JSON type whatever was logged into it.
    private static void WriteFields<TState>(Utf8JsonWriter json, TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            return;
        }

        var started = false;
        foreach (var (key, value) in values)
        {
            if (key == TemplateKey)
            {
                continue;
            }

            if (!started)
            {
                json.WriteStartObject("fields");
                started = true;
            }

            json.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        if (started)
        {
            json.WriteEndObject();
        }
    }
}
