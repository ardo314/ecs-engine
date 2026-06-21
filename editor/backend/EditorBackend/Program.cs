using System.Net.WebSockets;
using EditorBackend;
using Engine.Core;
using NATS.Client.Core;

Serialization.Initialize();

var builder = WebApplication.CreateBuilder(args);

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();
Console.WriteLine($"[Editor] Connected to NATS at {natsUrl}");

builder.Services.AddSingleton(nats);
builder.Services.AddSingleton<WsBroadcaster>();
builder.Services.AddHostedService<NatsBridgeService>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var wsManager = context.RequestServices.GetRequiredService<WsBroadcaster>();
    var clientId = wsManager.AddClient(ws);

    // Send cached snapshot immediately on connect
    var cached = wsManager.CachedSnapshot;
    if (cached is not null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(cached);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // Keep connection alive, listen for incoming messages (future: editor commands)
    var buffer = new byte[4096];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }
    }
    finally
    {
        wsManager.RemoveClient(clientId);
    }
});

app.Run();
