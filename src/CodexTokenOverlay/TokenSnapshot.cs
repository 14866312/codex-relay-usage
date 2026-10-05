namespace CodexTokenOverlay;

// Values are the reported log values, never estimates of the relay's bill.
internal sealed record TokenSnapshot(
    string ThreadId, string LogPath,
    long? TotalTokens, long? InputTokens, long? CachedInputTokens,
    long? OutputTokens, long? ReasoningOutputTokens,
    long? ContextUsedTokens, long? ContextWindowTokens, DateTime UpdatedAtUtc,
    string? Model = null, int? TurnCount = null, string? ParseIssue = null,
    bool UsageRecorded = true, bool TotalTokenInvalid = false, UsageLedgerSnapshot? Ledger = null,
    bool TurnInProgress = false, string? UsageSource = null)
{
    public static bool Valid(long? value) => value is >= 0;
    public bool CacheIsValid => Valid(InputTokens) && Valid(CachedInputTokens) && CachedInputTokens <= InputTokens;
    public bool ReasoningIsValid => Valid(ReasoningOutputTokens) &&
        (!OutputTokens.HasValue || (Valid(OutputTokens) && ReasoningOutputTokens <= OutputTokens));
    public long? EffectiveTotalTokens => TotalTokenInvalid ? null : TotalTokens.HasValue
        ? (Valid(TotalTokens) ? TotalTokens : null)
        : Sum(InputTokens, OutputTokens);
    public long? UncachedInputTokens => CacheIsValid ? InputTokens - CachedInputTokens : null;
    public double? CacheHitPercent => CacheIsValid && InputTokens > 0
        ? CachedInputTokens * 100d / InputTokens : null;
    public double? ContextPercent => Valid(ContextUsedTokens) && ContextWindowTokens > 0
        ? ContextUsedTokens * 100d / ContextWindowTokens : null;
    public string? Issue
    {
        get
        {
            var issues = new List<string>();
            if (ParseIssue is not null) issues.Add(ParseIssue);
            if (new[] { TotalTokens, InputTokens, CachedInputTokens, OutputTokens, ReasoningOutputTokens, ContextUsedTokens, ContextWindowTokens }.Any(v => v < 0))
                issues.Add("记录含负数");
            if (CachedInputTokens > InputTokens) issues.Add("缓存读取大于输入");
            if (ReasoningOutputTokens > OutputTokens) issues.Add("推理输出大于输出");
            var sum = Sum(InputTokens, OutputTokens);
            if (Valid(InputTokens) && Valid(OutputTokens) && sum is null) issues.Add("输入与输出相加溢出");
            if (Valid(TotalTokens) && sum.HasValue && TotalTokens != sum) issues.Add("总量与输入加输出不一致");
            if (ContextPercent > 100) issues.Add("最近调用用量超过上下文窗口");
            if (ContextWindowTokens == 0) issues.Add("上下文窗口记录为零");
            return issues.Count == 0 ? null : string.Join("；", issues.Distinct());
        }
    }
    internal static long? Sum(long? a, long? b) => Valid(a) && Valid(b) && a <= long.MaxValue - b ? a + b : null;
}

internal static class FollowSelection
{
    // Fail closed: page ambiguity/disconnection must never select the newest log.
    public static string? Resolve(ActiveThreadRouteStatus route, string? manualId) =>
        !string.IsNullOrWhiteSpace(manualId) ? manualId :
        route.IsConnected && route.ActiveWindowCount == 1 ? route.ThreadId : null;
    public static string Status(ActiveThreadRouteStatus route, string? manualId) =>
        !string.IsNullOrWhiteSpace(manualId) ? "手动锁定" :
        route.ActiveWindowCount > 1 ? "多窗口：请手动选择会话" :
        route.IsConnected && route.ActiveWindowCount == 1 && !string.IsNullOrWhiteSpace(route.ThreadId) ? "自动跟随" :
        !route.IsConnected ? "IPC 未连接 · 可手动选择" :
        (route.LastError ?? (route.ActiveWindowCount == 0 ? "未找到可见 Codex 主窗口" : "无法识别当前对话")) + " · 可手动选择";
}
