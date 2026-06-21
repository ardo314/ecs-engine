using System.Diagnostics;
using System.Net.Sockets;
using Engine.Core;

namespace Client.Tests.Integration;

/// <summary>
/// Provides a NATS URL for client integration tests.
/// Expects a running engine coordinator (e.g. via docker compose).
/// </summary>
public class NatsClientFixture : IAsyncLifetime
{
    private Process? _process;
    private string _url = null!;
    private bool _available;

    public string Url => _url;
    public bool Available => _available;

    public async Task InitializeAsync()
    {
        Serialization.Initialize();

        var envUrl = Environment.GetEnvironmentVariable("NATS_URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            _url = envUrl;
            try
            {
                await WaitForPortAsync(new Uri(envUrl).Host, new Uri(envUrl).Port, TimeSpan.FromSeconds(5));
                _available = true;
            }
            catch
            {
                _available = false;
            }
            return;
        }

        // Try nats-server on PATH
        var port = GetFreePort();
        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nats-server",
                    Arguments = $"-p {port}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _process.Start();
            await WaitForPortAsync("localhost", port, TimeSpan.FromSeconds(5));
            _url = $"nats://localhost:{port}";
            _available = true;
            return;
        }
        catch
        {
            _process?.Dispose();
            _process = null;
        }

        // Fall back to localhost:4222
        _url = "nats://localhost:4222";
        try
        {
            await WaitForPortAsync("localhost", 4222, TimeSpan.FromSeconds(2));
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
    }

    public void EnsureAvailable()
    {
        if (!_available)
            throw new InvalidOperationException(
                "NATS not available. Set NATS_URL env var, install nats-server on PATH, " +
                "or run 'docker compose up nats -d'.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"{host}:{port} not available within {timeout}");
    }
}

[CollectionDefinition("NATS")]
public class NatsClientCollection : ICollectionFixture<NatsClientFixture>;
