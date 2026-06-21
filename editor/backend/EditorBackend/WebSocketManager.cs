using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace EditorBackend;

/// <summary>
/// Tracks connected WebSocket clients and broadcasts JSON messages to all of them.
/// </summary>
public class WsBroadcaster
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private volatile string? _cachedSnapshot;

    public string? CachedSnapshot => _cachedSnapshot;

    public void UpdateCache(string json)
    {
        _cachedSnapshot = json;
    }

    public Guid AddClient(WebSocket ws)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;
        return id;
    }

    public void RemoveClient(Guid id)
    {
        _clients.TryRemove(id, out _);
    }

    public async Task BroadcastAsync(string json, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var toRemove = new List<Guid>();

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                toRemove.Add(id);
                continue;
            }

            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                toRemove.Add(id);
            }
        }

        foreach (var id in toRemove)
        {
            _clients.TryRemove(id, out _);
        }
    }
}
