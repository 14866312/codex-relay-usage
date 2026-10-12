using System.Diagnostics;

namespace CodexTokenOverlay;

// Codex surfaces its Chromium document before the lazily built accessibility tree
// fills in the URL; the pending classification only needs an empty-URL document while
// no document carries a usable URL, so a fresh window can expose that state for a
// while. These checks pin the pending state, the delayed wording and the binding
// recovery across a short gap, on synthetic offscreen windows and injected clocks, so
// the regression never depends on a personal Codex session.
internal static class PageReadinessTests
{
    internal static void Run(Action<string, bool> check, string directory)
    {
        check("a page whose route is not exposed yet is pending, not ambiguous",
            CodexViewIdentityReader.DocumentReadError(0, 1) == CodexViewIdentityReader.PagePendingError &&
            CodexViewIdentityReader.DocumentReadError(0, 0) == "无法唯一识别当前页面" &&
            CodexViewIdentityReader.DocumentReadError(2, 1) == "无法唯一识别当前页面");

        using var host = new SyntheticPageForm { Bounds = new(-20_000, -20_000, 240, 120) };
        var document = new SyntheticDocumentControl { Dock = DockStyle.Fill, DocumentName = "合成会话" };
        host.Controls.Add(document);
        host.Show();
        Application.DoEvents();
        try
        {
            var pending = CodexViewIdentityReader.ReadWindow(host.Handle);
            check("unpopulated accessibility document reports the pending page",
                pending is { WindowCount: 1, Error: "当前页面尚未就绪", Title: null, ThreadId: null }
                && pending.Lifecycle is not null);

            document.DocumentValue = "app://-/threads/11111111-2222-4333-8444-555555555555";
            Application.DoEvents();
            var ready = CodexViewIdentityReader.ReadWindow(host.Handle);
            check("populated accessibility document exposes title and route",
                ready is { WindowCount: 1, Error: null, Title: "合成会话" } &&
                ready.ThreadId == "11111111-2222-4333-8444-555555555555");
        }
        finally { host.Hide(); }

        var root = Path.Combine(directory, "page-readiness", "sessions");
        Directory.CreateDirectory(root);
        using var monitor = new CodexVisibleThreadMonitor(root, () => new(1, null, Error: CodexViewIdentityReader.PagePendingError));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (monitor.GetStatus().LastError != CodexViewIdentityReader.PagePendingError && DateTime.UtcNow < deadline) Thread.Sleep(5);
        var error = monitor.IdentifyVisibleAsync().GetAwaiter().GetResult();
        check("identify on a pending page names the page state instead of the sidebar",
            error == "当前页面尚未就绪，请稍后重新点击识别" && monitor.GetStatus().ThreadId is null);

        WaitStageTests(check);
        PendingWaitTests(check, directory);
        BindingRecoveryTests(check);
    }

    private static long TicksOf(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    private static ActiveThreadRouteStatus WaitFor(CodexVisibleThreadMonitor monitor,
        Func<ActiveThreadRouteStatus, bool> condition, int timeout = 2_000)
    {
        var deadline = Environment.TickCount64 + timeout;
        ActiveThreadRouteStatus status;
        do { Thread.Sleep(5); status = monitor.GetStatus(); }
        while (!condition(status) && Environment.TickCount64 < deadline);
        return status;
    }

    private static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline) Thread.Sleep(5);
    }

    // The strip clears numbers immediately but delays the wording: a short blip must
    // not flash "not ready", and only a long wait suggests a restart.
    private static void WaitStageTests(Action<string, bool> check)
    {
        const long start = 1_000;
        var pending = new ActiveThreadRouteStatus(null, 1, false, 2, CodexViewIdentityReader.PagePendingError,
            PagePendingSince: start);
        check("a pending page reads as waiting before any threshold",
            PageWaitRules.Stage(pending, start) == PageWaitStage.Reading
            && PageWaitRules.Stage(pending, start + TicksOf(0.2)) == PageWaitStage.Reading
            && PageWaitRules.Stage(pending, start + TicksOf(2)) == PageWaitStage.Reading);
        check("the not-ready wording appears at three seconds",
            PageWaitRules.Stage(pending, start + TicksOf(3)) == PageWaitStage.NotReady
            && PageWaitRules.Stage(pending, start + TicksOf(59)) == PageWaitStage.NotReady);
        check("the restart hint appears at one minute",
            PageWaitRules.Stage(pending, start + TicksOf(60)) == PageWaitStage.LongWait);
        check("stage wording matches the strip texts",
            PageWaitRules.Text(PageWaitStage.Reading) == "正在读取当前对话…"
            && PageWaitRules.Text(PageWaitStage.NotReady) == "当前页面尚未就绪"
            && PageWaitRules.Text(PageWaitStage.LongWait) == "等待较久，可尝试重启 Codex"
            && PageWaitRules.Text(PageWaitStage.None) is null);
        check("a ready page never shows a wait stage",
            PageWaitRules.Stage(new ActiveThreadRouteStatus("a", 1, true, 3, null), start) == PageWaitStage.None);
        check("follow status uses the wait stage for a pending page",
            FollowSelection.Status(pending, null, PageWaitStage.Reading) == "正在读取当前对话…"
            && FollowSelection.Status(pending, null, PageWaitStage.NotReady) == "当前页面尚未就绪"
            && FollowSelection.Status(pending, null, PageWaitStage.LongWait) == "等待较久，可尝试重启 Codex");
        check("manual lock and debug-route disconnects ignore the wait stage",
            FollowSelection.Status(pending, "manual", PageWaitStage.LongWait) == "手动锁定"
            && FollowSelection.Status(pending with { Identification = "页面唯一 ID", LastError = "自动连接已断开，正在重连" },
                null, PageWaitStage.LongWait) == "自动连接已断开，正在重连");
        check("other read errors keep their immediate wording",
            FollowSelection.Status(pending with { LastError = "无法唯一识别当前页面", PagePendingSince = 0 },
                null, PageWaitStage.None) == "无法唯一识别当前页面");
    }

    // The wait start, the window generation and the immediate clearing of numbers are
    // driven through the production monitor with an injected monotonic clock.
    private static void PendingWaitTests(Action<string, bool> check, string directory)
    {
        var root = Path.Combine(directory, "page-wait", "sessions");
        Directory.CreateDirectory(root);
        var clock = Stopwatch.GetTimestamp();
        var view = new CodexViewIdentity(1, null, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: "window-one");
        using var monitor = new CodexVisibleThreadMonitor(root, () => view, clock: () => clock);
        var first = WaitFor(monitor, status => status.PagePendingSince != 0);
        check("the first pending read starts the wait on the injected monotonic clock",
            first.PagePendingSince == clock && first.ThreadId is null && first.ActiveWindowCount == 1);
        clock += TicksOf(2);
        Pump(400);
        var sampled = monitor.GetStatus();
        check("unchanged sampling keeps one wait start instead of restarting it",
            sampled.PagePendingSince == first.PagePendingSince && sampled.PageGeneration == first.PageGeneration);

        view = view with { Lifecycle = "window-two" };
        var rebuilt = WaitFor(monitor, status => status.PageGeneration > first.PageGeneration);
        check("a rebuilt window lifetime restarts the wait for the empty document",
            rebuilt.PagePendingSince == clock && rebuilt.PageLifecycle == "window-two");

        clock += TicksOf(1);
        monitor.SetRoot(Path.Combine(directory, "page-wait", "other", "sessions"));
        var moved = WaitFor(monitor, status => status.PagePendingSince != 0 && status.PagePendingSince != rebuilt.PagePendingSince);
        check("a log-directory change restarts the wait", moved.PagePendingSince == clock);

        view = new CodexViewIdentity(1, "合成会话", "11111111-2222-4333-8444-555555555555", Lifecycle: "window-two");
        var ready = WaitFor(monitor, status => status.ThreadId is not null);
        check("a ready page clears the wait and resolves the route",
            ready.PagePendingSince == 0 && ready.IsConnected && ready.ThreadId == "11111111-2222-4333-8444-555555555555");

        var selection = new SessionSelection();
        var route = new ActiveThreadRouteStatus("test", 1, true, 1, null);
        selection.Observe(route, null, 0);
        var request = selection.Request();
        var cost = SessionCostTests.SampleResult();
        var snapshot = DiagnosticsRunner.Example() with { ThreadId = "test",
            Ledger = new("test", cost.LedgerId, cost.LedgerVersion, [], null, new(null, null, null, null), false, []) };
        var group = new SessionGroupSnapshot("test", request.Revision, 0, cost.PricingVersion, 1, [], []);
        check("results publish while the page is identified", selection.TryPublish(request, snapshot, null)
            && CostPublication.Matches(request, selection.Revision, snapshot, cost.PricingVersion, cost)
            && SessionGroupPublication.Matches(group, "test", selection.Revision, 0, cost.PricingVersion));
        var pendingRoute = new ActiveThreadRouteStatus(null, 1, false, 2, CodexViewIdentityReader.PagePendingError,
            PagePendingSince: 5_000);
        check("the first pending read clears tokens and bumps the selection generation",
            selection.Observe(pendingRoute, null, 0) && selection.ThreadId is null && selection.Snapshot is null);
        check("late token, cost and related results cannot land on the pending page",
            !selection.TryPublish(request, snapshot, null)
            && !CostPublication.Matches(request, selection.Revision, snapshot, cost.PricingVersion, cost)
            && !SessionGroupPublication.Matches(group, "test", selection.Revision, 0, cost.PricingVersion));
        check("the wording can advance to not-ready without another selection generation",
            PageWaitRules.Stage(pendingRoute, 5_000 + TicksOf(5)) == PageWaitStage.NotReady
            && !selection.Observe(pendingRoute, null, 0));
        var frame = new OverlayPresentationState(null, null, pendingRoute, null, null, DisplayField.Total,
            DisplayField.CacheHitRate, DisplayField.Total | DisplayField.CacheHitRate, false, PageWaitStage.Reading);
        check("the presentation cache repaints when only the wait stage moves",
            frame != frame with { Stage = PageWaitStage.NotReady }
            && frame == new OverlayPresentationState(null, null, pendingRoute, null, null, DisplayField.Total,
                DisplayField.CacheHitRate, DisplayField.Total | DisplayField.CacheHitRate, false, PageWaitStage.Reading));

        // A renderer that stops answering keeps the immediate timeout rule.
        var blockedRoot = Path.Combine(directory, "page-wait-blocked", "sessions");
        Directory.CreateDirectory(blockedRoot);
        var reads = 0;
        using var blocked = new CodexVisibleThreadMonitor(blockedRoot, () =>
        {
            if (Interlocked.Increment(ref reads) > 1) Thread.Sleep(1_400);
            return new CodexViewIdentity(1, "合成会话", "11111111-2222-4333-8444-555555555555", Lifecycle: "window");
        });
        var blockedStatus = WaitFor(blocked, status => status.LastError == "当前页面读取超时", 3_000);
        check("a blocked renderer reports the timeout state without a pending wait",
            blockedStatus.ThreadId is null && blockedStatus.LastError == "当前页面读取超时" && blockedStatus.PagePendingSince == 0);
    }

    // A short pending gap keeps the native-row binding as a candidate; it is restored
    // only when document, row, title and project all still match.
    private static void BindingRecoveryTests(Action<string, bool> check)
    {
        const string title = "相同的完整会话标题";
        var projects = new LocalProjectIndex();
        var titles = new Dictionary<string, string> { ["a"] = title, ["b"] = title };
        var pending = new CodexViewIdentity(1, null, Error: CodexViewIdentityReader.PagePendingError, Lifecycle: "window");
        var clock = 0L;
        CodexViewIdentity View(int row, string owner = "owner") =>
            new(1, title, DocumentKey: owner, Sidebar: new(owner, new[] { new SidebarRowIdentity(row, title, null) }));
        var resolver = new SidebarSessionResolver(() => clock);
        check("same-title rows stay ambiguous before binding", resolver.Resolve(View(10), titles, projects).ThreadId is null);
        check("explicit binding identifies the current row", resolver.TryBind(View(10), "a", titles, projects, out _)
            && resolver.Resolve(View(10), titles, projects) is { ThreadId: "a", Method: "侧栏绑定" });
        clock += TicksOf(0.2);
        check("a short pending gap keeps the page unidentified without numbers",
            resolver.Resolve(pending, titles, projects) is { ThreadId: null, Error: CodexViewIdentityReader.PagePendingError });
        clock += TicksOf(2);
        check("the same row after a two-second gap restores the binding",
            resolver.Resolve(View(10), titles, projects) is { ThreadId: "a", Method: "侧栏绑定" });

        clock += TicksOf(0.5);
        resolver.Resolve(pending, titles, projects);
        check("a rebuilt row does not inherit the old binding", resolver.Resolve(View(11), titles, projects).ThreadId is null);

        resolver = new SidebarSessionResolver(() => clock);
        resolver.TryBind(View(10), "a", titles, projects, out _);
        clock += TicksOf(0.5);
        resolver.Resolve(pending, titles, projects);
        clock += TicksOf(3.5);
        resolver.Resolve(pending, titles, projects);
        check("a gap beyond three seconds discards the binding candidate",
            resolver.Resolve(View(10), titles, projects).ThreadId is null);

        resolver = new SidebarSessionResolver(() => clock);
        resolver.TryBind(View(10), "a", titles, projects, out _);
        resolver.Resolve(pending, titles, projects);
        check("a different document after the gap cannot reuse the binding",
            resolver.Resolve(View(10, owner: "other"), titles, projects).ThreadId is null);

        resolver = new SidebarSessionResolver(() => clock);
        resolver.TryBind(View(10), "a", titles, projects, out _);
        resolver.Reset();
        check("a log root reset drops the candidate immediately", resolver.Resolve(View(10), titles, projects).ThreadId is null);
    }

    // Shared with the recovery checks: the synthetic page records every role query so
    // a check can tell which call path actually reached the document node.
    internal sealed class SyntheticPageForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    internal sealed class SyntheticDocumentControl : Control
    {
        internal string DocumentName = "";
        internal string DocumentValue = "";
        internal int RoleQueries;
        internal string? FillOnNextRoleQuery;
        protected override AccessibleObject CreateAccessibilityInstance() => new SyntheticDocumentAccessible(this);
        private sealed class SyntheticDocumentAccessible(SyntheticDocumentControl owner) : ControlAccessibleObject(owner)
        {
            public override string? Name { get => owner.DocumentName; set { } }
            public override string? Value { get => owner.DocumentValue; set => owner.DocumentValue = value ?? ""; }
            public override AccessibleRole Role
            {
                get
                {
                    owner.RoleQueries++;
                    if (owner.FillOnNextRoleQuery is { } route)
                    {
                        owner.FillOnNextRoleQuery = null;
                        owner.DocumentValue = route;
                    }
                    return AccessibleRole.Document;
                }
            }
        }
    }
}
