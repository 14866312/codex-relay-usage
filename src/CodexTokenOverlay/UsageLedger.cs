using System.Text.Json;

namespace CodexTokenOverlay;

internal sealed record UsageAmounts(long? Input, long? CacheRead, long? CacheWrite, long? Output,
    long? Reasoning = null, long? Total = null, string? ParseIssue = null)
{
    public string? ValidationIssue
    {
        get
        {
            if (ParseIssue is not null) return ParseIssue;
            if (new[] { Input, CacheRead, CacheWrite, Output, Reasoning, Total }.Any(n => n < 0)) return "用量存在负数";
            var missing = new[] { ("总输入", Input), ("缓存读取", CacheRead), ("缓存写入", CacheWrite), ("输出", Output) }
                .Where(p => p.Item2 is null).Select(p => p.Item1).ToArray();
            if (missing.Length > 0) return "用量字段未提供：" + string.Join("、", missing);
            try
            {
                if (checked(CacheRead!.Value + CacheWrite!.Value) > Input) return "缓存读写之和超过总输入";
                var total = checked(Input!.Value + Output!.Value);
                if (Total.HasValue && Total != total) return "总用量与输入输出不一致";
                if (Reasoning > Output) return "推理用量超过输出";
            }
            catch (OverflowException) { return "用量超出范围"; }
            return null;
        }
    }
    public static UsageAmounts Parse(JsonElement value)
    {
        var issues = new List<string>();
        long? Read(string name)
        {
            if (!value.TryGetProperty(name, out var n) || n.ValueKind == JsonValueKind.Null) return null;
            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var number)) return number;
            issues.Add(name + " 非整数或超出范围"); return null;
        }
        var input = Read("input_tokens"); var read = Read("cached_input_tokens");
        var write = Read("cache_write_input_tokens"); var output = Read("output_tokens");
        var reasoning = Read("reasoning_output_tokens"); var total = Read("total_tokens");
        return new(input, read, write, output, reasoning, total, issues.Count == 0 ? null : string.Join("；", issues));
    }
}

internal readonly record struct UsageCallKey(string SessionId, string ResponseId);
internal sealed record UsageCall(UsageCallKey Key, string? TurnId, string? Model, UsageAmounts Usage,
    long Revision, string? Issue = null);
internal sealed record UsageLedgerSnapshot(string ThreadId, Guid LedgerId, long Version,
    IReadOnlyList<UsageCall> Calls, UsageAmounts? ExpectedThreadUsage, UsageAmounts LegacyFloor,
    bool HasLegacyUsage, IReadOnlyList<string> Issues);

// Owned by the incremental reader. Stores usage only, never response/conversation bodies.
internal sealed class SessionUsageLedger(string threadId)
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly Dictionary<UsageCallKey, UsageCall> _calls = new();
    private readonly Dictionary<string, HashSet<string>> _models = new(StringComparer.Ordinal);
    private readonly HashSet<string> _issues = new(StringComparer.Ordinal);
    private UsageAmounts? _expected;
    private UsageAmounts _floor = new(null, null, null, null);
    private bool _hasLegacy;
    private long _version, _invalidSequence;
    private UsageLedgerSnapshot? _snapshot;

    public void ObserveContext(JsonElement payload)
    {
        var turn = IncrementalSessionReader.String(payload, "turn_id");
        var model = IncrementalSessionReader.String(payload, "model");
        if (string.IsNullOrWhiteSpace(turn) || string.IsNullOrWhiteSpace(model)) return;
        if (!_models.TryGetValue(turn, out var models)) _models[turn] = models = new(StringComparer.Ordinal);
        if (!models.Add(model)) return;
        var resolved = models.Count == 1 ? model : null;
        foreach (var key in _calls.Keys.ToArray())
            if (_calls[key].TurnId == turn) _calls[key] = _calls[key] with { Model = resolved, Revision = ++_version };
        _version++;
    }
    public void ObserveLegacy(JsonElement total)
    {
        var u = UsageAmounts.Parse(total);
        if (!_hasLegacy && (u.Input > 0 || u.Output > 0 || u.Total > 0)) { _hasLegacy = true; _version++; }
        // Total-only context-window adjustments cannot prove billable input/output.
        if (u.ParseIssue is not null || u.Input < 0 || u.Output < 0 ||
            (u.Total.HasValue && u.Input.HasValue && u.Output.HasValue &&
             (decimal)u.Input.Value + u.Output.Value != u.Total.Value)) return;
        static long? Max(long? a, long? b) => b is >= 0 ? a.HasValue ? Math.Max(a.Value, b.Value) : b : a;
        var next = new UsageAmounts(Max(_floor.Input, u.Input), Max(_floor.CacheRead, u.CacheRead),
            Max(_floor.CacheWrite, u.CacheWrite), Max(_floor.Output, u.Output));
        if (next != _floor) { _floor = next; _version++; }
    }
    public void ObserveRecord(JsonElement payload)
    {
        var reportedThread = IncrementalSessionReader.String(payload, "thread_id");
        if (reportedThread is not null && !string.Equals(reportedThread, threadId, StringComparison.OrdinalIgnoreCase)) return;
        var session = IncrementalSessionReader.String(payload, "session_id");
        var response = IncrementalSessionReader.String(payload, "response_id");
        var turn = IncrementalSessionReader.String(payload, "turn_id");
        string? issue = null;
        if (string.IsNullOrWhiteSpace(reportedThread) || string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(response))
            issue = "请求身份字段未提供";
        var key = new UsageCallKey(session ?? "", response ?? ("invalid-record-" + ++_invalidSequence));
        var usage = payload.TryGetProperty("usage", out var raw) && raw.ValueKind == JsonValueKind.Object
            ? UsageAmounts.Parse(raw) : new(null, null, null, null, ParseIssue: "逐次用量未提供");
        if (_calls.TryGetValue(key, out var previous))
        {
            if (previous.Usage != usage || previous.TurnId != turn)
            {
                _calls[key] = previous with { Issue = "同一请求的记录冲突", Revision = ++_version };
                _issues.Add("同一请求的记录冲突");
            }
            return; // Replayed snapshots do not advance the aggregate or charge again.
        }
        var model = turn is not null && _models.TryGetValue(turn, out var models) && models.Count == 1 ? models.Single() : null;
        _calls.Add(key, new(key, turn, model, usage, ++_version, issue));
        _expected = payload.TryGetProperty("thread_token_usage", out var aggregate) && aggregate.ValueKind == JsonValueKind.Object
            ? UsageAmounts.Parse(aggregate) : null;
    }
    public UsageLedgerSnapshot Snapshot()
    {
        if (_snapshot?.Version == _version) return _snapshot;
        return _snapshot = new(threadId, _id, _version, _calls.Values.ToArray(), _expected, _floor, _hasLegacy, _issues.ToArray());
    }
}
