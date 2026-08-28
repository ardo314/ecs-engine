using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Engine.Core;

/// <summary>
/// Minimal HTTP responder that lets a headless container satisfy an orchestrator
/// health probe. Serves <c>GET /health</c> and <c>GET /app_icon.png</c>; anything
/// else returns 404. Deliberately dependency-free so console apps stay on the
/// dotnet/runtime base image rather than dotnet/aspnet.
/// </summary>
public sealed class HealthEndpoint : IDisposable
{
    private const int MaxRequestLineBytes = 4096;

    // 1x1 transparent PNG — NOVA requires an app_icon path to be servable.
    private const string IconBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly byte[] Icon = Convert.FromBase64String(IconBase64);
    private static readonly byte[] HealthBody = """{"status":"healthy"}"""u8.ToArray();
    private static readonly byte[] NotFoundBody = "not found"u8.ToArray();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public HealthEndpoint(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>The bound port; resolved from the OS when constructed with port 0.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Starts a listener when <c>HEALTH_PORT</c> is set to a valid port, otherwise
    /// returns null so local and compose runs stay unaffected.
    /// </summary>
    public static HealthEndpoint? StartFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("HEALTH_PORT");
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            return null;

        var endpoint = new HealthEndpoint(port);
        endpoint.Start();
        return endpoint;
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    private static async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, timeout.Token);
                await stream.WriteAsync(BuildResponse(target), timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch
            {
                // A probe that hangs up mid-request must never take down the listener.
            }
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestLineBytes];
        var count = 0;

        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) break;
            count += read;

            var newline = buffer.AsSpan(0, count).IndexOf((byte)'\n');
            if (newline < 0) continue;

            var parts = Encoding.ASCII.GetString(buffer, 0, newline).TrimEnd('\r').Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }

        return null;
    }

    private static byte[] BuildResponse(string? target) => StripQuery(target) switch
    {
        "/health" or "/healthz" => Http(200, "OK", "application/json", HealthBody),
        "/app_icon.png" => Http(200, "OK", "image/png", Icon),
        _ => Http(404, "Not Found", "text/plain", NotFoundBody)
    };

    private static string? StripQuery(string? target)
    {
        if (target is null) return null;
        var query = target.IndexOf('?');
        return query < 0 ? target : target[..query];
    }

    private static byte[] Http(int status, string reason, string contentType, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        var response = new byte[header.Length + body.Length];
        header.CopyTo(response, 0);
        body.CopyTo(response, header.Length);
        return response;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
