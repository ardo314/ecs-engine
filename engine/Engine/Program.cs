using NATS.Client.Core;

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

Console.WriteLine("Engine coordinator starting...");

await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();

Console.WriteLine($"Connected to NATS at {natsUrl}");
Console.WriteLine("Coordinator running. Press Ctrl+C to exit.");

await Task.Delay(Timeout.Infinite);
