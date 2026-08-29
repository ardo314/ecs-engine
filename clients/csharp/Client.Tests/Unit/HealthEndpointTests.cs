using System.Net.Http;
using Client;

namespace Client.Tests.Unit;

public class HealthEndpointTests
{
    private static async Task<HttpResponseMessage> GetAsync(HealthEndpoint endpoint, string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await http.GetAsync($"http://localhost:{endpoint.Port}{path}");
    }

    [Fact]
    public async Task Health_ReturnsHealthyJson()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var response = await GetAsync(endpoint, "/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AppIcon_ReturnsAPng()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var response = await GetAsync(endpoint, "/app_icon.png");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var response = await GetAsync(endpoint, "/nope");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_IgnoresQueryString()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var response = await GetAsync(endpoint, "/health?probe=1");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_KeepsServingAfterAClientHangsUpMidRequest()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using (var rude = new System.Net.Sockets.TcpClient())
        {
            await rude.ConnectAsync("localhost", endpoint.Port);
            await rude.GetStream().WriteAsync("GET /health"u8.ToArray());
        }

        using var response = await GetAsync(endpoint, "/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void TryStart_ReturnsNullWhenThePortIsTaken()
    {
        using var first = new HealthEndpoint(0);
        first.Start();

        Assert.Null(HealthEndpoint.TryStart(first.Port));
    }

    [Fact]
    public async Task Health_IsServedUnderAnIngressPrefix()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var response = await GetAsync(endpoint, "/cell/ecs-engine/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
