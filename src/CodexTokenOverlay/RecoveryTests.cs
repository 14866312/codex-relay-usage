using System.Diagnostics;

namespace CodexTokenOverlay;

// The recovery walk exists because Chromium builds its accessibility tree on demand:
// after a Codex restart the MSAA reads can see only an unpopulated document node until
// a UIA client walks the view to the document boundary. These checks model that with a
// synthetic offscreen window that fills its own document the moment a UIA walk reaches
// it, prove that asking the root element alone never gets there, and pin the bounded
// ladder that requests the walks.
internal static class RecoveryTests
{
    private const string SyntheticRoute = "app://-/threads/11111111-2222-4333-8444-555555555555";
    private const string SyntheticThreadId = "11111111-2222-4333-8444-555555555555";

    internal static void Run(Action<string, bool> check, string directory)
    {
        TargetAndOutcomeTests(check);
        LadderTests(check, directory);
        SyntheticWindowTests(check, directory);
        StuckAndFailingWalkTests(check);
    }

    private static long TicksOf(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    private static bool WaitUntil(Func<bool> condition, int timeout = 3_000)
    {
        var deadline = Environment.TickCount64 + timeout;
        while (!condition() && Environment.TickCount64 < deadline) Thread.Sleep(5);
        return condition();
    }

    // A window message pump is what lets the monitor thread and the recovery worker
    // reach the synthetic window's accessible objects across apartments.
    private static bool PumpUntil(Func<bool> condition, int timeout = 5_000)
    {
        var deadline = Environment.TickCount64 + timeout;
        while (!condition() && Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        return condition();
    }

    // A negative assertion must cover several monitor ticks (150 ms each).
    private static void Settle() => Thread.Sleep(700);

    private static void TargetAndOutcomeTests(Action<string, bool> check)
    {
        check("a window lifetime parses into a bound recovery target",
            PageRecoveryTarget.TryParse("1234/5678/9012", out var target)
            && target == new PageRecoveryTarget(1234, 5678, 9012)
            && !PageRecoveryTarget.TryParse("1234/5678", out _)
            && !PageRecoveryTarget.TryParse("0/5678/9012", out _)
            && !PageRecoveryTarget.TryParse(null, out _));
        check("recovery outcomes carry identifier-free diagnostic names",
            PageRecoveryRules.OutcomeName(PageRecoveryOutcome.DocumentReached) == "document"
            && PageRecoveryRules.OutcomeName(PageRecoveryOutcome.BoundaryMissing) == "boundary-missing"
            && PageRecoveryRules.OutcomeName(PageRecoveryOutcome.Timeout) == "timeout"
            && PageRecoveryRules.OutcomeName(PageRecoveryOutcome.WindowGone) == "window-gone"
            && PageRecoveryRules.OutcomeName(PageRecoveryOutcome.Failed) == "failed"
            && PageRecoveryRules.OutcomeName(PageRecoveryOutcome.None) is null);
        check("the walk budget matches the documented bounds",
            CodexAccessibilityRecovery.MaximumNodes == 128 && CodexAccessibilityRecovery.MaximumDepth == 12
            && CodexAccessibilityRecovery.MaximumChildrenPerNode == 64
            && CodexAccessibilityRecovery.TraversalBudget == TimeSpan.FromMilliseconds(1500)
            && CodexAccessibilityRecovery.ConnectionTimeoutMilliseconds == 250
            && CodexAccessibilityRecovery.TransactionTimeoutMilliseconds == 250);
    }

    // The ladder is anchored to the first pending read: immediately, then at two and
    // five seconds, at most three walks per window lifetime. A rebuilt window lifetime
    // resets it, multiple visible windows never ask, and a readable page stops it.
    private static void LadderTests(Action<string, bool> check, string directory)
    {
        var root = Path.Combine(directory, "recovery-ladder", "sessions");
        Directory.CreateDirectory(root);
        var clock = Stopwatch.GetTimestamp();
        var requests = new List<PageRecoveryTarget>();
        var requestsLock = new object();
        var view = new CodexViewIdentity(1, null, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: "1234/5678/9012");
        using var recovery = new CodexAccessibilityRecovery(target =>
        {
            lock (requestsLock) requests.Add(target);
            return PageRecoveryOutcome.DocumentReached;
        });
        using var monitor = new CodexVisibleThreadMonitor(root, () => view, clock: () => clock, recovery: recovery);
        int Count() { lock (requestsLock) return requests.Count; }
        bool RequestIs(int index, long handle) { lock (requestsLock) return requests.Count > index && requests[index].Handle == handle; }

        check("the first pending read requests one recovery walk immediately",
            WaitUntil(() => Count() >= 1) && Count() == 1 && RequestIs(0, 1234));
        clock += TicksOf(2);
        check("the ladder retries two seconds after the wait started", WaitUntil(() => Count() >= 2) && Count() == 2);
        clock += TicksOf(3);
        check("the ladder retries five seconds after the wait started", WaitUntil(() => Count() >= 3) && Count() == 3);
        clock += TicksOf(30);
        Settle();
        check("the ladder stops after three requests per window lifetime", Count() == 3);

        view = new CodexViewIdentity(2, null);
        Settle();
        check("multiple visible windows never request a recovery walk", Count() == 3);

        view = new CodexViewIdentity(1, "合成会话", SyntheticThreadId, Lifecycle: "1234/5678/9012");
        check("a readable page ends the pending episode", WaitUntil(() => monitor.GetStatus().ThreadId is not null));
        clock += TicksOf(30);
        Settle();
        check("a recovered page issues no further walks", Count() == 3);

        view = new CodexViewIdentity(1, null, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: "1234/5678/9012");
        clock += TicksOf(30);
        Settle();
        check("a later pending read in the same window lifetime stays capped", Count() == 3);

        view = new CodexViewIdentity(1, null, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: "2222/3333/4444");
        check("a rebuilt window lifetime gets its own ladder", WaitUntil(() => Count() >= 4) && RequestIs(3, 2222));
        clock += TicksOf(30);
        check("the rebuilt lifetime is capped at three requests as well", WaitUntil(() => Count() == 6) && Count() == 6);
    }

    // The synthetic document fills its own URL the moment a UIA client reaches the
    // document node, the way Chromium builds its tree on demand; the production read
    // path alone never gets there.
    private static void SyntheticWindowTests(Action<string, bool> check, string directory)
    {
        using var host = new PageReadinessTests.SyntheticPageForm { Bounds = new(-20_000, -20_000, 240, 120) };
        var document = new PageReadinessTests.SyntheticDocumentControl { Dock = DockStyle.Fill, DocumentName = "合成会话" };
        // A document inside the document must never be read: the walk stops at the first
        // document boundary instead of descending into page content.
        var nestedDocument = new PageReadinessTests.SyntheticDocumentControl { Bounds = new(0, 0, 40, 20) };
        document.Controls.Add(nestedDocument);
        host.Controls.Add(document);
        host.Show();
        Application.DoEvents();
        try
        {
            using var process = Process.GetCurrentProcess();
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            var target = new PageRecoveryTarget(host.Handle.ToInt64(), (uint)Environment.ProcessId, startTicks);
            check("the synthetic page starts as an unpopulated document",
                CodexViewIdentityReader.ReadWindow(host.Handle) is { WindowCount: 1, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: not null }
                && document.RoleQueries > 0);

            var queries = document.RoleQueries;
            document.FillOnNextRoleQuery = SyntheticRoute;
            var rootType = CodexAccessibilityRecovery.RootControlType(target);
            check("asking only the root element never reaches or fills the document",
                rootType is not null && document.RoleQueries == queries && document.DocumentValue.Length == 0);

            check("a walk refuses a window whose process start does not match",
                CodexAccessibilityRecovery.WalkToDocumentBoundary(target with { ProcessStartTicks = startTicks + 1 })
                    == PageRecoveryOutcome.WindowGone);
            check("a walk refuses a dead window handle",
                CodexAccessibilityRecovery.WalkToDocumentBoundary(new PageRecoveryTarget(0x1234, (uint)Environment.ProcessId, startTicks))
                    == PageRecoveryOutcome.WindowGone);

            using var recovery = new CodexAccessibilityRecovery();
            var requested = recovery.TryRequest(target);
            check("the walk reaches the document boundary and the page fills itself",
                requested && PumpUntil(() => recovery.Snapshot().Completed == 1)
                && recovery.Snapshot() is { Outcome: PageRecoveryOutcome.DocumentReached } snapshot
                && snapshot.Milliseconds < CodexAccessibilityRecovery.TraversalBudget.TotalMilliseconds
                && document.RoleQueries > queries && document.DocumentValue == SyntheticRoute
                && nestedDocument.RoleQueries == 0);
            check("the walk applied the 250 ms native timeouts",
                CodexAccessibilityRecovery.CurrentTimeouts() is (250, 250));
            check("the page reads ready once the document filled itself",
                CodexViewIdentityReader.ReadWindow(host.Handle) is { Error: null, Title: "合成会话", ThreadId: SyntheticThreadId });

            // The production monitor on the same real read path: it must request the
            // recovery for the pending page and follow it back to the route.
            document.DocumentValue = "";
            Application.DoEvents();
            var monitorRoot = Path.Combine(directory, "recovery-monitor", "sessions");
            Directory.CreateDirectory(monitorRoot);
            using var monitor = new CodexVisibleThreadMonitor(monitorRoot, () => CodexViewIdentityReader.ReadWindow(host.Handle));
            check("the production monitor requests the walk for the pending page",
                PumpUntil(() => monitor.RecoverySnapshot() is { Completed: >= 1, Outcome: PageRecoveryOutcome.DocumentReached }));
            document.DocumentValue = SyntheticRoute;
            Application.DoEvents();
            check("the production monitor recovers the page without a restart",
                PumpUntil(() => monitor.GetStatus().ThreadId == SyntheticThreadId));

            // A window destroyed before its walk, and a large tree with no document,
            // both stay bounded and never leave the walker waiting on a page.
            IntPtr destroyedHandle;
            using (var temporary = new PageReadinessTests.SyntheticPageForm { Bounds = new(-20_000, -20_000, 160, 100) })
            {
                temporary.Show();
                Application.DoEvents();
                destroyedHandle = temporary.Handle;
            }
            check("a destroyed window is reported as gone",
                CodexAccessibilityRecovery.WalkToDocumentBoundary(new PageRecoveryTarget(destroyedHandle.ToInt64(),
                    (uint)Environment.ProcessId, startTicks)) == PageRecoveryOutcome.WindowGone);

            using (var busy = new PageReadinessTests.SyntheticPageForm { Bounds = new(-20_000, -20_000, 240, 160) })
            {
                var container = new Panel { Dock = DockStyle.Fill };
                for (var group = 0; group < 10; group++)
                {
                    var panel = new Panel { Bounds = new(0, 0, 60, 40) };
                    for (var leaf = 0; leaf < 16; leaf++) panel.Controls.Add(new Control { Bounds = new(0, 0, 4, 4) });
                    container.Controls.Add(panel);
                }
                busy.Controls.Add(container);
                busy.Show();
                Application.DoEvents();
                var started = Stopwatch.StartNew();
                var outcome = CodexAccessibilityRecovery.WalkToDocumentBoundary(new PageRecoveryTarget(busy.Handle.ToInt64(),
                    (uint)Environment.ProcessId, startTicks));
                started.Stop();
                check("a tree without a document stays bounded and reports the missing boundary",
                    outcome == PageRecoveryOutcome.BoundaryMissing && started.Elapsed < CodexAccessibilityRecovery.TraversalBudget);
            }
        }
        finally { host.Hide(); }
    }

    // A renderer that never answers, or fails outright, must stay bounded: one walk in
    // flight plus one queued, a recorded outcome, and no blocked shutdown.
    private static void StuckAndFailingWalkTests(Action<string, bool> check)
    {
        var target = new PageRecoveryTarget(0x1234, 4321, 1);
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        using (var stuck = new CodexAccessibilityRecovery(_ =>
        {
            entered.Set();
            release.Wait(10_000);
            return PageRecoveryOutcome.DocumentReached;
        }))
        {
            check("a stuck walk keeps one request in flight and one queued",
                stuck.TryRequest(target) && entered.Wait(2_000) && stuck.TryRequest(target) && !stuck.TryRequest(target));
            release.Set();
            check("queued walkers drain once the renderer answers", WaitUntil(() => stuck.Snapshot().Completed == 2));
        }

        // The walk must leave its delegate before the events the delegate waits on are
        // disposed, or the worker would die on an unhandled ObjectDisposedException.
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            var stuck = new CodexAccessibilityRecovery(_ =>
            {
                entered.Set();
                release.Wait(10_000);
                return PageRecoveryOutcome.WindowGone;
            });
            check("the stuck walk starts on the worker", stuck.TryRequest(target) && entered.Wait(2_000));
            var started = Stopwatch.StartNew();
            stuck.Dispose();
            var elapsed = started.Elapsed;
            release.Set();
            check("disposing a stuck recovery never blocks the caller", elapsed < TimeSpan.FromSeconds(1.5));
            check("the released walk still finishes after the dispose", WaitUntil(() => stuck.Snapshot().Completed == 1));
        }

        using var faulty = new CodexAccessibilityRecovery(_ => throw new InvalidOperationException("synthetic"));
        check("a failing walk is recorded as failed and the service survives",
            faulty.TryRequest(target) && WaitUntil(() => faulty.Snapshot().Completed == 1)
            && faulty.Snapshot().Outcome == PageRecoveryOutcome.Failed
            && faulty.TryRequest(target) && WaitUntil(() => faulty.Snapshot().Completed == 2));
    }
}

