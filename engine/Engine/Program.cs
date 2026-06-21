using System.Collections.Concurrent;
using Engine.Coordinator;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

Serialization.Initialize();

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
var tickRate = int.TryParse(Environment.GetEnvironmentVariable("TICK_RATE"), out var tr) ? tr : 20;

Console.WriteLine("Engine coordinator starting...");

await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();

Console.WriteLine($"Connected to NATS at {natsUrl}");

var world = new WorldState();
var registry = new SystemRegistry();
var pendingSpawns = new ConcurrentQueue<EntitySpawnRequest>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Start NATS subscription handlers and wait until they're active
var handlers = new NatsHandlers(nats, registry, pendingSpawns);
_ = Task.Run(() => handlers.StartAsync(cts.Token), cts.Token);
await handlers.Ready;

// Run tick loop
var tickLoop = new TickLoop(nats, world, registry, pendingSpawns, tickRate);
await tickLoop.RunAsync(cts.Token);
