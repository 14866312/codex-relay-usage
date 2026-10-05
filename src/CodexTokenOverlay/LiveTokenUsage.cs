namespace CodexTokenOverlay;

// A thread aggregate and a sum of calls are alternative views, never additive sources.
internal sealed class LiveTokenUsageAccumulator
{
    private UsageAmounts? _calls, _thread;
    public string? Issue { get; private set; }
    public bool HasThreadAggregate { get; private set; }
    public string? Source { get; private set; }

    public void Observe(UsageAmounts usage, UsageAmounts? aggregate, bool hasRequestIdentity)
    {
        if (hasRequestIdentity) _calls = _calls is null ? usage with { ParseIssue = TotalsIssue(usage) } : Add(_calls, usage);
        if (aggregate is not null)
        {
            var issue = TotalsIssue(aggregate);
            if (issue is not null)
            {
                Issue = "逐次线程累计异常：" + issue;
                return;
            }
            if (aggregate.Input.HasValue && aggregate.Output.HasValue)
            {
                // Ignore stale aggregates without rolling a consumed-token counter backwards.
                if (_thread is null || Covers(aggregate, _thread))
                {
                    _thread = aggregate; HasThreadAggregate = true; Issue = null;
                }
                return;
            }
        }
        if (_thread is not null && hasRequestIdentity) _thread = Add(_thread, usage);
    }

    public UsageAmounts? Select(UsageAmounts legacy, bool hasLegacy)
    {
        if (_thread is not null)
        {
            // A newer complete legacy snapshot can also advance the display. Context compaction
            // snapshots with smaller totals must not erase a reported lifetime aggregate.
            if (hasLegacy && TotalsIssue(legacy) is null && Covers(legacy, _thread)
                && (legacy.Input > _thread.Input || legacy.Output > _thread.Output))
            { Source = "累计快照"; return legacy; }
            Source = HasThreadAggregate ? "逐次线程累计" : "已记录调用合计"; return _thread;
        }
        if (_calls is not null && (!hasLegacy || (TotalsIssue(_calls) is null && Covers(_calls, legacy))))
        { Source = "已记录调用合计"; return _calls; }
        Source = hasLegacy ? "累计快照" : null; return null;
    }

    internal static bool Covers(UsageAmounts newer, UsageAmounts older) =>
        newer.Input is >= 0 && newer.Output is >= 0 && older.Input is >= 0 && older.Output is >= 0
        && newer.Input >= older.Input && newer.Output >= older.Output;

    private static UsageAmounts Add(UsageAmounts a, UsageAmounts b)
    {
        var issue = TotalsIssue(a) ?? TotalsIssue(b);
        var input = TokenSnapshot.Sum(a.Input, b.Input);
        var output = TokenSnapshot.Sum(a.Output, b.Output);
        if ((a.Input.HasValue && b.Input.HasValue && input is null)
            || (a.Output.HasValue && b.Output.HasValue && output is null)) issue ??= "用量相加溢出";
        return new(input, TokenSnapshot.Sum(a.CacheRead, b.CacheRead), TokenSnapshot.Sum(a.CacheWrite, b.CacheWrite),
            output, TokenSnapshot.Sum(a.Reasoning, b.Reasoning), TokenSnapshot.Sum(input, output), issue);
    }

    internal static string? TotalsIssue(UsageAmounts u)
    {
        if (u.ParseIssue is not null) return u.ParseIssue;
        if (new[] { u.Input, u.CacheRead, u.CacheWrite, u.Output, u.Reasoning, u.Total }.Any(n => n < 0)) return "记录含负数";
        if (u.CacheRead > u.Input || u.CacheWrite > u.Input
            || (u.CacheRead.HasValue && u.CacheWrite.HasValue && u.Input.HasValue
                && (decimal)u.CacheRead.Value + u.CacheWrite.Value > u.Input.Value)) return "缓存读写超过输入";
        if (u.Reasoning > u.Output) return "推理输出大于输出";
        var total = TokenSnapshot.Sum(u.Input, u.Output);
        if (u.Input.HasValue && u.Output.HasValue && total is null) return "输入与输出相加溢出";
        if (u.Total.HasValue && total.HasValue && u.Total != total) return "总量与输入加输出不一致";
        return null;
    }
}
