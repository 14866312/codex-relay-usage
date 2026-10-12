using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

// Minimal loopback CDP page for synthetic transport checks: it advertises the app page
// plus one decoy target on /json/list and answers every Runtime.evaluate with the
// scripted Payload. A failing connection never stops the listener.
internal sealed class SyntheticCdpPage : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _runner;
    public int Port { get; }
    public volatile string? Payload;
    public volatile bool Delay;
    public volatile bool ForeignEndpoint;
    private int _disposed;

    public SyntheticCdpPage()
    {
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _runner = Task.Run(Run);
    }

    private static bool Recoverable(Exception error) => error is OperationCanceledException or SocketException
        or WebSocketException or ObjectDisposedException or IOException;

    private async Task Run()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                // One broken or disconnected client must not take the page down.
                try { await Serve(client); }
                catch (Exception error) when (Recoverable(error)) { }
            }
        }
        catch (Exception error) when (Recoverable(error)) { }
    }

    private async Task Serve(TcpClient client)
    {
        var stream = client.GetStream();
        var headers = new StringBuilder();
        var one = new byte[1];
        while (headers.Length < 16384 && !headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _cancel.Token) == 0) throw new IOException("Disconnected");
            headers.Append((char)one[0]);
        }
        if (headers.ToString().StartsWith("GET /json/list ", StringComparison.Ordinal))
        {
            var host = ForeignEndpoint ? "192.0.2.1" : "127.0.0.1";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] {
                new { id = "ignored-webview", type = "page", url = "https://example.invalid/", webSocketDebuggerUrl = $"ws://{host}:{Port}/wrong" },
                new { id = "main", type = "page", url = "app://-/index.html", webSocketDebuggerUrl = $"ws://{host}:{Port}/socket" } });
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _cancel.Token);
            await stream.WriteAsync(bytes, _cancel.Token);
            return;
        }
        var key = headers.ToString().Split("\r\n").First(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _cancel.Token);
        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), _cancel.Token);
            if (received.MessageType != WebSocketMessageType.Text) break;
            using var request = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            if (Delay) await Task.Delay(2000, _cancel.Token);
            var raw = Payload ?? "null";
            var id = request.RootElement.GetProperty("id").GetInt32();
            var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"object\",\"value\":{raw}}}}}}}");
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cancel.Token);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel(); _listener.Stop();
        try { _runner.Wait(500); } catch (AggregateException) { }
        _cancel.Dispose();
    }
}
