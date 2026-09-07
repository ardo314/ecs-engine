using Engine.Coordinator;
using NATS.Client.Core;

// The URL may carry credentials as nats://user:token@host, so it is redacted before logging.
var natsUrl = Environment.GetEnvironmentVariable("NATS_URL");
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

var schemas = new SchemaRegistry();
var world = new WorldState(schemas);
var systems = new SystemRegistry();
var watches = new WatchManager();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Start NATS subscription handlers and wait until they're active
var handlers = new NatsHandlers(nats, schemas, systems, world, watches);
_ = Task.Run(() => handlers.StartAsync(cts.Token), cts.Token);
await handlers.Ready;

// Run tick loop
var tickLoop = new TickLoop(nats, schemas, systems, world, watches, handlers, tickRate);
await tickLoop.RunAsync(cts.Token);

static string Redact(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
        ? url.Replace($"{uri.UserInfo}@", "***@", StringComparison.Ordinal)
        : url;
