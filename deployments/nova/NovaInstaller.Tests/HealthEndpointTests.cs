using System.Net;

namespace NovaInstaller.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task Health_ReturnsHealthyJson()
    {
        await using var app = await HealthEndpoint.TryStartAsync(0);
        Assert.NotNull(app);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.GetAsync($"http://localhost:{new Uri(app.Urls.First()).Port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TryStartAsync_ReturnsNullWhenThePortIsTaken()
    {
        await using var first = await HealthEndpoint.TryStartAsync(0);
        Assert.NotNull(first);

        Assert.Null(await HealthEndpoint.TryStartAsync(new Uri(first.Urls.First()).Port));
    }
}
