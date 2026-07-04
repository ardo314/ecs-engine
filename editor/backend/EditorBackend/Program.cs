using System.Net.WebSockets;
using EditorBackend;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// ── Entity management ──────────────────────────────────────────

app.MapPost("/api/entities", async () =>
{
    var spawnRequest = new EntitySpawnRequest
    {
        ComponentTypes = [],
        ComponentData = []
    };
    await nats.PublishAsync("engine.entity.spawn.request",
        MessagePackSerializer.Serialize(spawnRequest));
    return Results.Accepted();
});

app.MapDelete("/api/entities/{id:long}", async (long id) =>
{
    var destroyRequest = new EntityDestroyRequest
    {
        EntityIds = [(ulong)id]
    };
    await nats.PublishAsync("engine.entity.destroy.request",
        MessagePackSerializer.Serialize(destroyRequest));
    return Results.Accepted();
});

app.MapDelete("/api/entities/{id:long}/components/{componentType}", async (long id, string componentType) =>
{
    var removeRequest = new ComponentRemoveRequest
    {
        EntityId = (ulong)id,
        ComponentType = Uri.UnescapeDataString(componentType)
    };
    await nats.PublishAsync("engine.entity.component.remove",
        MessagePackSerializer.Serialize(removeRequest));
    return Results.Accepted();
});

// ── WebSocket ──────────────────────────────────────────────────

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

    var cached = wsManager.CachedSnapshot;
    if (cached is not null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(cached);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

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
