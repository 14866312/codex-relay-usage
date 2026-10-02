using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var sessionRoot = SessionPathResolver.Resolve(args);
        if (DiagnosticsRunner.TryRun(args, sessionRoot) || ProbeRunner.TryRun(args, sessionRoot))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\CodexRelayUsage",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var settingsPath = OverlaySettings.ResolveSettingsOverride(args);
        Application.Run(new OverlayContext(sessionRoot, settingsPath, !args.Contains("--sessions")));
        GC.KeepAlive(singleInstanceMutex);
    }
}

internal static class SessionPathResolver
{
    public static string Resolve(IReadOnlyList<string>? arguments = null)
    {
        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals("--sessions", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Normalize(arguments[index + 1]);
                }
            }
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(Normalize(codexHome), "sessions");
    }

    private static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}

internal sealed record ActiveThreadRouteStatus(
    string? ThreadId,
    int ActiveWindowCount,
    bool IsConnected,
    long Version,
    string? LastError);

internal sealed class CodexIpcActiveThreadMonitor : IDisposable
{
    private const int MaximumWireFrameBytes = 256 * 1024 * 1024;
    private const int MaximumJsonFrameBytes = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, ActiveConversation> _activeByWindow = new(StringComparer.Ordinal);
    private readonly Task _runner;
    private string? _activeThreadId;
    private string? _lastError;
    private bool _isConnected;
    private long _sequence;
    private long _version;

    public CodexIpcActiveThreadMonitor()
    {
        _runner = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public ActiveThreadRouteStatus GetStatus()
    {
        lock (_sync)
        {
            return new ActiveThreadRouteStatus(
                _activeThreadId,
                _activeByWindow.Count,
                _isConnected,
                _version,
                _lastError);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(350);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    "codex-ipc",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(2500, cancellationToken).ConfigureAwait(false);
                MarkConnected();
                retryDelay = TimeSpan.FromMilliseconds(350);
                await SendInitializeAsync(pipe, cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var prefix = new byte[sizeof(uint)];
                    if (!await ReadExactlyAsync(pipe, prefix, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                    if (frameLength == 0 || frameLength > MaximumWireFrameBytes)
                    {
                        throw new InvalidDataException($"Codex IPC 帧长度无效：{frameLength}");
                    }

                    if (frameLength > MaximumJsonFrameBytes)
                    {
                        await DrainExactlyAsync(pipe, frameLength, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var payload = new byte[(int)frameLength];
                    if (!await ReadExactlyAsync(pipe, payload, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                    try
                    {
                        ProcessFrame(payload);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                    {
                        // 单个未知或不完整消息不应终止整个 IPC 监听。
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or TimeoutException)
            {
                MarkDisconnected(exception.Message);
            }

            MarkDisconnected(null);
            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            retryDelay = TimeSpan.FromMilliseconds(Math.Min(5000, retryDelay.TotalMilliseconds * 2));
        }
    }

    private static async Task SendInitializeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var request = new
        {
            type = "request",
            requestId = Guid.NewGuid().ToString(),
            sourceClientId = "initializing-client",
            version = 0,
            method = "initialize",
            @params = new { clientType = "codex-token-overlay" }
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await pipe.WriteAsync(prefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ProcessFrame(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "broadcast"
            || !root.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var methodName = method.GetString();
        if (methodName == "client-status-changed")
        {
            ProcessClientStatusChanged(parameters);
            return;
        }

        if (methodName != "thread-stream-following-changed"
            || !parameters.TryGetProperty("conversationId", out var conversationIdElement)
            || !parameters.TryGetProperty("hostId", out var hostIdElement)
            || !parameters.TryGetProperty("following", out var followingElement)
            || conversationIdElement.ValueKind != JsonValueKind.String
            || hostIdElement.ValueKind != JsonValueKind.String
            || followingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        var conversationId = conversationIdElement.GetString();
        var hostId = hostIdElement.GetString();
        var sourceClientId = root.TryGetProperty("sourceClientId", out var sourceElement)
            && sourceElement.ValueKind == JsonValueKind.String
            ? sourceElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(conversationId)
            || string.IsNullOrWhiteSpace(hostId)
            || string.IsNullOrWhiteSpace(sourceClientId))
        {
            return;
        }

        var key = $"{sourceClientId}\u001f{hostId}";
        lock (_sync)
        {
            if (followingElement.GetBoolean())
            {
                _activeByWindow[key] = new ActiveConversation(conversationId, ++_sequence);
            }
            else if (_activeByWindow.TryGetValue(key, out var active)
                && active.ThreadId.Equals(conversationId, StringComparison.OrdinalIgnoreCase))
            {
                _activeByWindow.Remove(key);
            }
            RecomputeActiveThread();
        }
    }

    private void ProcessClientStatusChanged(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || statusElement.GetString() != "disconnected"
            || !parameters.TryGetProperty("clientId", out var clientIdElement)
            || clientIdElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var clientId = clientIdElement.GetString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var keyPrefix = $"{clientId}\u001f";
        lock (_sync)
        {
            foreach (var key in _activeByWindow.Keys
                .Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .ToArray())
            {
                _activeByWindow.Remove(key);
            }
            RecomputeActiveThread();
        }
    }

    private void MarkConnected()
    {
        lock (_sync)
        {
            _activeByWindow.Clear();
            _activeThreadId = null;
            _lastError = null;
            _isConnected = true;
            _version++;
        }
    }

    private void MarkDisconnected(string? error)
    {
        lock (_sync)
        {
            var changed = _isConnected || _activeByWindow.Count > 0 || _activeThreadId is not null;
            _isConnected = false;
            _activeByWindow.Clear();
            _activeThreadId = null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastError = error;
            }
            if (changed)
            {
                _version++;
            }
        }
    }

    private void RecomputeActiveThread()
    {
        var nextThreadId = _activeByWindow.Values
            .OrderByDescending(item => item.Sequence)
            .Select(item => item.ThreadId)
            .FirstOrDefault();
        if (!string.Equals(nextThreadId, _activeThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _activeThreadId = nextThreadId;
            _version++;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static async Task DrainExactlyAsync(Stream stream, uint bytesToDrain, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = (long)bytesToDrain;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Codex IPC 在完整帧到达前关闭。");
            }
            remaining -= read;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        try
        {
            _runner.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 退出时取消后台读取属于正常流程。
        }
        _cancellation.Dispose();
    }

    private int _disposed;
    private sealed record ActiveConversation(string ThreadId, long Sequence);
}

