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
    public void StartFromEnvironment_ReturnsNullWhenHealthPortIsUnset()
    {
        var previous = Environment.GetEnvironmentVariable("HEALTH_PORT");
        Environment.SetEnvironmentVariable("HEALTH_PORT", null);

        try
        {
            Assert.Null(HealthEndpoint.StartFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEALTH_PORT", previous);
        }
    }
}
