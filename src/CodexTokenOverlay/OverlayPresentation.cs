using System.Globalization;

namespace CodexTokenOverlay;

internal sealed record OverlayMetric(DisplayField Field, string CompactLabel, string ExpandedLabel, string Value, bool HasValue);
internal sealed record OverlayPresentation(
    OverlayMetric Primary, OverlayMetric Secondary, IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent, bool ShowContextProgress, string? StatusText,
    OverlayMetric? Total = null, string? ExtraText = null, string? ModelText = null,
    string? SourceText = null, string? FollowText = null, string? IssueText = null);

internal static class OverlayPresentationBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static OverlayPresentation CreateWaiting(string statusText, DisplayField primaryField, DisplayField secondaryField, DisplayField visibleFields) =>
        new(WaitingMetric(primaryField), WaitingMetric(secondaryField), Rows(visibleFields, WaitingMetric), 0, false,
            Clean(statusText), WaitingMetric(DisplayField.Total), SourceText: "Codex 本地日志 · 未提供用量");

    public static OverlayPresentation Create(TokenSnapshot snapshot, DisplayField primaryField, DisplayField secondaryField, DisplayField visibleFields)
    {
        var context = snapshot.ContextPercent;
        var rounds = snapshot.TurnCount.HasValue ? snapshot.TurnCount + " 轮" : "— 轮";
        var time = snapshot.UpdatedAtUtc == default ? "未提供时间" : snapshot.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        return new(Metric(snapshot, primaryField, true), Metric(snapshot, secondaryField, true),
            Rows(visibleFields, field => Metric(snapshot, field, false)), context ?? 0,
            (visibleFields & DisplayField.ContextPercent) != 0 && context.HasValue,
            snapshot.UsageRecorded ? null : "等待当前会话用量", Metric(snapshot, DisplayField.Total, false),
            rounds + " · 上下文 " + Percent(context), "模型：" + Clean(snapshot.Model ?? "未提供"),
            "Codex 本地日志 · " + ShortThreadId(snapshot.ThreadId) + " · " + time,
            IssueText: snapshot.Issue);
    }
    private static IReadOnlyList<OverlayMetric> Rows(DisplayField fields, Func<DisplayField, OverlayMetric> metric) =>
        DisplayFieldRules.Ordered.Where(f => f != DisplayField.Total && (fields & f) != 0).Select(metric).ToArray();
    private static OverlayMetric WaitingMetric(DisplayField field)
    { var labels = Labels(field); return new(field, labels.Compact, labels.Full, "— / 未提供", false); }
    private static OverlayMetric Metric(TokenSnapshot s, DisplayField field, bool compact)
    {
        var labels = Labels(field);
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
            DisplayField.CacheHitRate => s.CachedInputTokens > s.InputTokens || s.InputTokens < 0 || s.CachedInputTokens < 0 ? "异常" : Percent(s.CacheHitPercent),
            DisplayField.Reasoning => Number(s.ReasoningOutputTokens, s.ReasoningIsValid),
            DisplayField.Context => Number(s.ContextUsedTokens) + " / " + Number(s.ContextWindowTokens),
            DisplayField.ContextPercent => Percent(s.ContextPercent),
            DisplayField.Thread => ShortThreadId(s.ThreadId),
            _ => "—"
        };
        return new(field, labels.Compact, labels.Full, value, !value.Contains('—') && value != "异常");
    }
    public static string Percent(double? percent) => percent.HasValue ? percent.Value.ToString("0", Invariant) + "%" : "—";
    public static string FormatTokenCount(long? value) => value switch
    {
        null => "—",
        >= 1_000_000 => (value.Value / 1_000_000d).ToString("0.#", Invariant) + "M",
        >= 1_000 => (value.Value / 1_000d).ToString("0.#", Invariant) + "k",
        _ => value.Value.ToString("N0", Invariant)
    };
    public static string CompactText(OverlayMetric m) => m.Field == DisplayField.Total ? m.Value + " tok" : m.CompactLabel + " " + m.Value;
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
                    probeCase.Snapshot ?? throw new ArgumentException("Create 操作需要 Snapshot。", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"不支持的展示探针操作：{probeCase.Operation}", nameof(probeCase))
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

