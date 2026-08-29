using System.Net;

namespace NovaInstaller.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task Health_ReturnsHealthyJson()
    {
        using var endpoint = new HealthEndpoint(0);
        endpoint.Start();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.GetAsync($"http://localhost:{endpoint.Port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void TryStart_ReturnsNullWhenThePortIsTaken()
    {
        using var first = new HealthEndpoint(0);
        first.Start();

        Assert.Null(HealthEndpoint.TryStart(first.Port));
    }
}
