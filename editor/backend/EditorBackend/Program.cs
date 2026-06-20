using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
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

    // TODO: Bridge NATS messages to/from the WebSocket connection.
    var buffer = new byte[4096];
    while (ws.State == WebSocketState.Open)
    {
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
            break;
    }
});

app.Run();
