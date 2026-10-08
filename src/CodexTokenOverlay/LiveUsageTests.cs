using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class LiveUsageTests
{
    internal static string Row(string type, object payload) => JsonSerializer.Serialize(new
        { timestamp = "2026-10-05T12:00:00Z", type, payload }) + '\n';
    internal static string Record(string id, UsageAmounts usage, UsageAmounts? total = null, string thread = "live") =>
        Row("token_usage_record", new { thread_id = thread, session_id = "session", response_id = id, turn_id = "turn",
            usage = SessionCostTests.Usage(usage), thread_token_usage = total is null ? null : SessionCostTests.Usage(total) });
    internal static string Started => Row("event_msg", new { type = "task_started", turn_id = "turn" });
    internal static string Legacy(UsageAmounts u) => Row("event_msg", new { type = "token_count",
        info = new { total_token_usage = SessionCostTests.Usage(u), last_token_usage = SessionCostTests.Usage(u), model_context_window = 272_000 } });
    internal static string Meta => Row("session_meta", new { id = "live" });
    internal static string Context => Row("turn_context", new { turn_id = "turn", model = "relay/test" });
    private static UsageAmounts U(long input, long output = 10, long cache = 40) => new(input, cache, 0, output, 5, input + output);

    internal static void Run(Action<string, bool> check, string directory)
    {
        var root = Path.Combine(directory, "live-usage"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "live.jsonl"); var utf8 = new UTF8Encoding(false);
        File.WriteAllText(path, Meta + Started + Context, utf8);
        var reader = new IncrementalSessionReader(path, "live");
        var waiting = reader.Read()!;
        check("running new turn waits without fabricated tokens", waiting.TurnInProgress && !waiting.UsageRecorded && waiting.EffectiveTotalTokens is null);
        var p = OverlayPresentationBuilder.Create(waiting, DisplayField.Total, DisplayField.CacheHitRate, DisplayField.Total);
        check("waiting running indicator names missing call report", p.StatusText?.Contains("等待调用") == true);
        void Append(string row) => File.AppendAllText(path, row, utf8);
        Append(Record("one", U(100), U(100)));
        var first = reader.Read()!;
        var bytesRead = reader.TotalBytesRead;
        for (var unchanged = 0; unchanged < 20; unchanged++) reader.Read();
        check("unchanged polling reuses published snapshot and reads no log bytes",
            ReferenceEquals(first, reader.Read()) && reader.TotalBytesRead == bytesRead);
        check("first call advances tokens before task completion or legacy snapshot", first.TotalTokens == 110 && first.TurnInProgress && first.UsageSource == "逐次线程累计");
        check("latest call supplies context estimate immediately", first.ContextUsedTokens == 110 && first.ContextWindowTokens is null);
        p = OverlayPresentationBuilder.Create(first, DisplayField.Total, DisplayField.CacheHitRate, DisplayField.Total);
        check("running indicator retains numeric metrics", p.StatusText is null && p.Primary.Value == "110" && p.ExtraText!.StartsWith("进行中"));
        Append(Record("two", U(150), U(250, 20, 80)));
        var second = reader.Read()!;
        check("second call in same running turn immediately accumulates", second.TotalTokens == 270 && second.TurnCount == 1 && second.OutputTokens == 20);
        Append(Legacy(U(100)));
        check("delayed legacy snapshot cannot roll reported aggregate back", reader.Read()!.TotalTokens == 270);
        Append(Legacy(U(250, 20, 80) with { CacheRead = null }));
        var equal = reader.Read()!;
        check("equal legacy totals cannot erase known cache fields from a modern aggregate", equal.TotalTokens == 270 && equal.CachedInputTokens == 80 && equal.UsageSource == "逐次线程累计");
        Append(Record("two", U(150), U(250, 20, 80)));
        check("replayed request never adds tokens or a priced call", reader.Read()!.TotalTokens == 270 && reader.Read()!.Ledger!.Calls.Count == 2);
        Append(Record("same-size-new-id", U(150), U(400, 30, 120)));
        check("equal usage with different response identity counts separately", reader.Read()!.TotalTokens == 430 && reader.Read()!.Ledger!.Calls.Count == 3);
        Append(Record("foreign", U(1_000), U(1_000), "another-thread"));
        check("foreign thread cannot change running tokens", reader.Read()!.TotalTokens == 430 && reader.Read()!.Ledger!.Calls.Count == 3);
        var half = Record("partial", U(75), U(475, 40, 160)); Append(half[..(half.Length / 2)]);
        check("partial call row preserves current running totals", reader.Read()!.TotalTokens == 430);
        Append(half[(half.Length / 2)..]);
        check("completed row advances without requiring task_complete", reader.Read()!.TotalTokens == 515 && reader.Read()!.TurnInProgress);
        Append(Row("event_msg", new { type = "task_complete", turn_id = "turn" }));
        var done = reader.Read()!;
        check("task completion changes state without adding usage", !done.TurnInProgress && done.TotalTokens == 515);
        Append(Record("conflict", U(50), U(525, 50, 200))); reader.Read();
        Append(Record("conflict", U(55), U(580, 60, 240)));
        var conflict = reader.Read()!;
        check("conflicting duplicate does not overwrite tokens and reports anomaly", conflict.TotalTokens == 575 && conflict.Issue?.Contains("冲突") == true);
        Append(Record("malformed-aggregate", U(50), U(10) with { CacheRead = 40 }));
        check("invalid aggregate preserves last accepted totals and shows issue", reader.Read()!.TotalTokens == 575 && reader.Read()!.Issue?.Contains("累计异常") == true);
        Append(Record("recovered", U(50), U(625, 70, 280)));
        check("valid aggregate resumes updates after malformed aggregate", reader.Read()!.TotalTokens == 695);
        Append(Record("out-of-order", U(25), U(500, 50, 200)));
        check("out-of-order older aggregate cannot decrease lifetime count", reader.Read()!.TotalTokens == 695);
        Append(Record("without-aggregate", U(100)));
        check("unique call extends last thread aggregate when aggregate omitted", reader.Read()!.TotalTokens == 805);

        File.WriteAllText(path, Meta + Started + Context + Record("a", U(100)) + Record("b", U(100)), utf8);
        reader = new(path, "live"); var calls = reader.Read()!;
        check("record-only logs sum unique calls without zeroing missing legacy", calls.TotalTokens == 220 && calls.UsageSource == "已记录调用合计");
        Append(Legacy(U(200, 20, 80)));
        check("matching cumulative snapshot is not added to call sum", reader.Read()!.TotalTokens == 220);
        var calculator = new SessionCostCalculator();
        var prices = new PricingRevision(1, new(1, new[] { SessionCostTests.Profile() }));
        var cost = calculator.Calculate(reader.Read()!.Ledger!, prices);
        check("live token source does not alter per-call billing", cost.RecordedCalls == 2 && cost.Amounts?.Total == .0001568m);

        File.WriteAllText(path, Meta + Legacy(U(1_000)) + Record("a", U(100)), utf8); reader = new(path, "live");
        check("historical gap never adds uncertain call sum on top of legacy", reader.Read()!.TotalTokens == 1_010);
        Append(Record("aggregate", U(100), U(1_200, 30, 120)));
        check("thread aggregate restores lifetime count across historic gap", reader.Read()!.TotalTokens == 1_230);
        Append(Legacy(U(800, 20, 80)));
        check("compaction snapshot cannot reduce lifetime consumption", reader.Read()!.TotalTokens == 1_230);
        Append(Legacy(U(1_300, 40, 160)));
        check("newer complete legacy usage may advance totals before next call report", reader.Read()!.TotalTokens == 1_340 && reader.Read()!.UsageSource == "累计快照");

        File.WriteAllText(path, Meta + Started + Record("missing", new(100, null, null, 10, Total: 110)), utf8); reader = new(path, "live");
        check("missing cache stays unknown while explicit input/output update", reader.Read()!.TotalTokens == 110 && reader.Read()!.CachedInputTokens is null);
        File.WriteAllText(path, Meta + Record("zero", new(0, 0, 0, 0, 0, 0)), utf8); reader = new(path, "live");
        check("explicit zeros are accepted without a fabricated hit rate", reader.Read()!.TotalTokens == 0 && reader.Read()!.CacheHitPercent is null);
        File.WriteAllText(path, Meta + Record("negative", U(100) with { Output = -1 }), utf8); reader = new(path, "live");
        check("invalid call is visibly abnormal instead of silently normalized", reader.Read()!.Issue is not null);
        File.WriteAllText(path, Meta + Record("huge", new(long.MaxValue, 0, 0, 0)) + Record("overflow", new(1, 0, 0, 0)), utf8); reader = new(path, "live");
        check("live addition overflow surfaces issue without wrapped number", reader.Read()!.EffectiveTotalTokens is null && reader.Read()!.Issue?.Contains("溢出") == true);

        var archiveRoot = Path.Combine(directory, "live-archive"); Directory.CreateDirectory(Path.Combine(archiveRoot, "sessions")); Directory.CreateDirectory(Path.Combine(archiveRoot, "archived_sessions"));
        var active = Path.Combine(archiveRoot, "sessions", "live.jsonl");
        File.WriteAllText(active, Meta + Started + Record("archived", U(100), U(100)), utf8);
        File.Copy(active, Path.Combine(archiveRoot, "archived_sessions", "live.jsonl"));
        using var monitor = new TokenLogMonitor(archiveRoot) { PreferredThreadId = "live" };
        check("active and archived copies remain one live call", monitor.Poll(true)!.TotalTokens == 110 && monitor.Poll()!.Ledger!.Calls.Count == 1);
        File.WriteAllText(path, Meta + Legacy(U(100)), utf8); reader = new(path, "live");
        check("legacy-only token display remains compatible", reader.Read()!.TotalTokens == 110 && !reader.Read()!.TurnInProgress);
        File.WriteAllText(path, Meta + Started + Context, utf8);
        check("truncation clears live accumulator and starts clean", reader.Read()!.TotalTokens is null && reader.Read()!.TurnInProgress && reader.Read()!.Ledger!.Calls.Count == 0);

        var rewrite = Meta + Legacy(U(100));
        File.WriteAllText(path, rewrite, utf8);
        reader = new(path, "live"); var beforeRewrite = reader.Read()!;
        var originalTime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, Meta + Legacy(U(200)), utf8);
        File.SetLastWriteTimeUtc(path, originalTime.AddSeconds(1));
        var afterRewrite = reader.Read()!;
        check("equal-length rewrite invalidates unchanged snapshot cache",
            new FileInfo(path).Length == Encoding.UTF8.GetByteCount(rewrite) &&
            afterRewrite.TotalTokens == 210 && !ReferenceEquals(beforeRewrite, afterRewrite));
        Append(Legacy(U(300)));
        using var cancelled = new CancellationTokenSource();
        try { reader.Read(cancelled.Token, cancelled.Cancel); } catch (OperationCanceledException) { }
        check("cancelled EOF read cannot return a previously cached snapshot",
            reader.ReadOffset == new FileInfo(path).Length && reader.Read()!.TotalTokens == 310);
    }
}
