using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

// Synthetic checks for related (subagent and side-chat) usage. Every input is generated
// here; no personal session log or live Codex window takes part.
internal static class RelatedUsageTests
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly PricingRevision Prices = new(1, new(1, new[] { SessionCostTests.Profile() }));
    private const string RootId = "aaaaaaaa-1111-4111-8111-111111111111";
    private const string SubId = "bbbbbbbb-2222-4222-8222-222222222222";
    private const string SideId = "cccccccc-3333-4333-8333-333333333333";

    internal static void Run(Action<string, bool> check, string directory)
    {
        ClassificationTests(check, Path.Combine(directory, "related-basic"));
        GeminiTests(check, Path.Combine(directory, "related-gemini"));
        RelationEdgeTests(check, Path.Combine(directory, "related-edges"));
        InheritanceTests(check, Path.Combine(directory, "related-fork"));
        DedupTests(check, Path.Combine(directory, "related-dedup"));
        SideChatTests(check, Path.Combine(directory, "related-side"));
        PublicationTests(check);
        ScaleTests(check, Path.Combine(directory, "related-scale"));
        ReadLimitTests(check, Path.Combine(directory, "related-limit"));
        ExpressionTests(check);
        TransportTests(check);
    }

    private static void ClassificationTests(Action<string, bool> check, string root)
    {
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 80, 0, 20));
        Write(root, SubId, Meta(SubId, RootId, agentPath: "/root/worker", agentRole: "data-explorer") +
            Context(SubId) + Usage(SubId, "s1", 30, 0, 10));
        var runtime = new ScriptedRuntime(Observed(SideId, RootId, 20, 0, 10));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor, runtime).Update(RootId, 1, Prices, default);
        check("group lists main, subagent and side chat", group.Members.Count == 3 &&
            group.Root!.KindText == "主会话" && group.Members.Count(m => m.KindText == "子代理") == 1 &&
            group.Members.Count(m => m.KindText == "侧聊") == 1);
        check("member totals are traceable to each thread", group.Root!.TotalTokens == 100 &&
            group.Members.Single(m => m.ThreadId == SubId).TotalTokens == 40 &&
            group.Members.Single(m => m.ThreadId == SideId).TotalTokens == 30);
        check("known total sums the three confirmed members", group.KnownTotals.TotalTokens == 170 && group.KnownTotals.IsComplete);
        check("subagent keeps its explicit parent and role", group.Members.Single(m => m.ThreadId == SubId) is
            { ParentThreadId: RootId, Role: "data-explorer" });
        check("cost is priced per member from its own calls", group.Members.Single(m => m.ThreadId == SubId).Cost is not null &&
            group.Members.Single(m => m.ThreadId == SideId).Cost is null &&
            group.Members.Single(m => m.ThreadId == SideId).CostCoverage == RelatedCoverage.Unavailable);
    }

    // A Gemini member reports thinking tokens outside output; its aggregate still matches its
    // own calls, so the member stays confirmed instead of being marked partial.
    private static void GeminiTests(Action<string, bool> check, string root)
    {
        var member = "55555555-eeee-4eee-8eee-555555555555";
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 80, 0, 20));
        Write(root, member, Meta(member, RootId) + Context(member, "gem/gemini-3.8-flash-high") +
            Usage(member, "g1", 100, 0, 5, reasoning: 30));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor).Update(RootId, 1, Prices, default);
        check("gemini member with reasoning above output stays confirmed",
            group.Members.Single(m => m.ThreadId == member) is { TotalTokens: 105, TokenCoverage: RelatedCoverage.Complete, Issue: null } &&
            group.KnownTotals.TotalTokens == 205);
    }

    private static void RelationEdgeTests(Action<string, bool> check, string root)
    {
        var middle = "dddddddd-4444-4444-8444-444444444444";
        var deep = "eeeeeeee-5555-4555-8555-555555555555";
        var conflict = "ffffffff-6666-4666-8666-666666666666";
        var unrelated = "99999999-7777-4777-8777-777777777777";
        var fork = "88888888-8888-4888-8888-888888888888";
        var created = "77777777-9999-4999-8999-999999999999";
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 10, 0, 5));
        Write(root, middle, Meta(middle, RootId) + Context(middle) + Usage(middle, "m1", 10, 0, 5));
        Write(root, deep, Meta(deep, middle) + Context(deep) + Usage(deep, "d1", 10, 0, 5));
        Write(root, conflict, Meta(conflict, RootId, declaredParent: unrelated) + Context(conflict) + Usage(conflict, "c1", 10, 0, 5));
        Write(root, unrelated, Meta(unrelated, "12121212-1212-4212-8212-121212121212") + Context(unrelated) + Usage(unrelated, "u1", 10, 0, 5));
        Write(root, fork, Meta(fork, subagent: false, declaredParent: RootId) + Context(fork) + Usage(fork, "f1", 10, 0, 5));
        Write(root, created, Meta(created, subagent: false, threadSource: "agent_created_thread") + Context(created) + Usage(created, "a1", 10, 0, 5));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor).Update(RootId, 1, Prices, default);
        check("multi-level subagents join once each", group.Members.Count(m => m.Kind == RelatedMemberKind.Subagent) == 2 &&
            group.Members.Single(m => m.ThreadId == deep).ParentThreadId == middle);
        check("conflicting parent relation stays unconfirmed", group.Members.Single(m => m.ThreadId == conflict) is
            { Kind: RelatedMemberKind.Unconfirmed, Issue: "父关系冲突", TotalTokens: null });
        check("foreign subagent tree never joins the group", group.Members.All(m => m.ThreadId != unrelated));
        check("plain fork and agent-created thread never join", group.Members.All(m => m.ThreadId != fork && m.ThreadId != created));
        check("unconfirmed member stays out of the known total", group.KnownTotals.TotalTokens == 45 && !group.KnownTotals.IsComplete &&
            group.Notes.Any(n => n.Contains("未确认")));
    }

    private static void InheritanceTests(Action<string, bool> check, string root)
    {
        var parent = "11111111-aaaa-4aaa-8aaa-111111111111";
        var child = "22222222-bbbb-4bbb-8bbb-222222222222";
        Write(root, parent, Meta(parent, subagent: false) + Context(parent) + Usage(parent, "p1", 80, 0, 20));
        // The forked child reports an inherited aggregate of 130 while only 30 belong to its own calls.
        Write(root, child, Meta(child, parent, subagent: true, forkedFrom: parent) + Context(child) +
            Usage(child, "c1", 25, 0, 5, aggregateTotal: 130));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor).Update(parent, 1, Prices, default);
        check("inherited fork baseline is never added", group.KnownTotals.TotalTokens == 130);
        check("inherited baseline is not shown as full coverage", group.Members.Single(m => m.ThreadId == child) is
            { TotalTokens: 30, TokenCoverage: RelatedCoverage.Partial } && !group.KnownTotals.IsComplete);
        check("partial member states the excluded aggregate", group.Members.Single(m => m.ThreadId == child).Issue?.Contains("不一致") == true);
    }

    private static void DedupTests(Action<string, bool> check, string root)
    {
        var member = "33333333-cccc-4ccc-8ccc-333333333333";
        var foreign = "44444444-dddd-4ddd-8ddd-444444444444";
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 10, 0, 5));
        var path = Write(root, member, Meta(member, RootId) + Context(member) +
            Usage(member, "m1", 10, 0, 5) + Usage(member, "m1", 10, 0, 5) +
            Usage(member, "m2", 20, 0, 10, threadOverride: foreign));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor).Update(RootId, 1, Prices, default);
        check("replayed report and foreign-thread record never add usage",
            group.Members.Single(m => m.ThreadId == member).TotalTokens == 15 &&
            group.Members.Single(m => m.ThreadId == member).Calls == 1);
        File.AppendAllText(path, Usage(member, "m1", 99, 0, 1), Utf8);
        group = new SessionGroupMonitor(monitor).Update(RootId, 2, Prices, default);
        check("conflicting duplicate keeps the first usage and reports an issue",
            group.Members.Single(m => m.ThreadId == member).TotalTokens == 15 &&
            group.Members.Single(m => m.ThreadId == member).TokenCoverage == RelatedCoverage.Partial);
        var archived = Path.Combine(root, "archived_sessions", member + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Move(path, archived);
        using var moved = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var movedGroup = new SessionGroupMonitor(moved).Update(RootId, 3, Prices, default);
        check("archived member copy is still one member", movedGroup.Members.Count(m => m.ThreadId == member) == 1 &&
            movedGroup.Members.Single(m => m.ThreadId == member).TotalTokens == 15);
        File.WriteAllText(archived, Meta(member, RootId) + Context(member) + Usage(member, "m9", 7, 0, 3), Utf8);
        var rewritten = new SessionGroupMonitor(moved).Update(RootId, 4, Prices, default);
        check("member log truncation recomputes instead of accumulating",
            rewritten.Members.Single(m => m.ThreadId == member).TotalTokens == 10);
    }

    private static void SideChatTests(Action<string, bool> check, string root)
    {
        var unknown = "66666666-ffff-4fff-8fff-666666666666";
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 10, 0, 5));
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var runtime = new ScriptedRuntime(RuntimeUsageReadResult.Unavailable("未启用本地调试连接"),
            Observed(SideId, RootId, 20, 0, 10), new RuntimeUsageReadResult(true, null, null, []),
            new RuntimeUsageReadResult(true, null, null, [], "侧聊结果超过观察上限"),
            Observed(SideId, RootId, 20, 0, 10));
        var group = new SessionGroupMonitor(monitor, runtime);
        var first = group.Update(RootId, 1, Prices, default);
        check("side chats stay out until the runtime entry answers", first.Members.All(m => m.Kind != RelatedMemberKind.SideChat) &&
            first.Notes.Any(n => n.Contains("侧聊")));
        check("an unavailable entry keeps the known total partial", !first.KnownTotals.IsComplete && first.KnownTotals.TotalTokens == 15);
        var second = group.Update(RootId, 2, Prices, default);
        check("mid-observation side chat appears with observed tokens",
            second.Members.Single(m => m.ThreadId == SideId) is { Kind: RelatedMemberKind.SideChat, TotalTokens: 30 } &&
            second.KnownTotals.TotalTokens == 45);
        check("a complete observation confirms the known total", second.KnownTotals.IsComplete);
        var closed = group.Update(RootId, 3, Prices, default);
        check("closed side chat keeps its observed numbers", closed.Members.Single(m => m.ThreadId == SideId) is
            { TotalTokens: 30, Issue: "已不在观察中 · 保留上次观察值" } && closed.Notes.Any(n => n.Contains("保留上次观察值")));
        check("an empty observation stays complete instead of counting as unknown", closed.KnownTotals.IsComplete);
        var truncated = group.Update(RootId, 4, Prices, default);
        check("a truncated observation retains the last observed side chat",
            truncated.Members.Single(m => m.ThreadId == SideId) is { TotalTokens: 30, Issue: "观察不完整 · 保留上次观察值" } &&
            truncated.Notes.Any(n => n.Contains("侧聊观察不完整") && n.Contains("侧聊结果超过观察上限")));
        check("a truncated observation marks the known total partial",
            !truncated.KnownTotals.IsComplete && truncated.KnownTotals.TotalTokens == 45);
        var restored = group.Update(RootId, 5, Prices, default);
        check("reconnected observation resumes without doubling", restored.KnownTotals.TotalTokens == 45);
        var unknownParent = new SessionGroupMonitor(monitor, new ScriptedRuntime(Observed(unknown, "12121212-3434-4434-8434-343434343434", 5, 0, 5)))
            .Update(RootId, 6, Prices, default);
        check("side chat with unknown parent stays unconfirmed and uncounted",
            unknownParent.Members.Single(m => m.ThreadId == unknown).Kind == RelatedMemberKind.Unconfirmed &&
            unknownParent.KnownTotals.TotalTokens == 15);
        check("side chat never fabricates itemized cost",
            second.Members.Single(m => m.ThreadId == SideId) is { Cost: null, CostCoverage: RelatedCoverage.Unavailable, Calls: 0 });
    }

    private static void PublicationTests(Action<string, bool> check)
    {
        var snapshot = new SessionGroupSnapshot(RootId, 4, 7, 3, 1, [], []);
        check("matching group versions publish", SessionGroupPublication.Matches(snapshot, RootId, 4, 7, 3));
        check("A to B to A switch rejects the stale group", !SessionGroupPublication.Matches(snapshot, RootId, 6, 7, 3));
        check("member add or remove rejects the old group", !SessionGroupPublication.Matches(snapshot, RootId, 4, 8, 3));
        check("price edit rejects the old group", !SessionGroupPublication.Matches(snapshot, RootId, 4, 7, 4));
        check("other root never publishes", !SessionGroupPublication.Matches(snapshot, SubId, 4, 7, 3));
        check("waiting group never matches", !SessionGroupPublication.Matches(SessionGroupSnapshot.Waiting, null, 0, 0, 0));
    }

    private static void ScaleTests(Action<string, bool> check, string root)
    {
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 10, 0, 5));
        for (var i = 0; i < 32; i++)
        {
            var id = $"00000000-0000-4000-8000-{i:D12}";
            Write(root, id, Meta(id, RootId, agentPath: "/root/m" + i) + Context(id) + Usage(id, "m" + i, 10, 0, 5));
        }
        for (var i = 0; i < 20; i++)
        {
            var id = $"11111111-0000-4000-8000-{i:D12}";
            Write(root, id, Meta(id, subagent: false) + Context(id) + Usage(id, "u" + i, 10, 0, 5));
        }
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor);
        var first = group.Update(RootId, 1, Prices, default);
        check("32 members all join the group", first.Members.Count == 33 && first.KnownTotals.TotalTokens == 33 * 15);
        check("unrelated sessions never join or open readers",
            first.Members.All(m => m.ThreadId == RootId || m.ParentThreadId == RootId) && monitor.MemberBytesRead("11111111-0000-4000-8000-000000000000") is null);
        var before = Enumerable.Range(0, 32).Select(i => monitor.MemberBytesRead($"00000000-0000-4000-8000-{i:D12}")).ToArray();
        var rootBefore = monitor.MemberBytesRead(RootId);
        var second = group.Update(RootId, 1, Prices, default);
        var after = Enumerable.Range(0, 32).Select(i => monitor.MemberBytesRead($"00000000-0000-4000-8000-{i:D12}")).ToArray();
        check("unchanged members are not reread from the start", before.SequenceEqual(after) &&
            monitor.MemberBytesRead(RootId) == rootBefore &&
            second.KnownTotals.TotalTokens == 33 * 15);
        var big = "22222222-0000-4000-8000-000000000000";
        var lines = new StringBuilder(Meta(big, RootId) + Context(big));
        for (var i = 0; i < 400; i++) lines.Append(Usage(big, "b" + i, 10, 0, 5, aggregateTotal: 15L * (i + 1)));
        Write(root, big, lines.ToString());
        monitor.RefreshRelations();
        var slice = monitor.PollMember(big, 65_536, default);
        check("bounded member read reports pending data", slice.Snapshot is { MoreDataPending: true } &&
            slice.Snapshot.Ledger!.Calls.Count < 400);
        var firstSlice = monitor.MemberBytesRead(big);
        while (monitor.PollMember(big, 65_536, default).Snapshot is { MoreDataPending: true }) { }
        check("bounded member read resumes and finishes", monitor.MemberBytesRead(big) > firstSlice &&
            monitor.PollMember(big, 65_536, default).Snapshot!.Ledger!.Calls.Count == 400);
    }

    // The member read limit: root plus 63 id-sorted members are read; anything beyond stays
    // visible as an explicit over-limit row. The selection must survive refreshes without
    // rotating readers, and a large member must advance its slices across consecutive updates.
    private static void ReadLimitTests(Action<string, bool> check, string root)
    {
        Write(root, RootId, Meta(RootId, subagent: false) + Context(RootId) + Usage(RootId, "r1", 10, 0, 5));
        var big = "00000000-0000-4000-8000-000000000000";
        var over = "00000000-0000-4000-8000-000000000063";
        for (var i = 0; i < 64; i++)
        {
            var id = $"00000000-0000-4000-8000-{i:D12}";
            Write(root, id, Meta(id, RootId, agentPath: "/root/m" + i) + Context(id) + Usage(id, "m" + i, 10, 0, 5));
        }
        var bigPath = Path.Combine(root, "sessions", big + ".jsonl");
        File.AppendAllText(bigPath, new string(' ', 2_300_000), Utf8);
        using var monitor = new TokenLogMonitor(Path.Combine(root, "sessions"));
        var group = new SessionGroupMonitor(monitor);
        var first = group.Update(RootId, 1, Prices, default);
        check("65 log members keep the root and 63 sorted ids in the read set",
            first.Members.Count == 65 && monitor.MemberBytesRead(RootId) is not null &&
            monitor.MemberBytesRead(big) is not null && monitor.MemberBytesRead(over) is null);
        check("an over-limit member stays visible, marked and outside the known total",
            first.Members.Single(m => m.ThreadId == over) is { Kind: RelatedMemberKind.Subagent, TotalTokens: null,
                CountedInTotals: false, Issue: "超过读取上限，未计入已知合计" } &&
            first.Notes.Any(n => n.Contains("超过读取上限")));
        check("a member beyond one slice reports reading and stays out of the known total",
            first.Members.Single(m => m.ThreadId == big) is { TokenCoverage: RelatedCoverage.Reading, CountedInTotals: false } &&
            monitor.MemberBytesRead(big) is { } slice && slice < new FileInfo(bigPath).Length && first.KnownTotals.TotalTokens == 15 * 63);
        var slice1 = monitor.MemberBytesRead(big);
        var second = group.Update(RootId, 1, Prices, default);
        var slice2 = monitor.MemberBytesRead(big);
        check("refreshes keep advancing the same member read position",
            slice1 is not null && slice2 > slice1 &&
            second.Members.Single(m => m.ThreadId == big).TokenCoverage == RelatedCoverage.Reading);
        var third = group.Update(RootId, 1, Prices, default);
        check("a large member finishes without restarting",
            third.Members.Single(m => m.ThreadId == big) is { TokenCoverage: RelatedCoverage.Complete, TotalTokens: 15, CountedInTotals: true });
        check("the known total counts every settled member once",
            third.KnownTotals.TotalTokens == 15 * 64 && third.KnownTotals.Cost is not null && !third.KnownTotals.IsComplete);
        var settled = monitor.MemberBytesRead(big);
        var fourth = group.Update(RootId, 1, Prices, default);
        check("a settled member is not reread from the start",
            monitor.MemberBytesRead(big) == settled && fourth.KnownTotals.TotalTokens == 15 * 64);
        var switched = group.Update("00000000-0000-4000-8000-000000000010", 2, Prices, default);
        check("switching the root releases the previous member readers",
            switched.Members.Count == 1 && monitor.MemberBytesRead(big) is null && monitor.MemberBytesRead(RootId) is null);
    }

    private static void ExpressionTests(Action<string, bool> check)
    {
        var expression = CodexRuntimeUsageReader.UsageExpression;
        check("runtime expression reads only whitelisted identifiers and numbers",
            expression.Contains("conversations") && expression.Contains("latestTokenUsageInfo") &&
            expression.Contains("sideConversation") && expression.Contains("parentThreadId") &&
            expression.Contains("inputTokens") && expression.Contains("forkedFromId"));
        check("runtime expression never touches conversation content",
            !expression.Contains("messages") && !expression.Contains("items") && !expression.Contains("title") &&
            !expression.Contains("content") && !expression.Contains("instructions") && !expression.Contains("prompt") &&
            !expression.Contains("turns"));
        check("runtime expression stays bounded", expression.Contains("20000") && expression.Contains("64"));
    }

    private static void TransportTests(Action<string, bool> check)
    {
        using var page = new SyntheticCdpPage();
        page.Payload = "{\"observations\":[{\"id\":\"" + SideId + "\",\"side\":true,\"ephemeral\":true,\"parent\":\"" + RootId +
            "\",\"parentPath\":\"/local/" + RootId + "\",\"counts\":{\"inputTokens\":20,\"cachedInputTokens\":5,\"outputTokens\":10,\"totalTokens\":30}}],\"truncated\":false,\"reason\":null}";
        using (var reader = new CodexRuntimeUsageReader())
        {
            var result = reader.ReadProcess(Environment.ProcessId, "runtime-transport", default);
            check("runtime transport parses the whitelist projection", result.IsAvailable && result.Observations.Count == 1 &&
                result.Observations[0] is { ThreadId: SideId, ParentThreadId: RootId, IsSideConversation: true } &&
                result.Observations[0].Counts!.EffectiveTotal == 30 && result.Observations[0].Counts!.CacheRead == 5);
        }
        page.Payload = "{\"observations\":[],\"truncated\":false,\"reason\":null}";
        using (var reader = new CodexRuntimeUsageReader())
        {
            var empty = reader.ReadProcess(Environment.ProcessId, "runtime-transport-empty", default);
            check("an empty projection without a reason stays unavailable", !empty.IsAvailable && empty.Observations.Count == 0);
        }
        page.Payload = "{\"observations\":[],\"truncated\":true,\"reason\":\"会话 store 超过观察上限（257）\"}";
        using (var reader = new CodexRuntimeUsageReader())
        {
            var truncated = reader.ReadProcess(Environment.ProcessId, "runtime-transport-truncated", default);
            check("runtime transport carries the truncation reason", truncated.IsAvailable &&
                truncated.TruncationReason == "会话 store 超过观察上限（257）" && !truncated.ObservationComplete);
        }
        page.Payload = "[]";
        using (var reader = new CodexRuntimeUsageReader())
        {
            var legacy = reader.ReadProcess(Environment.ProcessId, "runtime-transport-legacy", default);
            check("an unknown projection shape stays unavailable", !legacy.IsAvailable && legacy.Observations.Count == 0);
        }
        page.Payload = "null";
        using (var reader = new CodexRuntimeUsageReader())
        {
            var missing = reader.ReadProcess(Environment.ProcessId, "runtime-transport-missing", default);
            check("runtime transport reports a missing projection instead of guessing",
                !missing.IsAvailable && missing.Reason == "页面未提供运行时用量字段" && missing.Observations.Count == 0);
        }
        var camel = RuntimeUsageCounts.Parse(JsonSerializer.SerializeToElement(new { inputTokens = 7L, outputTokens = 3L, totalTokens = 10L }));
        check("runtime counts accept camelCase field names", camel.Input == 7 && camel.Output == 3 && camel.EffectiveTotal == 10);
    }

    private static RuntimeUsageReadResult Observed(string thread, string parent, long input, long cached, long output) =>
        new(true, null, null, new[] { new RuntimeUsageObservation(thread, parent, true, "/local/" + parent,
            new RuntimeUsageCounts(input, cached, 0, output, null, input + output)) });

    private static string Meta(string id, string? parent = null, bool subagent = true, string? agentPath = null,
        string? agentRole = null, string? threadSource = null, string? declaredParent = null, string? forkedFrom = null)
    {
        object source = subagent
            ? new { subagent = new { thread_spawn = new { parent_thread_id = parent, depth = 1, agent_path = agentPath, agent_role = agentRole } } }
            : "vscode";
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["source"] = source,
            ["cwd"] = "synthetic",
            ["thread_source"] = threadSource ?? (subagent ? "subagent" : "user")
        };
        if (declaredParent is not null) payload["parent_thread_id"] = declaredParent;
        else if (subagent && parent is not null) payload["parent_thread_id"] = parent;
        if (forkedFrom is not null) payload["forked_from_id"] = forkedFrom;
        return JsonSerializer.Serialize(new { type = "session_meta", payload }) + "\n";
    }

    private static string Context(string thread, string model = "relay/test") => JsonSerializer.Serialize(new
    {
        timestamp = "2026-10-12T10:00:00Z",
        type = "turn_context",
        payload = new { turn_id = "turn-" + thread, model }
    }) + "\n";

    private static string Usage(string thread, string response, long input, long cached, long output,
        long? aggregateTotal = null, string? threadOverride = null, long reasoning = 0)
    {
        var total = aggregateTotal ?? input + output;
        return JsonSerializer.Serialize(new
        {
            timestamp = "2026-10-12T10:00:05Z",
            type = "token_usage_record",
            payload = new
            {
                thread_id = threadOverride ?? thread,
                session_id = thread,
                response_id = response,
                turn_id = "turn-" + thread,
                usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0L,
                    output_tokens = output, reasoning_output_tokens = reasoning, total_tokens = input + output },
                thread_token_usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0L,
                    output_tokens = output, reasoning_output_tokens = reasoning, total_tokens = total }
            }
        }) + "\n";
    }

    private static string Write(string root, string id, string content, bool archived = false)
    {
        var directory = Path.Combine(root, archived ? "archived_sessions" : "sessions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".jsonl");
        File.WriteAllText(path, content, Utf8);
        return path;
    }

    private sealed class ScriptedRuntime(params RuntimeUsageReadResult[] results) : IRuntimeUsageSource
    {
        private readonly Queue<RuntimeUsageReadResult> _results = new(results.Length == 0 ? [RuntimeUsageReadResult.Disabled] : results);
        private RuntimeUsageReadResult _last = RuntimeUsageReadResult.Disabled;
        public RuntimeUsageReadResult Read(CancellationToken cancellationToken)
        {
            if (_results.Count > 0) _last = _results.Dequeue();
            return _last;
        }
    }
}
