using System.Net;
using System.Net.Http;
using Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Client.Tests.Unit;

public class HealthEndpointTests
{
    private static HttpClient HttpFor(WebApplication app) => new()
    {
        BaseAddress = new Uri($"http://localhost:{new Uri(app.Urls.First()).Port}"),
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static async Task WithProbeHostAsync(string? basePath, Func<HttpClient, Task> assert)
    {
        var previous = Environment.GetEnvironmentVariable("BASE_PATH");
        Environment.SetEnvironmentVariable("BASE_PATH", basePath);
        try
        {
            await using var app = await HealthEndpoint.TryStartAsync(0);
            Assert.NotNull(app);

            using var http = HttpFor(app);
            await assert(http);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BASE_PATH", previous);
        }
    }

    [Fact]
    public Task Health_ReturnsHealthyJson() => WithProbeHostAsync(null, async http =>
    {
        using var response = await http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"healthy"}""", await response.Content.ReadAsStringAsync());
    });

    [Fact]
    public Task AppIcon_ReturnsAPng() => WithProbeHostAsync(null, async http =>
    {
        using var response = await http.GetAsync("/app_icon.png");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
    });

    [Fact]
    public Task UnknownPath_Returns404() => WithProbeHostAsync(null, async http =>
    {
        using var response = await http.GetAsync("/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    public Task Health_IgnoresQueryString() => WithProbeHostAsync(null, async http =>
    {
        using var response = await http.GetAsync("/health?probe=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    });

    [Fact]
    public Task Endpoints_AreServedBelowTheInjectedBasePath() =>
        WithProbeHostAsync("/cell/ecs-engine", async http =>
        {
            using var health = await http.GetAsync("/cell/ecs-engine/health");
            using var icon = await http.GetAsync("/cell/ecs-engine/app_icon.png");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        });

    [Fact]
    public async Task BasePath_AppliesToEveryEndpointNotJustTheProbes()
    {
        var previous = Environment.GetEnvironmentVariable("BASE_PATH");
        Environment.SetEnvironmentVariable("BASE_PATH", "/cell/ecs-editor-api");
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.AddHealthEndpoint(0);

            await using var app = builder.Build();
            app.UseBasePath();
            app.UseHealthEndpoint();
            app.MapGet("/api/entities", () => Results.Ok());
            await app.StartAsync();

            using var http = HttpFor(app);
            using var response = await http.GetAsync("/cell/ecs-editor-api/api/entities");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BASE_PATH", previous);
        }
    }

    [Fact]
    public async Task TryStartAsync_ReturnsNullWhenThePortIsTaken()
    {
        await using var first = await HealthEndpoint.TryStartAsync(0);
        Assert.NotNull(first);

        var taken = new Uri(first.Urls.First()).Port;
        Assert.Null(await HealthEndpoint.TryStartAsync(taken));
    }
}
