using System.Text.Json;
using CoachVision.Api.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoachVision.Api.Tests;

public sealed class CloudLoggingJsonFormatterTests
{
    private static string Format<TState>(
        LogLevel level,
        TState state,
        Func<TState, Exception?, string> formatter,
        Exception? exception = null,
        EventId eventId = default)
    {
        var entry = new LogEntry<TState>(level, "Vision.Category", eventId, state, exception, formatter);
        using var writer = new StringWriter();
        new CloudLoggingJsonFormatter().Write(in entry, scopeProvider: null, writer);
        return writer.ToString();
    }

    private static JsonElement FormatOne(LogLevel level, string message, Exception? exception = null) =>
        JsonDocument.Parse(Format(level, message, (text, _) => text, exception)).RootElement;

    [Theory]
    [InlineData(LogLevel.Trace, "DEBUG")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void Severity_uses_cloud_logging_names(LogLevel level, string severity)
    {
        Assert.Equal(severity, FormatOne(level, "hello").GetProperty("severity").GetString());
    }

    [Fact]
    public void One_entry_is_one_line_of_json_with_message_logger_and_time()
    {
        var text = Format(LogLevel.Information, "line one\nline two", (s, _) => s);

        Assert.EndsWith("\n", text);
        Assert.DoesNotContain('\n', text.TrimEnd('\n'));
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("line one\nline two", json.GetProperty("message").GetString());
        Assert.Equal("Vision.Category", json.GetProperty("logger").GetString());
        Assert.True(DateTimeOffset.TryParse(json.GetProperty("time").GetString(), out _));
    }

    [Fact]
    public void Exception_goes_in_stack_trace()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("broken");
        }
        catch (InvalidOperationException error)
        {
            thrown = error;
        }

        var json = FormatOne(LogLevel.Error, "failed", thrown);

        Assert.Equal("failed", json.GetProperty("message").GetString());
        var stack = json.GetProperty("stack_trace").GetString();
        Assert.Contains("System.InvalidOperationException: broken", stack);
        Assert.Contains(nameof(Exception_goes_in_stack_trace), stack);
    }

    [Fact]
    public void Structured_values_become_fields_without_the_template()
    {
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new("JobId", "job_01"),
            new("Frames", 120),
            new("{OriginalFormat}", "Job {JobId} has {Frames} frames"),
        ];

        var json = JsonDocument.Parse(
            Format(LogLevel.Information, state, (_, _) => "Job job_01 has 120 frames", eventId: new EventId(7, "Done"))).RootElement;

        var fields = json.GetProperty("fields");
        Assert.Equal("job_01", fields.GetProperty("JobId").GetString());
        Assert.Equal("120", fields.GetProperty("Frames").GetString());
        Assert.False(fields.TryGetProperty("{OriginalFormat}", out _));
        Assert.Equal(7, json.GetProperty("eventId").GetInt32());
    }

    [Fact]
    public void Entry_without_message_or_exception_writes_nothing()
    {
        Assert.Equal("", Format(LogLevel.Information, "", (s, _) => s));
    }

    [Fact]
    public void Missing_fields_are_left_out()
    {
        var json = FormatOne(LogLevel.Information, "plain");

        Assert.False(json.TryGetProperty("fields", out _));
        Assert.False(json.TryGetProperty("eventId", out _));
        Assert.False(json.TryGetProperty("stack_trace", out _));
    }
}
