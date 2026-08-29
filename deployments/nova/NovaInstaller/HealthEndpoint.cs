using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NovaInstaller;

/// <summary>
/// The HTTP surface every deployment target probes: <c>GET /health</c> and
/// <c>GET /app_icon.png</c>, served below the host-injected <c>BASE_PATH</c>.
/// An app that already has a <see cref="WebApplication"/> adds it with
/// <see cref="AddHealthEndpoint"/> plus <see cref="UseHealthEndpoint"/>; a process
/// with no HTTP surface of its own gets a host from <see cref="TryStartAsync"/>.
/// </summary>
public static class HealthEndpoint
{
    /// <summary>Every deployment target probes this port, so it is the default everywhere.</summary>
    public const int DefaultPort = 8080;

    // 1x1 transparent PNG — NOVA requires an app_icon path to be servable.
    private const string IconBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly byte[] Icon = Convert.FromBase64String(IconBase64);

    /// <summary>
    /// Binds <see cref="DefaultPort"/> unless the host was already pointed elsewhere
    /// through ASPNETCORE_URLS, <c>--urls</c> or a launch profile.
    /// </summary>
    public static WebApplicationBuilder AddHealthEndpoint(
        this WebApplicationBuilder builder, int port = DefaultPort)
    {
        if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
            builder.WebHost.UseUrls($"http://+:{port}");

        return builder;
    }

    /// <summary>
    /// Mounts the app below <c>BASE_PATH</c> and maps the probe endpoints. Call before
    /// any other middleware so the whole app is served relative to the base path.
    /// </summary>
    public static WebApplication UseHealthEndpoint(this WebApplication app)
    {
        var basePath = Environment.GetEnvironmentVariable("BASE_PATH")?.Trim('/') ?? "";
        if (basePath.Length > 0)
            app.UsePathBase("/" + basePath);

        app.MapGet("/health", () => Results.Json(new { status = "healthy" }));
        app.MapGet("/app_icon.png", () => Results.Bytes(Icon, "image/png"));
        return app;
    }

    /// <summary>
    /// Starts a host that serves nothing but the probe endpoints. Returns null when the
    /// port is already taken — several processes on one dev machine must not fail to start.
    /// </summary>
    public static async Task<WebApplication?> TryStartAsync(
        int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // A port clash is expected on a dev machine; the message below replaces the host's stack trace.
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
        // A probe arriving every second must not fill the log with request traces.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.AddHealthEndpoint(port);

        var app = builder.Build();
        app.UseHealthEndpoint();

        try
        {
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch (IOException e)
        {
            Console.WriteLine($"Health endpoint disabled: {e.Message}");
            await app.DisposeAsync();
            return null;
        }
    }
}
