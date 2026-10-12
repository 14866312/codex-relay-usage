using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class ConversationSwitchTests
{
    internal static void Run(Action<string, bool> check, string temporaryDirectory)
    {
        var titles = new Dictionary<string, string> { ["a"] = "中文对话 A", ["b"] = "中文对话 B" };
        var resolver = new SidebarSessionResolver();
        var noProjects = new LocalProjectIndex();
        check("actual visible title overrides unrelated retained subscriptions",
            resolver.Resolve(new(1, "中文对话 A"), titles, noProjects).ThreadId == "a");
        check("visible idle page resolves without a subscription event",
            resolver.Resolve(new(1, "中文对话 B"), titles, noProjects).ThreadId == "b");
        titles["duplicate"] = "中文对话 A";
        check("duplicate visible titles fail closed",
            resolver.Resolve(new(1, "中文对话 A"), titles, noProjects) is { ThreadId: null, SameTitleAmbiguous: true });
        check("title matching is exact", resolver.Resolve(new(1, "中文对话 B…"), titles, noProjects).ThreadId is null);
        check("home and unindexed pages do not retain a conversation",
            resolver.Resolve(new(1, "Codex"), titles, noProjects).ThreadId is null &&
            resolver.Resolve(new(1, "ChatGPT"), titles, noProjects).ThreadId is null &&
            resolver.Resolve(new(1, "unknown"), titles, noProjects).ThreadId is null);
        check("window ambiguity and accessibility errors clear identity",
            resolver.Resolve(new(2, "中文对话 B"), titles, noProjects).ThreadId is null &&
            resolver.Resolve(new(0, "中文对话 B"), titles, noProjects).ThreadId is null &&
            resolver.Resolve(new(1, "中文对话 B", Error: "unavailable"), titles, noProjects).ThreadId is null);
        const string uuid = "11111111-2222-4333-8444-555555555555";
        check("explicit document route bypasses duplicate title matching",
            resolver.Resolve(new(1, "中文对话 A", uuid), titles, noProjects) is { ThreadId: uuid, Method: "页面路由" });
        check("document route accepts only validated thread identifiers",
            CodexViewIdentityReader.ThreadIdFromUrl("app://-/threads/" + uuid) == uuid &&
            CodexViewIdentityReader.ThreadIdFromUrl("app://-/thread/" + uuid) == uuid &&
            CodexViewIdentityReader.ThreadIdFromUrl("app://-/index.html?thread=" + uuid) is null &&
            CodexViewIdentityReader.ThreadIdFromUrl("https://example.com/threads/" + uuid) is null &&
            CodexViewIdentityReader.ThreadIdFromUrl("app://-/threads/not-an-id") is null);

        var home = Path.Combine(temporaryDirectory, "switching");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions); Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
        var indexPath = Path.Combine(home, "session_index.jsonl");
        var index = new SessionTitleIndex(); index.SetRoot(home); index.Refresh();
        check("missing title index cannot invent an identity", index.Titles.Count == 0 && index.Error is not null);
        string Title(string id, string title) => JsonSerializer.Serialize(new { id, thread_name = title }) + '\n';
        File.WriteAllText(indexPath, Title("a", "旧标题") + "{broken" + '\n' +
            JsonSerializer.Serialize(new { id = "blank", thread_name = "   " }) + '\n' +
            Title("b", "中文对话 B") + Title("a", "中文对话 A"));
        index.SetRoot(sessions); index.Refresh();
        check("latest rename wins and bad or blank rows stay out",
            index.Titles.GetValueOrDefault("a") == "中文对话 A" && !index.Titles.ContainsKey("blank") &&
            !index.Titles.Values.Contains("旧标题") && index.Error is null);
        var rename = Title("b", "新标题 B"); var split = rename.Length / 2;
        File.AppendAllText(indexPath, rename[..split]); index.Refresh();
        check("half-written title row retains previous complete titles", index.Titles.GetValueOrDefault("b") == "中文对话 B");
        File.AppendAllText(indexPath, rename[split..]); index.Refresh();
        check("completed title rename updates without restarting",
            index.Titles.GetValueOrDefault("b") == "新标题 B" && !index.Titles.Values.Contains("中文对话 B"));
        var cachedTitles = index.Titles; var size = (int)new FileInfo(indexPath).Length; var stamp = File.GetLastWriteTimeUtc(indexPath);
        File.WriteAllText(indexPath, new string(' ', size));
        File.SetLastWriteTimeUtc(indexPath, stamp); index.Refresh();
        check("unchanged length and timestamp reuse the cached titles", ReferenceEquals(cachedTitles, index.Titles));
        File.Delete(indexPath); index.Refresh();
        check("deleted index clears previously cached mappings", index.Titles.Count == 0);
        File.WriteAllText(indexPath, Title("a", "新索引 A")); index.Refresh();
        check("recreated index can recover automatically", index.Titles.GetValueOrDefault("a") == "新索引 A");

        var selection = new SessionSelection();
        var route = new ActiveThreadRouteStatus("a", 1, true, 1, null);
        TokenSnapshot Snapshot(string id, long total = 100) => DiagnosticsRunner.Example() with { ThreadId = id, TotalTokens = total };
        selection.Observe(route, null, 0); var originalA = selection.Request();
        check("matching read can publish", selection.TryPublish(originalA, Snapshot("a"), null));
        check("unchanged selection preserves displayed usage", !selection.Observe(route, null, 0) && selection.Snapshot?.TotalTokens == 100);
        selection.Observe(route with { ThreadId = "b", Version = 2 }, null, 0); var originalB = selection.Request();
        check("switch clears old usage immediately", selection.ThreadId == "b" && selection.Snapshot is null);
        check("late A cannot overwrite B", !selection.TryPublish(originalA, Snapshot("a"), null));
        selection.Observe(route with { Version = 3 }, null, 0);
        check("A to B to A rejects the first A read", !selection.TryPublish(originalA, Snapshot("a"), null));
        check("late null and error cannot clear newer selection", !selection.TryPublish(originalB, null, "obsolete error") && selection.Error is null);
        check("snapshot identity must match the requested page", !selection.TryPublish(selection.Request(), Snapshot("b"), null));
        var obsolete = new List<SessionReadRequest>();
        for (var i = 0; i < 60; i++)
        {
            obsolete.Add(selection.Request());
            selection.Observe(route with { ThreadId = i % 2 == 0 ? "b" : "a", Version = 10 + i }, null, 0);
        }
        selection.TryPublish(selection.Request(), Snapshot("a", 999), null);
        var rejected = obsolete.AsEnumerable().Reverse().All(request => !selection.TryPublish(request, Snapshot(request.ThreadId!), null));
        check("sixty rapid switches reject all out-of-order completions", rejected && selection.Snapshot?.TotalTokens == 999);
        var beforeDisconnect = selection.Request();
        selection.Observe(route with { IsConnected = false, Version = 100 }, null, 0);
        check("disconnect clears tokens and rejects in-flight usage", selection.ThreadId is null && selection.Snapshot is null &&
            !selection.TryPublish(beforeDisconnect, Snapshot("a"), null));
        selection.Observe(route with { IsConnected = false, Version = 101 }, "b", 1);
        selection.TryPublish(selection.Request(), Snapshot("b"), null);
        check("manual lock ignores page subscription churn", !selection.Observe(route with { ThreadId = "a", Version = 105 }, "b", 1) &&
            selection.Snapshot?.ThreadId == "b");
        var beforeRootChange = selection.Request();
        selection.Observe(route, "b", 2);
        check("log directory change rejects old reads even for the same ID", selection.Snapshot is null && !selection.TryPublish(beforeRootChange, Snapshot("b"), null));
        selection.Observe(route with { ActiveWindowCount = 2, Version = 106 }, null, 2);
        check("unlocking into ambiguous windows clears manual data", selection.ThreadId is null && selection.Snapshot is null);
        selection.Observe(route with { ThreadId = "empty", Version = 107 }, null, 2);
        selection.TryPublish(selection.Request(), Snapshot("empty") with { TotalTokens = null, UsageRecorded = false }, null);
        check("empty new conversation never displays previous tokens", selection.Snapshot?.ThreadId == "empty" && selection.Snapshot.TotalTokens is null);

        var utf8 = new UTF8Encoding(false);
        string Meta(string id) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + '\n';
        string Usage(long input) => JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:30:00Z", type = "event_msg",
            payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, output_tokens = 5, total_tokens = input + 5 } } } }) + '\n';
        var a = Path.Combine(sessions, "a.jsonl"); var b = Path.Combine(sessions, "b.jsonl");
        File.WriteAllText(a, Meta("a") + Usage(10), utf8); File.WriteAllText(b, Meta("b") + Usage(20), utf8);
        using var monitor = new TokenLogMonitor(home);
        monitor.Poll("a", true); var aBytes = monitor.ReaderBytesRead("a");
        check("different idle log is selected without a write", monitor.Poll("b")!.TotalTokens == 25);
        var bBytes = monitor.ReaderBytesRead("b");
        check("warm revisit retains values without rereading the log", monitor.Poll("a")!.TotalTokens == 15 && monitor.ReaderBytesRead("a") == aBytes);
        var append = Usage(30); File.AppendAllText(a, append, utf8);
        monitor.Poll("b", true);
        check("revisit reads only bytes appended in the background", monitor.Poll("a")!.TotalTokens == 35 &&
            monitor.ReaderBytesRead("a") == aBytes + utf8.GetByteCount(append) && monitor.ReaderBytesRead("b") == bBytes);
        var half = Usage(40); File.AppendAllText(a, half[..(half.Length / 2)], utf8); monitor.Poll("a", true);
        monitor.Poll("b");
        File.AppendAllText(a, half[(half.Length / 2)..], utf8);
        check("partial usage survives switching away and back", monitor.Poll("a", true)!.TotalTokens == 45);
        var archive = Path.Combine(home, "archived_sessions", "a.jsonl"); File.Move(a, archive);
        check("cached reader follows archive moves without double counting", monitor.Poll("a", true)!.TotalTokens == 45);
        var alternate = Path.Combine(temporaryDirectory, "alternate"); Directory.CreateDirectory(alternate);
        File.WriteAllText(Path.Combine(alternate, "a.jsonl"), Meta("a") + Usage(90), utf8);
        monitor.SetRoot(alternate);
        check("root change invalidates same-ID reader cache", monitor.Poll("a", true)!.TotalTokens == 95);
        monitor.SetRoot(home); monitor.Poll("a", true); aBytes = monitor.ReaderBytesRead("a");
        monitor.SetRoot(sessions); monitor.Poll("a");
        check("equivalent normalized root preserves incremental progress", monitor.ReaderBytesRead("a") == aBytes);
        for (var i = 0; i < TokenLogMonitor.ReaderCacheCapacity + 2; i++)
        {
            var id = "cache-" + i; File.WriteAllText(Path.Combine(sessions, id + ".jsonl"), Meta(id) + Usage(i), utf8);
            monitor.Poll(id, true);
        }
        check("reader cache bounds memory while retaining recent sessions", monitor.ReaderBytesRead("cache-0") is null &&
            monitor.ReaderBytesRead("cache-9") is not null && monitor.Poll("cache-9")!.TotalTokens == 14);

        var cancelPath = Path.Combine(sessions, "cancel.jsonl");
        var prefix = Meta("cancel") + Usage(100) + "{\"type\":\"turn_context\",\"payload\":{\"padding\":\"";
        var padded = prefix + new string('x', 65_535 - utf8.GetByteCount(prefix)) + "中\",\"model\":\"relay/中文-🤖\"}}\n" + Usage(200);
        File.WriteAllText(cancelPath, padded, utf8);
        var reader = new IncrementalSessionReader(cancelPath, "cancel");
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        try { reader.Read(() => cancellation.Cancel(), cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check("large read cancellation stops after a complete byte block", cancelled && reader.ReadOffset == 65_536 && reader.TotalBytesRead == 65_536);
        var resumed = reader.Read();
        check("cancelled read resumes a split UTF8 line without loss or duplicate bytes", resumed?.TotalTokens == 205 &&
            resumed.Model == "relay/中文-🤖" && reader.TotalBytesRead == new FileInfo(cancelPath).Length);
        var once = reader.TotalBytesRead; reader.Read();
        check("resumed reader is incremental on the following poll", reader.TotalBytesRead == once);
        var appendedWhileReading = false;
        File.AppendAllText(cancelPath, Usage(250), utf8);
        var concurrent = reader.Read(blockProcessed: () =>
        {
            if (appendedWhileReading) return;
            appendedWhileReading = true; File.AppendAllText(cancelPath, Usage(260), utf8);
        });
        once = reader.TotalBytesRead; reader.Read();
        check("appends during an active read do not restart a large log", concurrent?.TotalTokens == 265 && reader.TotalBytesRead == once);
        var eofPath = Path.Combine(sessions, "cancel-eof.jsonl"); File.WriteAllText(eofPath, Meta("cancel-eof") + Usage(300), utf8);
        var eofReader = new IncrementalSessionReader(eofPath, "cancel-eof");
        using var eofCancellation = new CancellationTokenSource();
        try { eofReader.Read(() => eofCancellation.Cancel(), cancellationToken: eofCancellation.Token); } catch (OperationCanceledException) { }
        var eofBytes = eofReader.TotalBytesRead;
        check("cancellation at EOF preserves fingerprint and latest parsed usage", eofReader.Read()!.TotalTokens == 305 && eofReader.TotalBytesRead == eofBytes);
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel(); cancelled = false;
        try { monitor.Poll("cache-9", true, alreadyCancelled.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("superseded request does not begin a directory scan", cancelled);
    }
}
