namespace CodexTokenOverlay;

// Turns the relation catalog plus per-member ledgers into one immutable, versioned group
// snapshot. The main conversation keeps its own read path; this type never rewrites it.
internal sealed class SessionGroupMonitor
{
    internal const int MemberSliceBytes = 1024 * 1024;
    // shortcut: one 1 MB slice per log member per refresh; when a member must be covered in
    // fewer refreshes, add a scheduler that persists read positions instead of raising it.
    private readonly TokenLogMonitor _monitor;
    private readonly IRuntimeUsageSource? _runtime;
    private readonly Dictionary<string, SessionCostCalculator> _memberCosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelatedMemberUsage> _observedSideChats = new(StringComparer.OrdinalIgnoreCase);
    private string? _retainedRoot;
    private long _version;

    public SessionGroupMonitor(TokenLogMonitor monitor, IRuntimeUsageSource? runtime = null)
    {
        _monitor = monitor;
        _runtime = runtime;
    }

    public SessionGroupSnapshot Update(string? rootThreadId, long selectionRevision, PricingRevision prices,
        CancellationToken cancellationToken = default)
    {
        if (rootThreadId is null) return SessionGroupSnapshot.Waiting with { SelectionRevision = selectionRevision, PricingVersion = prices.Version };
        if (!string.Equals(_retainedRoot, rootThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _retainedRoot = rootThreadId;
            _observedSideChats.Clear();
        }
        var catalog = _monitor.RefreshRelations(cancellationToken);
        var runtime = _runtime?.Read(cancellationToken) ?? RuntimeUsageReadResult.Disabled;
        var members = SessionGroupMembership.Resolve(catalog, rootThreadId, runtime.Observations);
        // Only the stable selection keeps readers; everything else is released, so a group
        // larger than the reader cache never rotates its readers out mid-read.
        var readSet = SelectLogMembers(members);
        _monitor.ReleaseMembers(readSet);
        foreach (var departed in _memberCosts.Keys.Where(id => !members.Any(m => string.Equals(m.ThreadId, id, StringComparison.OrdinalIgnoreCase))).ToArray())
            _memberCosts.Remove(departed);
        var notes = new List<string>();
        if (!runtime.IsAvailable && runtime.Reason is { } reason) notes.Add("侧聊：" + reason);
        var rows = new List<RelatedMemberUsage>();
        var observedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overLimit = 0;
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.Kind == RelatedMemberKind.SideChat)
            {
                var row = SideChatRow(member, runtime);
                observedIds.Add(member.ThreadId);
                _observedSideChats[member.ThreadId] = row;
                rows.Add(row);
                continue;
            }
            if (member.Kind == RelatedMemberKind.Unconfirmed)
            {
                rows.Add(UnconfirmedRow(member));
                continue;
            }
            if (!readSet.Contains(member.ThreadId))
            {
                overLimit++;
                rows.Add(OverLimitRow(member));
                continue;
            }
            rows.Add(ReadMember(member, prices, cancellationToken));
        }
        if (overLimit > 0) notes.Add(overLimit + " 个成员超过读取上限，未计入已知合计");
        // A closed side chat keeps its last observed numbers in memory; the note states the scope.
        var retained = _observedSideChats.Where(pair => !observedIds.Contains(pair.Key)).ToArray();
        if (retained.Length > 0)
        {
            rows.AddRange(retained.Select(pair => pair.Value with { Issue = "已不在观察中 · 保留上次观察值" }));
            notes.Add(retained.Length + " 个侧聊已不在运行时观察中，保留上次观察值");
        }
        var unconfirmed = rows.Count(r => r.Kind == RelatedMemberKind.Unconfirmed);
        if (unconfirmed > 0) notes.Add(unconfirmed + " 个成员关系未确认，未计入已知合计");
        if (rows.Any(r => r.TokenCoverage == RelatedCoverage.Reading)) notes.Add("正在分片读取成员日志");
        return new(rootThreadId, selectionRevision, catalog.Version, prices.Version, ++_version, rows, notes);
    }

    // Root first, then member ids in a stable order: the selection must not change between
    // refreshes, or a group larger than the reader cache would restart readers mid-read.
    private static HashSet<string> SelectLogMembers(IReadOnlyList<RelatedMemberCandidate> members)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members
            .Where(m => m.Kind is RelatedMemberKind.Root or RelatedMemberKind.Subagent)
            .OrderBy(m => m.Kind == RelatedMemberKind.Root ? 0 : 1)
            .ThenBy(m => m.ThreadId, StringComparer.Ordinal)
            .Take(TokenLogMonitor.MemberReaderCapacity))
            selected.Add(member.ThreadId);
        return selected;
    }

    // Beyond the read limit the member still appears; a marker replaces the numbers it never
    // had a chance to read, and it never enters the known total.
    private static RelatedMemberUsage OverLimitRow(RelatedMemberCandidate member) =>
        new(member.ThreadId, member.ParentThreadId, member.Kind, Attribution(member), member.Relation?.AgentRole,
            RelatedCoverage.Partial, RelatedCoverage.Unavailable, null, null, null, null, null, 0, 0, null,
            "超过读取上限，未计入已知合计", "本地日志");

    private RelatedMemberUsage ReadMember(RelatedMemberCandidate member, PricingRevision prices, CancellationToken token)
    {
        var (snapshot, error) = _monitor.PollMember(member.ThreadId, MemberSliceBytes, token);
        var role = member.Relation?.AgentRole;
        if (snapshot is null)
            return new(member.ThreadId, member.ParentThreadId, member.Kind, Attribution(member), role,
                RelatedCoverage.Unavailable, RelatedCoverage.Unavailable, null, null, null, null, null, 0, 0, null,
                error ?? "成员日志尚未出现", "本地日志");
        SessionCostResult? cost = null;
        if (snapshot.Ledger is { } ledger)
        {
            if (!_memberCosts.TryGetValue(member.ThreadId, out var calculator))
                _memberCosts[member.ThreadId] = calculator = new SessionCostCalculator();
            cost = calculator.Calculate(ledger, prices, token);
        }
        var (total, measured, issue, counted) = MemberTokens(snapshot);
        var tokenCoverage = snapshot.MoreDataPending ? RelatedCoverage.Reading : measured;
        var costCoverage = snapshot.MoreDataPending ? RelatedCoverage.Reading : cost?.Status switch
        {
            SessionCostStatus.Complete => RelatedCoverage.Complete,
            SessionCostStatus.Partial => RelatedCoverage.Partial,
            _ => RelatedCoverage.Unavailable
        };
        return new(member.ThreadId, member.ParentThreadId, member.Kind, Attribution(member), role,
            tokenCoverage, costCoverage, total, snapshot.InputTokens, snapshot.CachedInputTokens,
            snapshot.OutputTokens, snapshot.ReasoningOutputTokens, cost?.RecordedCalls ?? 0, cost?.PricedCalls ?? 0,
            cost?.Amounts?.Total, tokenCoverage == RelatedCoverage.Partial ? issue ?? snapshot.Issue : null, "本地日志")
        { CountedInTotals = counted && !snapshot.MoreDataPending };
    }

    // A member contributes only what provably belongs to itself. The reported thread
    // aggregate is accepted when it equals the deduplicated call sum; otherwise the calls
    // are used and the member stays partial, so an inherited fork baseline is never added.
    private static (long? Total, RelatedCoverage Coverage, string? Issue, bool Counted) MemberTokens(TokenSnapshot snapshot)
    {
        var ledger = snapshot.Ledger;
        var calls = ledger?.Calls ?? [];
        var snapshotIssue = snapshot.Issue;
        if (calls.Count > 0)
        {
            long input = 0, output = 0;
            try
            {
                foreach (var call in calls)
                {
                    var usage = call.Usage;
                    if (usage.ValidationIssue is not null || usage.Input is null || usage.Output is null)
                        return (null, RelatedCoverage.Partial, "逐调用用量不完整", false);
                    input = checked(input + usage.Input.Value);
                    output = checked(output + usage.Output.Value);
                }
            }
            catch (OverflowException) { return (null, RelatedCoverage.Partial, "调用合计超出范围", false); }
            var callsTotal = input + output;
            var aggregate = ledger!.ExpectedThreadUsage;
            if (aggregate is null || Matches(callsTotal, input, output, aggregate))
                return snapshotIssue is null
                    ? (callsTotal, RelatedCoverage.Complete, null, true)
                    : (callsTotal, RelatedCoverage.Partial, snapshotIssue, true);
            return (callsTotal, RelatedCoverage.Partial, "线程累计与调用合计不一致，只计入自身调用", true);
        }
        var reported = snapshot.EffectiveTotalTokens;
        return reported is not null
            ? (reported, RelatedCoverage.Partial, snapshotIssue ?? "只有累计数字，缺少逐调用身份，未计入已知合计", false)
            : (null, RelatedCoverage.Unavailable, snapshotIssue ?? "尚无调用记录", false);
    }

    private static bool Matches(long callsTotal, long input, long output, UsageAmounts aggregate)
    {
        if (aggregate.ValidationIssue is not null) return false;
        if (aggregate.Total is { } total && total != callsTotal) return false;
        if (aggregate.Input is { } aggregateInput && aggregateInput != input) return false;
        if (aggregate.Output is { } aggregateOutput && aggregateOutput != output) return false;
        return true;
    }

    private static RelatedMemberUsage SideChatRow(RelatedMemberCandidate member, RuntimeUsageReadResult runtime)
    {
        var observation = runtime.Observations.FirstOrDefault(o =>
            string.Equals(o.ThreadId, member.ThreadId, StringComparison.OrdinalIgnoreCase));
        var counts = observation?.Counts;
        // A runtime cumulative has no call identity: tokens can be shown, itemized cost cannot.
        var coverage = counts is null ? RelatedCoverage.Unavailable : RelatedCoverage.Complete;
        return new(member.ThreadId, member.ParentThreadId, RelatedMemberKind.SideChat, "会话内侧聊", null,
            coverage, RelatedCoverage.Unavailable, counts?.EffectiveTotal, counts?.Input, counts?.CacheRead,
            counts?.Output, counts?.Reasoning, 0, 0, null, counts is null ? "未观察到用量更新" : null, "运行时观察")
        { CountedInTotals = coverage == RelatedCoverage.Complete };
    }

    private static RelatedMemberUsage UnconfirmedRow(RelatedMemberCandidate member) =>
        new(member.ThreadId, member.ParentThreadId, RelatedMemberKind.Unconfirmed, member.Reason ?? "关系未确认", null,
            RelatedCoverage.Unavailable, RelatedCoverage.Unavailable, null, null, null, null, null, 0, 0, null,
            member.Reason, "关系目录");

    private static string Attribution(RelatedMemberCandidate member) => member.Kind switch
    {
        RelatedMemberKind.Root => "当前会话",
        RelatedMemberKind.Subagent => "父 " + OverlayPresentationBuilder.ShortThreadId(member.ParentThreadId ?? "—", 10) +
            (member.Relation?.AgentPath is { } path ? " · " + path : ""),
        _ => member.Reason ?? "关系未确认"
    };
}
