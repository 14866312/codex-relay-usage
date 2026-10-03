using System.Globalization;

namespace CodexTokenOverlay;

internal enum SessionCostStatus { Waiting, Unavailable, Partial, Complete }
internal sealed record CostAmounts(decimal Input, decimal CacheRead, decimal CacheWrite, decimal Output)
{
    public decimal Total => checked(Input + CacheRead + CacheWrite + Output);
    public static CostAmounts Zero => new(0, 0, 0, 0);
    public CostAmounts Add(CostAmounts other) => new(checked(Input + other.Input), checked(CacheRead + other.CacheRead),
        checked(CacheWrite + other.CacheWrite), checked(Output + other.Output));
}
internal sealed record EvaluatedCall(UsageCall Call, string? Profile, string Tier, CostAmounts? Amounts, string? Reason);
internal sealed record CostGroup(string Model, string Profile, string Tier, int Count, CostAmounts Amounts);
internal sealed record SessionCostResult(string ThreadId, Guid LedgerId, long LedgerVersion, long PricingVersion,
    SessionCostStatus Status, CostAmounts? Amounts, int RecordedCalls, int PricedCalls,
    IReadOnlyList<CostGroup> Groups, IReadOnlyList<EvaluatedCall> Unpriced, IReadOnlyList<string> Issues)
{
    public string Label => Status == SessionCostStatus.Complete ? "估算" : "已知";
    public string StateText => Status switch
    {
        SessionCostStatus.Complete => "完整估算", SessionCostStatus.Partial => "已知费用／部分记录",
        SessionCostStatus.Unavailable => "无法逐次计费", _ => "等待调用记录"
    };
    public string MissingText => Status switch
    {
        SessionCostStatus.Waiting => "— / 等待调用记录", SessionCostStatus.Unavailable => "— / 无法逐次计费",
        _ => Unpriced.Count > 0 && Unpriced.All(c => c.Reason == "模型未配置价格") ? "— / 未配置价格" : "— / 数据不全"
    };
}

// Called by the single background worker. Revalues only changed calls unless the price revision changes.
internal sealed class SessionCostCalculator
{
    private Guid _ledgerId;
    private long _priceVersion = -1, _ledgerVersion = -1;
    private readonly Dictionary<UsageCallKey, EvaluatedCall> _evaluated = new();
    private SessionCostResult? _result;
    internal int LastEvaluatedCallCount { get; private set; }
    public SessionCostResult Calculate(UsageLedgerSnapshot ledger, PricingRevision prices, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); LastEvaluatedCallCount = 0;
        if (_ledgerId != ledger.LedgerId || _priceVersion != prices.Version)
        { _evaluated.Clear(); _result = null; _ledgerVersion = -1; _ledgerId = ledger.LedgerId; _priceVersion = prices.Version; }
        if (_result is not null && _ledgerVersion == ledger.Version) return _result;
        var evaluations = new List<EvaluatedCall>();
        foreach (var call in ledger.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_evaluated.TryGetValue(call.Key, out var value) || value.Call != call)
            { value = Evaluate(call, prices.Settings); _evaluated[call.Key] = value; LastEvaluatedCallCount++; }
            evaluations.Add(value);
        }
        var issues = ledger.Issues.ToList();
        if (ledger.Calls.Count > 0 && !Covered(ledger)) issues.Add("逐次记录与线程累计量未核对一致，可能存在历史缺口");
        var unpriced = evaluations.Where(e => e.Amounts is null).ToArray();
        var priced = evaluations.Where(e => e.Amounts is not null).ToArray();
        CostAmounts? amounts = null; var groups = new List<CostGroup>();
        try
        {
            if (priced.Length > 0)
            {
                amounts = priced.Aggregate(CostAmounts.Zero, (sum, e) => sum.Add(e.Amounts!));
                _ = amounts.Total;
                foreach (var g in priced.GroupBy(e => (Model: e.Call.Model!, Profile: e.Profile!, e.Tier)))
                    groups.Add(new(g.Key.Model, g.Key.Profile, g.Key.Tier, g.Count(), g.Aggregate(CostAmounts.Zero, (sum, e) => sum.Add(e.Amounts!))));
            }
        }
        catch (OverflowException) { amounts = null; groups.Clear(); issues.Add("费用汇总超出范围"); }
        var status = ledger.Calls.Count == 0 ? ledger.HasLegacyUsage ? SessionCostStatus.Unavailable : SessionCostStatus.Waiting
            : issues.Count == 0 && unpriced.Length == 0 ? SessionCostStatus.Complete : SessionCostStatus.Partial;
        _ledgerVersion = ledger.Version;
        return _result = new(ledger.ThreadId, ledger.LedgerId, ledger.Version, prices.Version, status, amounts,
            ledger.Calls.Count, priced.Length, groups, unpriced, issues.Distinct().ToArray());
    }
    private static bool Covered(UsageLedgerSnapshot ledger)
    {
        var expected = ledger.ExpectedThreadUsage;
        if (expected is null || expected.ValidationIssue is not null || ledger.Calls.Any(c => c.Issue is not null || c.Usage.ValidationIssue is not null)) return false;
        try
        {
            var input = 0L; var read = 0L; var write = 0L; var output = 0L;
            long? reasoning = expected.Reasoning.HasValue && ledger.Calls.All(c => c.Usage.Reasoning.HasValue) ? 0L : null;
            foreach (var c in ledger.Calls)
            { input = checked(input + c.Usage.Input!.Value); read = checked(read + c.Usage.CacheRead!.Value);
                write = checked(write + c.Usage.CacheWrite!.Value); output = checked(output + c.Usage.Output!.Value);
                if (reasoning.HasValue) reasoning = checked(reasoning.Value + c.Usage.Reasoning!.Value); }
            return input == expected.Input && read == expected.CacheRead && write == expected.CacheWrite && output == expected.Output
                && (!reasoning.HasValue || reasoning == expected.Reasoning)
                && !(ledger.LegacyFloor.Input > input || ledger.LegacyFloor.CacheRead > read || ledger.LegacyFloor.CacheWrite > write || ledger.LegacyFloor.Output > output);
        }
        catch (OverflowException) { return false; }
    }
    private static EvaluatedCall Evaluate(UsageCall call, PricingSettings settings)
    {
        EvaluatedCall Fail(string reason, string? name = null, string tier = "—") => new(call, name, tier, null, reason);
        var issue = call.Issue ?? call.Usage.ValidationIssue;
        if (issue is not null) return Fail(issue);
        if (call.Model is null) return Fail("调用模型未知或不唯一");
        var profile = settings.ForModel(call.Model);
        if (profile is null) return Fail("模型未配置价格");
        var high = profile.TwoTiers && call.Usage.Input > profile.Threshold;
        var tier = !profile.TwoTiers ? "统一价格" : high ? "> " + profile.Threshold.ToString("N0", CultureInfo.InvariantCulture) : "≤ " + profile.Threshold.ToString("N0", CultureInfo.InvariantCulture);
        var rates = high ? profile.High : profile.Low;
        var u = call.Usage; var normal = u.Input!.Value - u.CacheRead!.Value - u.CacheWrite!.Value;
        var missing = new[] { ("普通输入", normal, rates.Input), ("缓存读取", u.CacheRead!.Value, rates.CacheRead),
            ("缓存写入", u.CacheWrite!.Value, rates.CacheWrite), ("输出", u.Output!.Value, rates.Output) }
            .Where(p => p.Item2 > 0 && p.Item3 is null).Select(p => p.Item1).ToArray();
        if (missing.Length > 0) return Fail("所用档位的分项单价未配置：" + string.Join("、", missing), profile.Name, tier);
        try
        {
            var factor = profile.Mode == PriceEntryMode.EffectivePrices ? 1m : profile.Multiplier;
            decimal Charge(long count, decimal? rate) => count == 0 || rate == 0m || factor == 0m ? 0m
                : checked(count / 1_000_000m * (rate ?? 0m) * factor);
            var amounts = new CostAmounts(Charge(normal, rates.Input), Charge(u.CacheRead.Value, rates.CacheRead),
                Charge(u.CacheWrite.Value, rates.CacheWrite), Charge(u.Output!.Value, rates.Output));
            _ = amounts.Total;
            return new(call, profile.Name, tier, amounts, null);
        }
        catch (OverflowException) { return Fail("单次费用超出范围", profile.Name, tier); }
    }
}
internal static class CostFormatting
{
    public static string Money(decimal value, bool compact = false)
    {
        var smallest = compact ? 0.0001m : 0.00000001m;
        if (value > 0 && value < smallest) return compact ? "<$0.0001" : "<$0.00000001";
        return "$" + value.ToString(compact ? "0.0000" : "0.00000000", CultureInfo.InvariantCulture);
    }
    public static string Summary(SessionCostResult? result, bool compact) => result?.Amounts is { } a
        ? result.Label + " " + Money(a.Total, compact) + (result.Status == SessionCostStatus.Partial && !compact ? "（部分记录）" : "")
        : result?.MissingText ?? "正在计算费用";
}
