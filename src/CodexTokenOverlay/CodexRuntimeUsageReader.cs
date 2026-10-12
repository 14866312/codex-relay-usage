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
    IReadOnlyList<RuntimeUsageObservation> Observations, string? TruncationReason = null)
{
    internal static readonly RuntimeUsageReadResult Disabled = new(false, "运行时用量入口未启用", null, []);
    internal static RuntimeUsageReadResult Unavailable(string reason) => new(false, reason, null, []);
    // Only a complete observation proves whether side chats exist; an unavailable entry or a
    // truncated projection cannot, so the known total has to stay partial.
    internal bool ObservationComplete => IsAvailable && TruncationReason is null;
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
    // shortcut: the projection stops at 64 results, a 256-entry store and a 20000-node walk;
    // when a required side chat falls outside those budgets, page the read instead of
    // raising them.
    internal const string UsageExpression = """
        (()=>{try{
        if(window.codexWindowType!=='electron')return null;
        const root=window.__codexRoot?._internalRoot?.current;
        if(!root)return null;
        const idPattern=/^[0-9a-fA-F-]{20,64}$/;
        const id=v=>typeof v==='string'&&idPattern.test(v)?v:null;
        const pathId=v=>{if(typeof v!=='string'||v.length>256)return null;const m=/\/local\/([0-9a-fA-F-]{20,64})$/.exec(v);return m?m[1]:null;};
        const keys=['inputTokens','input_tokens','cachedInputTokens','cached_input_tokens','cacheWriteInputTokens','cache_write_input_tokens','outputTokens','output_tokens','reasoningOutputTokens','reasoning_output_tokens','totalTokens','total_tokens'];
        const counts=o=>{if(!o||typeof o!=='object')return null;const r={};let any=false;
        for(const k of keys){const v=o[k];if(typeof v==='number'&&isFinite(v)&&v>=0){r[k]=v;any=true;}}return any?r:null;};
        const isState=v=>v!==null&&typeof v==='object'&&('latestTokenUsageInfo' in v||'sideConversation' in v||'resumeState' in v);
        // Side chats keep every result slot they need; normal sessions only fill what is
        // left. Any budget that could hide a side chat is reported as a truncated
        // observation instead of silently returning a smaller list.
        const side=[];const other=[];const seen=new Set();let observed=false;let reason=null;
        const add=(threadId,state)=>{
        if(!threadId||seen.has(threadId)||!isState(state))return;
        seen.add(threadId);observed=true;
        const entry={id:threadId,side:state.sideConversation===true,ephemeral:state.ephemeral===true,
        parent:id(state.parentThreadId)||pathId(state.sideConversationParentNavigationPath)||id(state.forkedFromId),
        parentPath:pathId(state.sideConversationParentNavigationPath)||null,
        counts:counts(state.latestTokenUsageInfo&&state.latestTokenUsageInfo.total)||counts(state.latestTokenUsageInfo)};
        if(entry.side){if(side.length>=64)reason=reason||'侧聊结果超过观察上限';else side.push(entry);}
        else if(other.length<64)other.push(entry);
        };
        const result=()=>({observations:side.concat(other).slice(0,64),truncated:reason!==null,reason:reason});
        // The conversation store is held by a hook value; only the whitelisted fields above
        // are read from it, never message bodies. Lightweight metadata copies elsewhere in
        // the tree are only a fallback, so the store with usage always wins.
        const q=[root];let n=0;const loose=[];
        while(q.length&&n++<20000){
        const f=q.shift();if(!f)continue;
        let h=f.memoizedState;
        for(let i=0;h&&i<64;i++){try{
        const v=h.memoizedState;
        for(const item of [v,v&&typeof v==='object'?v.current:null]){
        if(!item||typeof item!=='object')continue;
        const map=item.conversations;
        if(map&&typeof map.get==='function'&&typeof map.size==='number'){
        if(map.size<=256){for(const [key,value] of map)add(id(key),value);if(side.length||other.length)return result();}
        else{observed=true;reason=reason||('会话 store 超过观察上限（'+map.size+'）');}
        }else if(isState(item)){if(loose.length<64)loose.push(item);else reason=reason||'关系元数据超过观察上限';}
        }}catch(e){}h=h.next;}
        if(f.child)q.push(f.child);if(f.sibling)q.push(f.sibling);
        }
        for(const item of loose)add(id(item.id)||id(item.threadId),item);
        if(!reason&&q.length)reason='关系遍历超过观察上限';
        if(!observed&&reason===null)return null;
        return result();
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
            if (payload is not { ValueKind: JsonValueKind.Object } envelope ||
                !envelope.TryGetProperty("observations", out var items) || items.ValueKind != JsonValueKind.Array)
                return RuntimeUsageReadResult.Unavailable("页面未提供运行时用量字段");
            var truncation = envelope.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True &&
                envelope.TryGetProperty("reason", out var truncationReason) && truncationReason.ValueKind == JsonValueKind.String
                ? truncationReason.GetString() : null;
            var observations = new List<RuntimeUsageObservation>();
            foreach (var item in items.EnumerateArray())
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
            return observations.Count == 0 && truncation is null
                ? RuntimeUsageReadResult.Unavailable("页面未提供运行时用量字段")
                : new(true, null, null, observations, truncation);
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
