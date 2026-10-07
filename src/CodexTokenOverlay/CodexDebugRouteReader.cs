using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

// Only attach to loopback listeners owned by the verified foreground Codex process.
// Read route metadata, never messages, request bodies, credentials or conversation text.
internal sealed class CodexDebugRouteReader : IDisposable
{
    internal const string RouteExpression = "(()=>{if(window.codexWindowType!=='electron')return null;const q=[window.__codexRoot?._internalRoot?.current];let n=0;while(q.length&&n++<256){const f=q.shift();if(!f)continue;const r=f.memoizedProps?.router;if(r?.state?.location)return {path:r.state.location.pathname,key:r.state.location.key};if(f.child)q.push(f.child);if(f.sibling)q.push(f.sibling);}return null;})()";
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private ClientWebSocket? _socket;
    private string? _owner;
    private string? _target;
    private long _nextDiscovery;
    private int _sequence;
    private bool _wasEnabled;

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
            GetWindowThreadProcessId(windows[0].Handle, out var pid);
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
        if (_owner != owner) { Reset(); _owner = owner; _nextDiscovery = 0; _wasEnabled = false; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(650);
        try
        {
            if (_socket is null)
            {
                if (Stopwatch.GetTimestamp() < _nextDiscovery)
                    return _wasEnabled ? Unavailable() : null;
                _nextDiscovery = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
                ConnectAsync(pid, timeout.Token).GetAwaiter().GetResult();
                if (_socket is null) return _wasEnabled ? Unavailable() : null;
                _wasEnabled = true;
            }
            var route = EvaluateAsync(timeout.Token).GetAwaiter().GetResult();
            if (route is null) return new(null, 1, true, 0, "当前页面路由暂不可用", owner + "/" + _target, "页面唯一 ID");
            var (path, key) = route.Value;
            return new(ThreadFromPath(path), 1, true, 0,
                ThreadFromPath(path) is null ? "当前页面不是本地对话" : null,
                owner + "/" + _target + "/" + key + "/" + path, "页面唯一 ID");
        }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            Disconnect();
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return _wasEnabled ? Unavailable() : null;
        }
    }

    private ActiveThreadRouteStatus Unavailable() => new(null, 1, false, 0,
        "自动连接已断开，正在重连", _owner, "页面唯一 ID");

    private async Task ConnectAsync(int pid, CancellationToken token)
    {
        foreach (var port in OwnedLoopbackPorts(pid))
        {
            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/json/list", HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var data = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(token), 128 * 1024, token);
                using var document = JsonDocument.Parse(data);
                var targets = document.RootElement.EnumerateArray().Where(t =>
                    t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString() == "app://-/index.html").ToArray();
                if (targets.Length != 1) continue;
                var endpoint = targets[0].GetProperty("webSocketDebuggerUrl").GetString();
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "ws"
                    || uri.Host != "127.0.0.1" || uri.Port != port) continue;
                var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                try { await socket.ConnectAsync(uri, token); }
                catch { socket.Dispose(); throw; }
                _socket = socket;
                _target = targets[0].GetProperty("id").GetString();
                return;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IOException) { }
        }
    }

    private async Task<(string Path, string Key)?> EvaluateAsync(CancellationToken token)
    {
        var id = ++_sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Runtime.evaluate", @params = new { expression = RouteExpression, returnByValue = true } });
        await _socket!.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
        var buffer = new byte[4096];
        for (var ignored = 0; ignored < 32; ignored++)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(buffer.AsMemory(), token);
                if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 32 * 1024)
                    throw new IOException("Invalid route response");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            if (!doc.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.TryGetProperty("exceptionDetails", out _)
                || !result.TryGetProperty("result", out var value) || !value.TryGetProperty("value", out var route)
                || route.ValueKind != JsonValueKind.Object) return null;
            if (!route.TryGetProperty("path", out var path) || !route.TryGetProperty("key", out var key)
                || path.ValueKind != JsonValueKind.String || key.ValueKind != JsonValueKind.String) return null;
            return (path.GetString()!, key.GetString()!);
        }
        throw new IOException("No matching route response");
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
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0); // IPv4 / OWNER_PID_LISTENER
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

    private void Disconnect() { _socket?.Abort(); _socket?.Dispose(); _socket = null; _target = null; }
    private void Reset() { Disconnect(); _owner = null; }
    public void Dispose() { Reset(); _http.Dispose(); }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
