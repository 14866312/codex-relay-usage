using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexTokenOverlay;

// Shared transport for the verified main page: loopback discovery, one websocket to the
// unique app://-/index.html target, bounded request/response handling. Callers decide which
// whitelisted fields they evaluate; this type never reads messages, bodies or credentials.
internal sealed class CodexDebugConnection : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private ClientWebSocket? _socket;
    private string? _owner;
    private string? _target;
    private long _nextDiscovery;
    private int _sequence;
    private bool _wasEnabled;

    internal bool WasEnabled => _wasEnabled;
    internal string? Target => _target;
    internal string? Owner => _owner;

    // A new window/process lifetime owns the socket; reuse only while the owner is unchanged.
    internal void BeginOwner(string owner)
    {
        if (string.Equals(_owner, owner, StringComparison.Ordinal)) return;
        Reset(); _owner = owner; _nextDiscovery = 0; _wasEnabled = false;
    }

    // Returns false while discovery is throttled or no owned listener exists.
    internal bool TryEnsureConnected(int pid, CancellationToken token)
    {
        if (_socket is not null) return true;
        if (Stopwatch.GetTimestamp() < _nextDiscovery) return false;
        _nextDiscovery = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
        Connect(pid, token);
        if (_socket is null) return false;
        _wasEnabled = true;
        return true;
    }

    // Evaluates one expression and returns its by-value result, bounded in both message
    // count and size. Never returns a live object graph from the page.
    internal JsonElement? Evaluate(string expression, int maximumBytes, CancellationToken token) =>
        EvaluateAsync(expression, maximumBytes, token).GetAwaiter().GetResult();

    private async Task<JsonElement?> EvaluateAsync(string expression, int maximumBytes, CancellationToken token)
    {
        var id = ++_sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true } });
        await _socket!.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
        var buffer = new byte[4096];
        for (var ignored = 0; ignored < 32; ignored++)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(buffer.AsMemory(), token);
                if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > maximumBytes)
                    throw new IOException("Invalid debug response");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            if (!doc.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.TryGetProperty("exceptionDetails", out _)
                || !result.TryGetProperty("result", out var value) || !value.TryGetProperty("value", out var payload)) return null;
            return payload.Clone();
        }
        throw new IOException("No matching debug response");
    }

    private void Connect(int pid, CancellationToken token)
    {
        foreach (var port in OwnedLoopbackPorts(pid))
        {
            try
            {
                using var response = _http.GetAsync($"http://127.0.0.1:{port}/json/list", HttpCompletionOption.ResponseHeadersRead, token)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var data = ReadBoundedAsync(response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult(), 128 * 1024, token)
                    .GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(data);
                var targets = document.RootElement.EnumerateArray().Where(t =>
                    t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString() == "app://-/index.html").ToArray();
                if (targets.Length != 1) continue;
                var endpoint = targets[0].GetProperty("webSocketDebuggerUrl").GetString();
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "ws"
                    || uri.Host != "127.0.0.1" || uri.Port != port) continue;
                var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                try { socket.ConnectAsync(uri, token).GetAwaiter().GetResult(); }
                catch { socket.Dispose(); throw; }
                _socket = socket;
                _target = targets[0].GetProperty("id").GetString();
                return;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IOException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > maximum) throw new IOException("Response too large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static IReadOnlyList<int> OwnedLoopbackPorts(int pid)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0); // IPv4 / OWNER_PID_LISTENER
        if (size <= 0 || size > 8 * 1024 * 1024) return [];
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 3, 0) != 0) return [];
            var rows = Marshal.ReadInt32(memory);
            var ports = new List<int>();
            for (var i = 0; i < rows && 4 + (i + 1) * 24 <= size; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * 24);
                if (Marshal.ReadInt32(row, 20) != pid || unchecked((uint)Marshal.ReadInt32(row, 4)) != 0x0100007F) continue;
                var port = (ushort)Marshal.ReadInt32(row, 8);
                ports.Add(BinaryPrimitives.ReverseEndianness(port));
            }
            return ports.Distinct().ToArray();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal void Disconnect() { _socket?.Abort(); _socket?.Dispose(); _socket = null; _target = null; }
    internal void Reset() { Disconnect(); _owner = null; }
    public void Dispose() { Reset(); _http.Dispose(); }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

// Only attach to loopback listeners owned by the verified foreground Codex process.
// Read route metadata, never messages, request bodies, credentials or conversation text.
internal sealed class CodexDebugRouteReader : IDisposable
{
    internal const string RouteExpression = "(()=>{if(window.codexWindowType!=='electron')return null;const q=[window.__codexRoot?._internalRoot?.current];let n=0;while(q.length&&n++<256){const f=q.shift();if(!f)continue;const r=f.memoizedProps?.router;if(r?.state?.location)return {path:r.state.location.pathname,key:r.state.location.key};if(f.child)q.push(f.child);if(f.sibling)q.push(f.sibling);}return null;})()";
    private const int RouteResponseBytes = 32 * 1024;
    private readonly CodexDebugConnection _connection = new();

    internal static string? ThreadFromPath(string? path)
    {
        if (path is null) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] is "local" or "thread" or "threads"
            && Guid.TryParseExact(parts[1], "D", out var id) ? id.ToString() : null;
    }

    public ActiveThreadRouteStatus? Read(CancellationToken cancellationToken)
    {
        var windows = CodexWindowLocator.GetVisibleMainWindows();
        if (windows.Count != 1)
        {
            Reset();
            return new(null, windows.Count, false, 0, windows.Count > 1 ? "仅支持一个 Codex 主窗口" : null);
        }
        try
        {
            _ = GetWindowThreadProcessId(windows[0].Handle, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return ReadProcess((int)pid, $"{windows[0].Handle}/{pid}/{process.StartTime.ToUniversalTime().Ticks}", cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Reset();
            return new(null, 0, false, 0, "Codex 窗口正在重新连接");
        }
    }

    internal ActiveThreadRouteStatus? ReadProcess(int pid, string owner, CancellationToken cancellationToken)
    {
        _connection.BeginOwner(owner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(650);
        try
        {
            if (!_connection.TryEnsureConnected(pid, timeout.Token))
                return _connection.WasEnabled ? Unavailable() : null;
            var route = _connection.Evaluate(RouteExpression, RouteResponseBytes, timeout.Token);
            if (route is not { ValueKind: JsonValueKind.Object } value ||
                !value.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String)
                return new(null, 1, true, 0, "当前页面路由暂不可用", owner + "/" + _connection.Target, "页面唯一 ID");
            var path = pathValue.GetString()!;
            var key = keyValue.GetString()!;
            return new(ThreadFromPath(path), 1, true, 0,
                ThreadFromPath(path) is null ? "当前页面不是本地对话" : null,
                owner + "/" + _connection.Target + "/" + key + "/" + path, "页面唯一 ID");
        }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            Reset();
            cancellationToken.ThrowIfCancellationRequested();
            return _connection.WasEnabled ? Unavailable() : null;
        }
    }

    private ActiveThreadRouteStatus Unavailable() => new(null, 1, false, 0,
        "自动连接已断开，正在重连", _connection.Owner, "页面唯一 ID");

    private void Reset() => _connection.Reset();
    public void Dispose() => _connection.Dispose();
    internal static IReadOnlyList<int> OwnedLoopbackPorts(int pid) => CodexDebugConnection.OwnedLoopbackPorts(pid);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
}
