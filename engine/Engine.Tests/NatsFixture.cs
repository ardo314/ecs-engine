using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Engine.Coordinator;
using Engine.Core;
using Engine.Core.Messages;
using NATS.Client.Core;

namespace Engine.Tests;

/// <summary>
/// Manages a nats-server process and a shared coordinator for integration tests.
/// Reads NATS_URL from environment, tries nats-server on PATH, then falls back to localhost:4222.
/// </summary>
public class NatsFixture : IAsyncLifetime
{
    private Process? _process;
    private string _url = null!;
    private NatsConnection _coordNats = null!;
    private CancellationTokenSource _cts = null!;
    private bool _available;

    public string Url => _url;
    public bool Available => _available;
    public SystemRegistry Registry { get; private set; } = null!;
    public WorldState World { get; private set; } = null!;
    public WatchManager WatchManager { get; private set; } = null!;
    public ConcurrentQueue<EntitySpawnRequest> PendingSpawns { get; private set; } = null!;
    public NatsHandlers Handlers { get; private set; } = null!;

    public async Task<NatsConnection> ConnectAsync()
    {
        var conn = new NatsConnection(new NatsOpts { Url = _url });
        await conn.ConnectAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        Serialization.Initialize();

        // 1. Check NATS_URL env var (set in CI or docker-compose)
        var envUrl = Environment.GetEnvironmentVariable("NATS_URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            _url = envUrl;
            var envPort = new Uri(envUrl).Port;
            try
            {
                await WaitForPortAsync(new Uri(envUrl).Host, envPort, TimeSpan.FromSeconds(5));
            }
            catch
            {
                _available = false;
                return;
            }
        }
        else
        {
            // 2. Try to start nats-server on a random port
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
            }
            catch
            {
                _process?.Dispose();
                _process = null;

                // 3. Fall back to localhost:4222
                _url = "nats://localhost:4222";
                try
                {
                    await WaitForPortAsync("localhost", 4222, TimeSpan.FromSeconds(2));
                }
                catch
                {
                    _available = false;
                    return;
                }
            }
        }

        // Start a shared coordinator
        _coordNats = await ConnectAsync();
        Registry = new SystemRegistry();
        World = new WorldState();
        WatchManager = new WatchManager();
        PendingSpawns = new ConcurrentQueue<EntitySpawnRequest>();

        Handlers = new NatsHandlers(_coordNats, Registry, World, WatchManager, PendingSpawns);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Handlers.StartAsync(_cts.Token));
        await Handlers.Ready;

        // Let NATS propagate subscriptions
        await Task.Delay(300);
        _available = true;
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            _cts.Cancel();
            await _coordNats.DisposeAsync();
            _cts.Dispose();
        }

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
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
    /// <summary>
    /// Call at the start of integration tests. Throws if NATS is not available,
    /// causing the test to fail with a clear message.
    /// In CI (GitHub Actions), NATS is always available via service container.
    /// </summary>
    public void EnsureAvailable()
    {
        if (!_available)
            throw new InvalidOperationException(
                "NATS not available. Set NATS_URL env var, install nats-server on PATH, " +
                "or run 'docker compose up nats -d'.");
    }
}

[CollectionDefinition("NATS")]
public class NatsCollection : ICollectionFixture<NatsFixture>;
