using System.Globalization;

namespace CodexTokenOverlay;

internal sealed record OverlayMetric(DisplayField Field, string CompactLabel, string ExpandedLabel, string Value, bool HasValue);
internal sealed record OverlayPresentation(
    OverlayMetric Primary, OverlayMetric Secondary, IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent, bool ShowContextProgress, string? StatusText,
    OverlayMetric? Total = null, string? ExtraText = null, string? ModelText = null,
    string? SourceText = null, string? FollowText = null, string? IssueText = null, string? EstimateText = null,
    bool ShowIdentifyAction = false, bool IsIdentifying = false);

internal static class OverlayPresentationBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static OverlayPresentation CreateWaiting(string statusText, DisplayField primaryField, DisplayField secondaryField, DisplayField visibleFields) =>
        new(WaitingMetric(primaryField), WaitingMetric(secondaryField), Rows(visibleFields, WaitingMetric), 0, false,
            Clean(statusText), WaitingMetric(DisplayField.Total), SourceText: "Codex 本地日志 · 未提供用量");

    public static OverlayPresentation Create(TokenSnapshot snapshot, DisplayField primaryField, DisplayField secondaryField, DisplayField visibleFields, SessionCostResult? cost = null)
    {
        var context = snapshot.ContextPercent;
        var rounds = snapshot.TurnCount.HasValue ? snapshot.TurnCount + " 轮" : "— 轮";
        var time = snapshot.UpdatedAtUtc == default ? "未提供时间" : snapshot.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var rows = Rows(visibleFields, field => Metric(snapshot, field, false, cost)).ToList();
        if (cost is not null && visibleFields.HasFlag(DisplayField.Cost))
        {
            foreach (var item in new[] { ("普通输入费用", cost.Amounts?.Input), ("缓存读取费用", cost.Amounts?.CacheRead),
                ("缓存写入费用", cost.Amounts?.CacheWrite), ("输出费用", cost.Amounts?.Output) })
                rows.Add(new(DisplayField.Cost, item.Item1, item.Item1, item.Item2.HasValue ? CostFormatting.Money(item.Item2.Value) : "— / 未计价", item.Item2.HasValue));
        }
        AddContextRows(rows, snapshot, visibleFields);
        return new(Metric(snapshot, primaryField, true, cost), Metric(snapshot, secondaryField, true, cost),
            rows, context ?? 0,
            (visibleFields & DisplayField.ContextPercent) != 0 && context.HasValue,
            snapshot.UsageRecorded || snapshot.Ledger?.Calls.Count > 0 ? null : snapshot.TurnInProgress ? "进行中 · 等待调用报告用量" : "等待当前会话用量", Metric(snapshot, DisplayField.Total, false),
            (snapshot.TurnInProgress ? "进行中 · " : "") + rounds + " · 上下文 " + Percent(context), "模型：" + Clean(snapshot.Model ?? "未提供"),
            "Codex 本地日志 · " + ShortThreadId(snapshot.ThreadId) + " · " + time
                + (snapshot.UsageSource is null ? "" : " · " + snapshot.UsageSource)
                + (snapshot.TurnInProgress ? " · 随调用报告更新" : ""),
            IssueText: snapshot.Issue,
            EstimateText: cost is null ? null : "按当前配置估算（USD） · 已计价 " + cost.PricedCalls + "/" + cost.RecordedCalls + " 次"
                + (cost.Status == SessionCostStatus.Partial ? " · 部分记录" : ""));
    }
    private static OverlayMetric[] Rows(DisplayField fields, Func<DisplayField, OverlayMetric> metric) =>
        DisplayFieldRules.Ordered.Where(f => f != DisplayField.Total && (fields & f) != 0).Select(metric).ToArray();
    private static OverlayMetric WaitingMetric(DisplayField field)
    { var labels = Labels(field); return new(field, labels.Compact, labels.Full, "— / 未提供", false); }
    private static OverlayMetric Metric(TokenSnapshot s, DisplayField field, bool compact, SessionCostResult? cost = null)
    {
        var labels = Labels(field);
        if (field == DisplayField.Speed)
            labels = (labels.Compact, s.SpeedGenerationOnly ? "输出速度（纯生成）" : "输出速度（本轮平均）");
        string Number(long? number, bool valid = true) => number.HasValue && (!TokenSnapshot.Valid(number) || !valid)
            ? "异常" : number.HasValue ? (compact ? FormatTokenCount(number) : number.Value.ToString("N0", Invariant)) + (compact ? "" : " tok")
            : compact ? "—" : "— / 未提供";
        var value = field switch
        {
            DisplayField.Total => s.TotalTokenInvalid || s.TotalTokens < 0 || (TokenSnapshot.Valid(s.InputTokens) && TokenSnapshot.Valid(s.OutputTokens) && s.EffectiveTotalTokens is null) ? "异常" : Number(s.EffectiveTotalTokens),
            DisplayField.Input => Number(s.InputTokens),
            DisplayField.Output => Number(s.OutputTokens),
            DisplayField.CacheHit => Number(s.CachedInputTokens, !s.InputTokens.HasValue || s.CacheIsValid),
            DisplayField.CacheMiss => s.CachedInputTokens > s.InputTokens || s.InputTokens < 0 || s.CachedInputTokens < 0 ? "异常" : Number(s.UncachedInputTokens),
            DisplayField.CacheHitRate => s.CachedInputTokens > s.InputTokens || s.InputTokens < 0 || s.CachedInputTokens < 0 ? "异常" : Rate(s.CacheHitPercent),
            DisplayField.Reasoning => Number(s.ReasoningOutputTokens, s.ReasoningIsValid),
            DisplayField.Context => ContextAmounts(s, compact),
            DisplayField.ContextPercent => Percent(s.ContextPercent),
            DisplayField.Thread => ShortThreadId(s.ThreadId),
            DisplayField.Cost => CostFormatting.Summary(cost, compact),
            DisplayField.Speed => s.OutputSpeed is { } speed && speed > 0
                ? speed.ToString("0.#", Invariant) + " tok/s" : "— / 未提供",
            _ => "—"
        };
        var hasValue = field switch
        {
            DisplayField.Cost => cost?.Amounts is not null,
            DisplayField.Speed => s.OutputSpeed is > 0,
            _ => !value.Contains('—') && value != "异常"
        };
        return new(field, labels.Compact, labels.Full, value, hasValue);
    }
    public static string Percent(double? percent) => percent.HasValue ? percent.Value.ToString("0.#", Invariant) + "%" : "—";
    // The cache rate keeps hundredths; the other percentages keep up to one decimal.
    public static string Rate(double? percent) => percent.HasValue ? percent.Value.ToString("0.00", Invariant) + "%" : "—";
    public static string FormatTokenCount(long? value) => value switch
    {
        null => "—",
        >= 1_000_000 => (value.Value / 1_000_000d).ToString("0.#", Invariant) + "M",
        // Rounding at one decimal can promote a sub-million count onto 1000K; the
        // promoted value moves to the next unit instead of printing “1000K”.
        >= 1_000 when Math.Round(value.Value / 1_000d, 1) >= 1_000d => (value.Value / 1_000_000d).ToString("0.#", Invariant) + "M",
        >= 1_000 => (value.Value / 1_000d).ToString("0.#", Invariant) + "K",
        _ => value.Value.ToString("N0", Invariant)
    };
    // The context block repeats no fact: with both fields on, the 最近调用 / 窗口 row already
    // carries used over window, so only the amount, directory, remaining, model and anomaly
    // rows are added; with only 上下文占用 on, the block supplies the amounts itself.
    private static void AddContextRows(List<OverlayMetric> rows, TokenSnapshot snapshot, DisplayField fields)
    {
        var showsContext = (fields & DisplayField.Context) != 0;
        var showsPercent = (fields & DisplayField.ContextPercent) != 0;
        if (!showsContext && !showsPercent) return;
        var insertAt = rows.FindLastIndex(row => row.Field is DisplayField.Context or DisplayField.ContextPercent) + 1;
        var block = new List<OverlayMetric>();
        if (showsPercent && !showsContext)
            block.Add(new(DisplayField.None, "已用 / 可用", "已用 / 可用窗口", ContextAmounts(snapshot, compact: false),
                TokenSnapshot.Valid(snapshot.ContextUsedTokens) && snapshot.ContextWindowTokens is > 0));
        block.Add(ConfiguredWindowMetric(snapshot));
        block.Add(new(DisplayField.None, "剩余可用", "剩余可用（估算）",
            snapshot.RemainingContextTokens is { } remaining ? remaining.ToString("N0", Invariant) + " tok" : "—",
            snapshot.RemainingContextTokens.HasValue));
        block.Add(new(DisplayField.None, "模型", "模型", Clean(snapshot.Model ?? "— / 未提供"),
            !string.IsNullOrWhiteSpace(snapshot.Model)));
        if (snapshot.Issue is { } issue) block.Add(new(DisplayField.None, "异常", "异常", issue, true));
        rows.InsertRange(insertAt, block);
    }
    private static OverlayMetric ConfiguredWindowMetric(TokenSnapshot snapshot)
    {
        // The directory answers only for an exact slug; a log window with no directory row
        // keeps the ceiling unknown here instead of inventing one.
        var suffix = snapshot.ConfiguredWindowSource is { } source
            ? "（" + source + (snapshot.ConfiguredWindowReference ? "参考" : "") + "）" : "";
        var label = "模型配置窗口" + suffix;
        return snapshot.ConfiguredWindowTokens is { } window
            ? new(DisplayField.None, label, label, window.ToString("N0", Invariant) + " tok", true)
            : new(DisplayField.None, label, label, "— / 未知上限", false);
    }
    // One unit closes the pair instead of repeating “tok” after each side.
    private static string ContextAmounts(TokenSnapshot snapshot, bool compact)
    {
        var used = ContextPart(snapshot.ContextUsedTokens, compact);
        var window = ContextPart(snapshot.ContextWindowTokens, compact);
        return window is "—" or "异常" ? used + " / " + window : used + " / " + window + " tok";
    }
    private static string ContextPart(long? value, bool compact) =>
        value is null ? "—" : !TokenSnapshot.Valid(value) ? "异常" : compact ? FormatTokenCount(value) : value.Value.ToString("N0", Invariant);
    public static string CompactText(OverlayMetric m) => m.Field == DisplayField.Total ? m.Value + " tok" :
        m.Field == DisplayField.Cost ? m.Value : m.CompactLabel + " " + m.Value;
    public static string ShortThreadId(string id, int maximumLength = 12)
    {
        id = Clean(id); if (id.Length <= maximumLength) return id;
        if (maximumLength <= 1) return maximumLength == 1 ? "…" : "";
        var left = Math.Min(4, maximumLength - 1); var right = Math.Min(6, maximumLength - left - 1);
        return id[..left] + "…" + id[^right..];
    }
    public static string GetFieldMenuText(DisplayField field) => Labels(field).Full;
    private static (string Compact, string Full) Labels(DisplayField f) => f switch
    {
        DisplayField.Total => ("用量", "Token 用量"), DisplayField.Input => ("输入", "输入"),
        DisplayField.Output => ("输出", "输出"), DisplayField.CacheHit => ("缓存读取", "缓存读取"),
        DisplayField.CacheHitRate => ("缓存命中", "缓存命中"), DisplayField.CacheMiss => ("未缓存", "未缓存输入"),
        DisplayField.Context => ("上下文", "最近调用 / 窗口"),
        DisplayField.ContextPercent => ("上下文", "上下文占用（最近调用估算）"),
        DisplayField.Reasoning => ("推理", "推理输出（已含在输出）"), DisplayField.Thread => ("会话", "会话"),
        DisplayField.Cost => ("费用", "会话费用估算"),
        DisplayField.Speed => ("速度", "输出速度"),
        _ => throw new ArgumentOutOfRangeException(nameof(f))
    };
    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());
}
internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create 操作需要 Snapshot。", nameof(request)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"不支持的展示探针操作：{probeCase.Operation}", nameof(request))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("展示探针需要字段。", parameterName);
        }

        return (DisplayField)value.Value;
    }
}

