using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexTokenOverlay;

// Numbers reported by the desktop runtime for a thread. They are an observation, never a
// per-call ledger: without a call identity they can support tokens but not itemized cost.
internal sealed record RuntimeUsageCounts(long? Input, long? CacheRead, long? CacheWrite, long? Output,
    long? Reasoning, long? Total)
{
    internal bool HasAny => new[] { Input, CacheRead, CacheWrite, Output, Reasoning, Total }.Any(v => v.HasValue);
    internal long? EffectiveTotal => Total ?? (Input.HasValue && Output.HasValue ? Input + Output : null);

    internal static RuntimeUsageCounts Parse(JsonElement element)
    {
        long? Read(params string[] names)
        {
            foreach (var name in names)
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var number) && number >= 0) return number;
            return null;
        }
        return new(Read("input_tokens", "inputTokens"), Read("cached_input_tokens", "cachedInputTokens"),
            Read("cache_write_input_tokens", "cacheWriteInputTokens"), Read("output_tokens", "outputTokens"),
            Read("reasoning_output_tokens", "reasoningOutputTokens"), Read("total_tokens", "totalTokens"));
    }
}

internal sealed record RuntimeUsageObservation(string ThreadId, string? ParentThreadId, bool IsSideConversation,
    string? ParentNavigationPath, RuntimeUsageCounts? Counts, string? Issue = null);

internal sealed record RuntimeUsageReadResult(bool IsAvailable, string? Reason, string? RouteThreadId,
    IReadOnlyList<RuntimeUsageObservation> Observations)
{
    internal static readonly RuntimeUsageReadResult Disabled = new(false, "运行时用量入口未启用", null, []);
    internal static RuntimeUsageReadResult Unavailable(string reason) => new(false, reason, null, []);
}

// Seam for the runtime projection: production reads the verified main page, tests script it.
internal interface IRuntimeUsageSource
{
    RuntimeUsageReadResult Read(CancellationToken cancellationToken);
}

// Reads relationship and usage fields from the already verified main page through the shared
// debug transport. The expression builds the whitelist result inside the page: only thread
// identifiers, the side-conversation flag and the numeric usage fields ever cross the wire.
// Conversation text, titles, prompts and request bodies are never read or returned.
internal sealed class CodexRuntimeUsageReader : IRuntimeUsageSource, IDisposable
{
    internal const string UsageExpression = """
        (()=>{try{
        if(window.codexWindowType!=='electron')return null;
        const root=window.__codexRoot?._internalRoot?.current;
        if(!root)return null;
        const idPattern=/^[0-9a-fA-F-]{20,64}$/;
        const id=v=>typeof v==='string'&&idPattern.test(v)?v:null;
        const path=v=>typeof v==='string'&&v.length<=128&&/^[A-Za-z0-9_-/:.]*$/.test(v)?v:null;
        const keys=['input_tokens','inputTokens','cached_input_tokens','cachedInputTokens','cache_write_input_tokens','cacheWriteInputTokens','output_tokens','outputTokens','reasoning_output_tokens','reasoningOutputTokens','total_tokens','totalTokens'];
        const counts=o=>{if(!o||typeof o!=='object')return null;const r={};let any=false;
        for(const k of keys){const v=o[k];if(typeof v==='number'&&isFinite(v)&&v>=0){r[k]=v;any=true;}}return any?r:null;};
        const out=[];const seen=new Set();const q=[root];let n=0;
        while(q.length&&n++<512){
        const f=q.shift();if(!f)continue;
        for(const bag of [f.memoizedProps,f.memoizedState]){
        if(!bag||typeof bag!=='object')continue;
        for(const item of (Array.isArray(bag)?bag:[bag])){
        if(!item||typeof item!=='object')continue;
        const threadId=id(item.id)||id(item.threadId);
        const usage=counts(item.latestTokenUsageInfo)||counts(item.tokenUsageInfo);
        const side=item.sideConversation===true;
        if(!usage&&!side)continue;
        if(!threadId||seen.has(threadId))continue;
        seen.add(threadId);
        out.push({id:threadId,parent:id(item.parentThreadId),side:side,parentPath:path(item.sideConversationParentNavigationPath),counts:usage});
        if(out.length>=64)return out;
        }}
        if(f.child)q.push(f.child);if(f.sibling)q.push(f.sibling);
        }
        return out.length?out:null;
        }catch(e){return null;}})()
        """;
    private const int UsageResponseBytes = 64 * 1024;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    private readonly CodexDebugConnection _connection = new();
    private RuntimeUsageReadResult? _cached;
    private long _nextRead;

    // The runtime projection is polled at most once per second; a cached observation is
    // returned in between so a fast UI timer never multiplies renderer reads.
    public RuntimeUsageReadResult Read(CancellationToken cancellationToken)
    {
        if (_cached is not null && Stopwatch.GetTimestamp() < _nextRead) return _cached;
        _nextRead = Stopwatch.GetTimestamp() + (long)(MinimumInterval.TotalSeconds * Stopwatch.Frequency);
        _cached = ReadCore(cancellationToken);
        return _cached;
    }

    private RuntimeUsageReadResult ReadCore(CancellationToken cancellationToken)
    {
        var windows = CodexWindowLocator.GetVisibleMainWindows();
        if (windows.Count != 1)
            return RuntimeUsageReadResult.Unavailable(windows.Count > 1 ? "仅支持一个 Codex 主窗口" : "未找到可见 Codex 主窗口");
        try
        {
            _ = GetWindowThreadProcessId(windows[0].Handle, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return ReadProcess((int)pid, $"{windows[0].Handle}/{pid}/{process.StartTime.ToUniversalTime().Ticks}", cancellationToken);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return RuntimeUsageReadResult.Unavailable("Codex 窗口正在重新连接");
        }
    }

    internal RuntimeUsageReadResult ReadProcess(int pid, string owner, CancellationToken cancellationToken)
    {
        _connection.BeginOwner(owner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(650);
        try
        {
            if (!_connection.TryEnsureConnected(pid, timeout.Token))
                return RuntimeUsageReadResult.Unavailable(_connection.WasEnabled ? "自动连接已断开，正在重连" : "未启用本地调试连接");
            var payload = _connection.Evaluate(UsageExpression, UsageResponseBytes, timeout.Token);
            if (payload is not { ValueKind: JsonValueKind.Array } array)
                return RuntimeUsageReadResult.Unavailable("页面未提供运行时用量字段");
            var observations = new List<RuntimeUsageObservation>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = IncrementalSessionReader.String(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var side = item.TryGetProperty("side", out var sideValue) && sideValue.ValueKind == JsonValueKind.True;
                var counts = item.TryGetProperty("counts", out var raw) && raw.ValueKind == JsonValueKind.Object
                    ? RuntimeUsageCounts.Parse(raw) : null;
                observations.Add(new(id, IncrementalSessionReader.String(item, "parent"), side,
                    IncrementalSessionReader.String(item, "parentPath"), counts?.HasAny == true ? counts : null));
            }
            return observations.Count == 0
                ? RuntimeUsageReadResult.Unavailable("页面未提供运行时用量字段")
                : new(true, null, null, observations);
        }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or IOException or JsonException
            or OperationCanceledException or InvalidOperationException)
        {
            _connection.Reset();
            cancellationToken.ThrowIfCancellationRequested();
            return RuntimeUsageReadResult.Unavailable("运行时用量读取失败");
        }
    }

    public void Dispose() => _connection.Dispose();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
}
