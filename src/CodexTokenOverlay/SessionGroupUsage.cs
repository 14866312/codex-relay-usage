using System.Globalization;

namespace CodexTokenOverlay;

// Coverage is tracked separately for tokens and for money. A member with a verified
// cumulative total but no per-call identity still reports tokens and refuses cost.
internal enum RelatedCoverage { Complete, Partial, Reading, Unavailable }

internal sealed record RelatedMemberUsage(
    string ThreadId, string? ParentThreadId, RelatedMemberKind Kind, string? Attribution, string? Role,
    RelatedCoverage TokenCoverage, RelatedCoverage CostCoverage,
    long? TotalTokens, long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningOutputTokens,
    int Calls, int PricedCalls, decimal? Cost, string? Issue, string? Source)
{
    public bool TokenConfirmed => TokenCoverage == RelatedCoverage.Complete;
    public bool CostConfirmed => CostCoverage == RelatedCoverage.Complete;
    // Only a number derived from the member's own deduplicated calls is additive-safe;
    // an unverified cumulative stays visible but never enters the known total.
    public bool CountedInTotals { get; init; }
    public string KindText => Kind switch
    {
        RelatedMemberKind.Root => "主会话",
        RelatedMemberKind.Subagent => "子代理",
        RelatedMemberKind.SideChat => "侧聊",
        _ => "未确认"
    };
    public string CoverageText => TokenCoverage switch
    {
        RelatedCoverage.Complete => CostCoverage == RelatedCoverage.Complete ? "已确认" : "Token 已确认 · 费用 " + CostReason,
        RelatedCoverage.Reading => "读取中",
        RelatedCoverage.Partial => Issue ?? "数据不全",
        _ => Issue ?? CostReason
    };
    private string CostReason => CostCoverage switch
    {
        RelatedCoverage.Complete => "已确认",
        RelatedCoverage.Partial => "部分记录",
        RelatedCoverage.Reading => "读取中",
        _ => "不可用"
    };
}

internal sealed record RelatedUsageTotals(long? TotalTokens, decimal? Cost, int Members, int ConfirmedMembers)
{
    public bool IsComplete => Members > 0 && ConfirmedMembers == Members;
}

// Immutable group snapshot. Selection revision, relation version and pricing version
// travel with the numbers so a stale group can never be published into the current view.
internal sealed record SessionGroupSnapshot(
    string? RootThreadId, long SelectionRevision, long RelationVersion, long PricingVersion, long Version,
    IReadOnlyList<RelatedMemberUsage> Members, IReadOnlyList<string> Notes)
{
    public static readonly SessionGroupSnapshot Waiting = new(null, 0, 0, 0, 0, [], []);

    public RelatedMemberUsage? Root => Members.FirstOrDefault(m => m.Kind == RelatedMemberKind.Root);

    public RelatedUsageTotals Totals(params RelatedMemberKind[] kinds)
    {
        var selected = Members.Where(m => kinds.Contains(m.Kind)).ToArray();
        return Aggregate(selected);
    }

    // Known total: only numbers derived from a member's own confirmed calls contribute;
    // a partial member still counts when its own calls are the source of the number.
    public RelatedUsageTotals KnownTotals => Aggregate(Members.ToArray());

    private static RelatedUsageTotals Aggregate(RelatedMemberUsage[] selected)
    {
        long? tokens = null;
        decimal? cost = null;
        var confirmed = 0;
        try
        {
            foreach (var member in selected)
            {
                if (member.TotalTokens is { } value && member.CountedInTotals)
                {
                    tokens = (tokens ?? 0) + value;
                }
                if (member.TokenCoverage == RelatedCoverage.Complete) confirmed++;
                if (member.Cost is { } amount && member.CostCoverage == RelatedCoverage.Complete)
                    cost = (cost ?? 0m) + amount;
            }
        }
        catch (OverflowException) { tokens = null; cost = null; }
        return new(tokens, cost, selected.Length, confirmed);
    }

    public bool HasUnconfirmed => Members.Any(m => m.Kind == RelatedMemberKind.Unconfirmed);
    public bool IsReading => Members.Any(m => m.TokenCoverage == RelatedCoverage.Reading);
}

internal static class SessionGroupPublication
{
    // Mirrors CostPublication: a late group result never lands in a newer view.
    internal static bool Matches(SessionGroupSnapshot? snapshot, string? rootThreadId, long selectionRevision,
        long relationVersion, long pricingVersion) => snapshot is not null
        && snapshot.RootThreadId is not null
        && string.Equals(snapshot.RootThreadId, rootThreadId, StringComparison.OrdinalIgnoreCase)
        && snapshot.SelectionRevision == selectionRevision
        && snapshot.RelationVersion == relationVersion
        && snapshot.PricingVersion == pricingVersion;
}

internal static class RelatedUsageFormatting
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static string Tokens(long? value) => value is null ? "—" : value.Value.ToString("N0", Invariant);

    internal static string Summary(SessionGroupSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.RootThreadId is null) return "等待当前会话的关联用量";
        var root = snapshot.Root;
        var subagents = snapshot.Totals(RelatedMemberKind.Subagent);
        var sideChats = snapshot.Totals(RelatedMemberKind.SideChat);
        var known = snapshot.KnownTotals;
        var text = "主会话 " + Tokens(root?.TotalTokens) + " · 子代理 " + Tokens(subagents.TotalTokens) +
            " · 侧聊 " + Tokens(sideChats.TotalTokens) + " · 已知合计 " + Tokens(known.TotalTokens);
        if (!known.IsComplete) text += "（部分）";
        return text;
    }

    internal static string CostSummary(SessionGroupSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.RootThreadId is null) return "—";
        var known = snapshot.KnownTotals;
        if (known.Cost is not { } cost) return "— / 无法逐次计费";
        return CostFormatting.Money(cost) + (known.IsComplete ? "" : "（部分记录）");
    }
}
