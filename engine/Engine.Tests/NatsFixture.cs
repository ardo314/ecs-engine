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
/// Falls back to an existing NATS server at localhost:4222 if nats-server binary isn't found.
/// </summary>
public class NatsFixture : IAsyncLifetime
{
    private Process? _process;
    private int _port;
    private NatsConnection _coordNats = null!;
    private CancellationTokenSource _cts = null!;

    public string Url => $"nats://localhost:{_port}";
    public SystemRegistry Registry { get; private set; } = null!;
    public WorldState World { get; private set; } = null!;
    public WatchManager WatchManager { get; private set; } = null!;
    public ConcurrentQueue<EntitySpawnRequest> PendingSpawns { get; private set; } = null!;
    public NatsHandlers Handlers { get; private set; } = null!;

    public async Task<NatsConnection> ConnectAsync()
    {
        var conn = new NatsConnection(new NatsOpts { Url = Url });
        await conn.ConnectAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        Serialization.Initialize();

        // Try to start embedded nats-server on a random port
        _port = GetFreePort();

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nats-server",
                    Arguments = $"-p {_port}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _process.Start();
            await WaitForPortAsync(_port, TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            _process?.Dispose();
            _process = null;
            _port = 4222;

            try
            {
                await WaitForPortAsync(_port, TimeSpan.FromSeconds(2));
            }
            catch
            {
                throw new InvalidOperationException(
                    "NATS integration tests require either 'nats-server' on PATH or a NATS server running on localhost:4222. " +
                    "Run 'docker compose up nats -d' to start one.");
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
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _coordNats.DisposeAsync();
        _cts.Dispose();

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

    private static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port);
                return;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"Port {port} not available within {timeout}");
    }
}

[CollectionDefinition("NATS")]
public class NatsCollection : ICollectionFixture<NatsFixture>;
