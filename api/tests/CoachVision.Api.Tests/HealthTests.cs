using System.Net;
using CoachVision.Api.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CoachVision.Api.Tests;

public sealed class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_answers_200()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_path_is_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Console_logs_use_the_cloud_logging_json_formatter()
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        Assert.Equal(CloudLoggingJsonFormatter.FormatterName, options.FormatterName);
    }
}
