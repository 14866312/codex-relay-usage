using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class DebugRouteTests
{
    internal static void Run(Action<string, bool> check)
    {
        const string a = "11111111-1111-4111-8111-111111111111";
        const string b = "22222222-2222-4222-8222-222222222222";
        check("local MemoryRouter path identifies exact thread", CodexDebugRouteReader.ThreadFromPath("/local/" + a) == a);
        check("home clears exact thread", CodexDebugRouteReader.ThreadFromPath("/") is null);
        check("remote host route is not treated as local", CodexDebugRouteReader.ThreadFromPath("/remote/" + a) is null);
        check("trailing segments cannot alias local conversation", CodexDebugRouteReader.ThreadFromPath("/local/" + a + "/other") is null);
        check("malformed thread ID is rejected", CodexDebugRouteReader.ThreadFromPath("/local/not-a-thread") is null);
        check("legacy route accepts exact GUID", CodexDebugRouteReader.ThreadFromPath("/threads/" + b) == b);
        using var server = new RouteServer();
        using var reader = new CodexDebugRouteReader();
        server.Path = "/local/" + a;
        var first = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("owned loopback CDP transport reads current route", first?.ThreadId == a && first.IsConnected);
        check("route source does not depend on title or sidebar", first?.Identification == "页面唯一 ID");
        server.Path = "/local/" + b;
        server.Key = "b";
        var second = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("same-title conversation switches by unique ID", second?.ThreadId == b);
        check("switch changes publication generation", first is not null && second is not null && CodexVisibleThreadMonitor.Advance(first, second).Version > first.Version);
        server.Key = "new-visit";
        var revisit = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("new navigation to same ID changes route key", revisit?.ThreadId == b && revisit.ViewKey != second?.ViewKey);
        server.Path = "/";
        check("transport home does not retain previous ID", reader.ReadProcess(Environment.ProcessId, "test-owner", default)?.ThreadId is null);
        server.Path = "/local/" + a;
        server.MissingRouter = true;
        var missing = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("missing router fails closed", missing?.ThreadId is null && missing?.LastError is not null);
        server.MissingRouter = false;
        check("router recovery restores actual route", reader.ReadProcess(Environment.ProcessId, "test-owner", default)?.ThreadId == a);
        server.Delay = true;
        var timer = Stopwatch.StartNew();
        var slow = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("hung renderer clears thread within bounded timeout", slow?.ThreadId is null && timer.ElapsedMilliseconds < 1500);
        using var wrongOwner = new CodexDebugRouteReader();
        check("other process cannot borrow listener", wrongOwner.ReadProcess(int.MaxValue, "wrong-owner", default) is null);
        check("native listener enumeration is process scoped", CodexDebugRouteReader.OwnedLoopbackPorts(Environment.ProcessId).Contains(server.Port)
            && !CodexDebugRouteReader.OwnedLoopbackPorts(int.MaxValue).Contains(server.Port));
        using var unsafeServer = new RouteServer { ForeignEndpoint = true };
        using var unsafeReader = new CodexDebugRouteReader();
        // Retire the first endpoint so the remaining candidate is deliberately unsafe.
        server.Dispose();
        check("foreign websocket host is rejected", unsafeReader.ReadProcess(Environment.ProcessId, "unsafe", default) is null);
    }

    private sealed class RouteServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _runner;
        public int Port { get; }
        public volatile string Path = "/";
        public volatile string Key = "a";
        public volatile bool MissingRouter;
        public volatile bool Delay;
        public volatile bool ForeignEndpoint;
        private int _disposed;
        public RouteServer()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _runner = Task.Run(Run);
        }
        private async Task Run()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
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
                        continue;
                    }
                    var websocketKey = headers.ToString().Split("\r\n").First(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(websocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _cancel.Token);
                    using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                    var buffer = new byte[8192];
                    while (socket.State == WebSocketState.Open)
                    {
                        var received = await socket.ReceiveAsync(buffer.AsMemory(), _cancel.Token);
                        if (received.MessageType != WebSocketMessageType.Text) break;
                        using var request = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                        if (Delay) await Task.Delay(2000, _cancel.Token);
                        object? route = MissingRouter ? null : new { path = Path, key = Key };
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                            id = request.RootElement.GetProperty("id").GetInt32(),
                            result = new { result = new { type = "object", value = route } } });
                        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cancel.Token);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or WebSocketException or ObjectDisposedException or IOException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancel.Cancel(); _listener.Stop();
            try { _runner.Wait(500); } catch (AggregateException) { }
            _cancel.Dispose();
        }
    }
}
