using System.Collections.Concurrent;
using Engine.Coordinator;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

Serialization.Initialize();

// NATS_BROKER is injected by hosts that supply their own broker, such as Wandelbots NOVA,
// and may carry credentials as nats://user:token@host.
var natsUrl = Environment.GetEnvironmentVariable("NATS_URL");
if (string.IsNullOrWhiteSpace(natsUrl))
    natsUrl = Environment.GetEnvironmentVariable("NATS_BROKER");
if (string.IsNullOrWhiteSpace(natsUrl))
    natsUrl = "nats://localhost:4222";

var tickRate = int.TryParse(Environment.GetEnvironmentVariable("TICK_RATE"), out var tr) ? tr : 20;

Console.WriteLine("Engine coordinator starting...");

await using var health = await HealthEndpoint.TryStartAsync();
if (health is not null)
    Console.WriteLine($"Health endpoint listening on {string.Join(", ", health.Urls)}");

await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();

Console.WriteLine($"Connected to NATS at {Redact(natsUrl)}");

var world = new WorldState();
var registry = new SystemRegistry();
var watchManager = new WatchManager();
var pendingSpawns = new ConcurrentQueue<EntitySpawnRequest>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Start NATS subscription handlers and wait until they're active
var handlers = new NatsHandlers(nats, registry, world, watchManager, pendingSpawns);
_ = Task.Run(() => handlers.StartAsync(cts.Token), cts.Token);
await handlers.Ready;

// Run tick loop
var tickLoop = new TickLoop(nats, world, registry, watchManager, handlers, pendingSpawns, tickRate);
await tickLoop.RunAsync(cts.Token);

static string Redact(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
        ? url.Replace($"{uri.UserInfo}@", "***@", StringComparison.Ordinal)
        : url;
