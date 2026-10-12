using System.Text.Json;

namespace CodexTokenOverlay;

// The opt-in page-state recorder is off until it is started, writes one JSONL file per
// enabled run, never samples an unchanged state, and fails soft: a write failure, a
// full queue or the size limit stops the recording while the statistics keep running.
internal static class PageDiagnosticsTests
{
    private static readonly string[] RecoveryFieldNames = { "Completed", "Milliseconds", "Outcome" };

    internal static void Run(Action<string, bool> check, string directory)
    {
        check("raw page states are classified without identifiers",
            PageDiagnosticsRules.RawKind(new ActiveThreadRouteStatus(null, 1, false, 1, CodexViewIdentityReader.PagePendingError)) == "pending"
            && PageDiagnosticsRules.RawKind(new ActiveThreadRouteStatus("a", 1, true, 1, null)) == "ready"
            && PageDiagnosticsRules.RawKind(new ActiveThreadRouteStatus(null, 1, false, 1, "无法唯一识别当前页面")) == "unreadable"
            && PageDiagnosticsRules.RawKind(new ActiveThreadRouteStatus(null, 0, false, 1, null)) == "no-window"
            && PageDiagnosticsRules.RawKind(new ActiveThreadRouteStatus(null, 2, false, 1, null)) == "multi-window");

        var folder = Path.Combine(directory, "page-diagnostics");
        check("recording stays off until it is explicitly enabled", !Directory.Exists(folder));

        var run = Path.Combine(folder, "run");
        using (var recorder = PageDiagnosticsRecorder.Start(run, out var startError))
        {
            check("enabling creates one jsonl file for this run",
                startError is null && recorder is not null && Directory.GetFiles(run, "*.jsonl").Length == 1);
            recorder!.Observe(new("pending", PageWaitStage.Reading, 200, 1, true, false));
            recorder.Observe(new("pending", PageWaitStage.Reading, 900, 1, true, false));
            recorder.Observe(new("pending", PageWaitStage.NotReady, 3_200, 1, true, false,
                new PageRecoverySnapshot(2, PageRecoveryOutcome.DocumentReached, 41)));
            recorder.Observe(new("ready", PageWaitStage.None, 0, 1, true, true,
                new PageRecoverySnapshot(2, PageRecoveryOutcome.DocumentReached, 41)));
            recorder.Stop("测试结束");
            check("a stopped recorder keeps its reason for the tray", recorder.StopReason == "测试结束");
        }
        var lines = File.ReadAllLines(Directory.GetFiles(run, "*.jsonl").Single())
            .Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line)).ToArray();
        check("reading the pending page and showing the pending stage stay distinguishable",
            lines.Any(l => l.RootElement.GetProperty("Raw").GetString() == "pending"
                && l.RootElement.GetProperty("Stage").GetString() == "reading")
            && lines.Any(l => l.RootElement.GetProperty("Raw").GetString() == "pending"
                && l.RootElement.GetProperty("Stage").GetString() == "not-ready"));
        check("unchanged sampling does not repeat a line", lines.Length == 4);
        check("recovery records the wait that preceded it",
            lines.Any(l => l.RootElement.GetProperty("Kind").GetString() == "recovered"
                && l.RootElement.GetProperty("PendingMs").GetDouble() == 3_200));
        check("the recovery state is recorded without identifiers",
            lines.Any(l => l.RootElement.TryGetProperty("Recovery", out var recovery)
                && recovery.GetProperty("Completed").GetInt32() == 2
                && recovery.GetProperty("Outcome").GetString() == "document"
                && recovery.GetProperty("Milliseconds").GetDouble() == 41));
        check("the recovery record keeps its own identifier-free allow-list",
            lines.Where(l => l.RootElement.TryGetProperty("Recovery", out _)).All(l =>
                l.RootElement.GetProperty("Recovery").EnumerateObject().Select(p => p.Name).Order()
                    .SequenceEqual(RecoveryFieldNames)));
        check("the stop reason is recorded",
            lines.Any(l => l.RootElement.GetProperty("Kind").GetString() == "stop"
                && l.RootElement.GetProperty("Reason").GetString() == "测试结束"));
        var properties = lines.SelectMany(l => l.RootElement.EnumerateObject().Select(p => p.Name))
            .Distinct().Order().ToArray();
        check("the record schema stays on the identifier-free allow-list",
            properties.SequenceEqual(new[] { "Generation", "Kind", "Numbers", "PendingMs", "Raw", "Reason", "Recovery", "Stage", "Time", "Visible" }));

        var limited = Path.Combine(folder, "limited");
        using (var recorder = PageDiagnosticsRecorder.Start(limited, out _, maximumBytes: 400))
        {
            for (var i = 0; i < 40; i++)
                recorder!.Observe(new(i % 2 == 0 ? "pending" : "ready", PageWaitStage.NotReady, i, 1, true, false));
            var deadline = Environment.TickCount64 + 2_000;
            while (recorder!.StopReason is null && Environment.TickCount64 < deadline) Thread.Sleep(5);
            check("the size limit stops recording instead of growing the file",
                recorder.StopReason == "达到单文件上限"
                && new FileInfo(Directory.GetFiles(limited, "*.jsonl").Single()).Length <= 400);
            recorder.Observe(new("pending", PageWaitStage.LongWait, 1, 1, true, false));
        }
        check("observing a stopped recorder neither throws nor writes",
            new FileInfo(Directory.GetFiles(limited, "*.jsonl").Single()).Length <= 400);

        var blocked = Path.Combine(directory, "page-diagnostics-blocked");
        File.WriteAllText(blocked, "not a directory");
        var failed = PageDiagnosticsRecorder.Start(Path.Combine(blocked, "run"), out var failureReason);
        check("an unusable folder reports a start failure instead of throwing",
            failed is null && failureReason is not null);
    }
}
