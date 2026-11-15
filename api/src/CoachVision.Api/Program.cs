using CoachVision.Api.Logging;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// One JSON line per entry on standard output, which Cloud Run passes to Cloud Logging.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = CloudLoggingJsonFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CloudLoggingJsonFormatter, ConsoleFormatterOptions>();

// No checks are registered: /health answers as soon as the app serves requests. Cloud Run's
// startup probe and the uptime check call it, so it must stay off the database once there is one.
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health").ShortCircuit();

app.Run();
