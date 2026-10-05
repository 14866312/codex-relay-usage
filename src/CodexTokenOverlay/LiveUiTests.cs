using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexTokenOverlay;

internal sealed record LatencySummary(int Samples, double MedianMilliseconds, double P95Milliseconds, double MaximumMilliseconds);
internal sealed record LiveUiProbeResult(int Passed, int Failed, IReadOnlyList<DiagnosticsRunner.TestResult> Results,
    LatencySummary LogAppendToPublication, LatencySummary HostMoveToOverlay);

internal static class LiveUiTests
{
    internal static LiveUiProbeResult Run()
    {
        var checks = new List<DiagnosticsRunner.TestResult>();
        void Check(string name, bool ok) => checks.Add(new(name, ok, ok ? null : "assertion failed"));
        var logTimes = new List<double>(); var moveTimes = new List<double>();
        var directory = Path.Combine(Path.GetTempPath(), "CodexRelayUsage-live-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            LogNotifications(Check, directory, logTimes);
            WindowEvents(Check, moveTimes);
        }
        catch (Exception error) { checks.Add(new("unhandled live UI exception", false, error.ToString())); }
        finally { Directory.Delete(directory, true); }
        return new(checks.Count(c => c.Passed), checks.Count(c => !c.Passed), checks, Summary(logTimes), Summary(moveTimes));
    }

    private static void LogNotifications(Action<string, bool> check, string directory, List<double> times)
    {
        var home = Path.Combine(directory, "home"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "live.jsonl"); var utf8 = new UTF8Encoding(false);
        File.WriteAllText(path, LiveUsageTests.Meta + LiveUsageTests.Started + LiveUsageTests.Context, utf8);
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = OverlaySettings.CreateDefault(); settings.PinnedThreadId = "live"; settings.Save(settingsPath);
        using var context = new OverlayContext(home, settingsPath, useSavedRoot: false);
        context.StartNotificationsOnlyProbe();
        check("production controller reads initial synthetic turn", PumpUntil(() => context.ReadLiveProbeSnapshot() is not null));
        check("ongoing empty turn has no invented count", context.ReadLiveProbeSnapshot() is { TurnInProgress: true, UsageRecorded: false });
        for (var i = 1; i <= 12; i++)
        {
            var u = new UsageAmounts(100, 40, 0, 10, 5, 110);
            var total = new UsageAmounts(100 * i, 40 * i, 0, 10 * i, 5 * i, 110 * i);
            var watch = Stopwatch.StartNew();
            File.AppendAllText(path, LiveUsageTests.Record("live-" + i, u, total), utf8);
            var published = PumpUntil(() => context.ReadLiveProbeSnapshot()?.TotalTokens == 110 * i);
            watch.Stop(); if (published) times.Add(watch.Elapsed.TotalMilliseconds);
            check("notification-only running call " + i + " publishes without a legacy snapshot", published && context.ReadLiveProbeSnapshot()!.TurnInProgress);
        }
        var before = context.ReadLiveProbeSnapshot();
        File.AppendAllText(path, LiveUsageTests.Record("live-12", new(100, 40, 0, 10, 5, 110), new(1200, 480, 0, 120, 60, 1320)), utf8);
        PumpFor(40);
        check("duplicate watcher notifications cannot add the same usage again", context.ReadLiveProbeSnapshot()?.TotalTokens == 1320 && context.ReadLiveProbeSnapshot()?.Ledger?.Calls.Count == 12);
        var partial = LiveUsageTests.Record("live-13", new(100, 40, 0, 10, 5, 110), new(1300, 520, 0, 130, 65, 1430));
        File.AppendAllText(path, partial[..(partial.Length / 2)], utf8); PumpFor(40);
        check("notification on half a row retains valid totals", context.ReadLiveProbeSnapshot()?.TotalTokens == before?.TotalTokens);
        File.AppendAllText(path, partial[(partial.Length / 2)..], utf8);
        check("finishing half a row triggers publication with the poll timer stopped", PumpUntil(() => context.ReadLiveProbeSnapshot()?.TotalTokens == 1430));
        File.AppendAllText(path, LiveUsageTests.Row("event_msg", new { type = "task_complete", turn_id = "turn" }), utf8);
        check("completion notification clears running state without adding tokens", PumpUntil(() => context.ReadLiveProbeSnapshot() is { TurnInProgress: false, TotalTokens: 1430 }));
        check("all log-to-publication latency samples collected", times.Count == 12);
    }

    private static void WindowEvents(Action<string, bool> check, List<double> times)
    {
        using var host = new ProbeHostForm { Bounds = new(-20_000, -20_000, 1000, 700) };
        using var unrelated = new ProbeHostForm { Bounds = new(-25_000, -25_000, 900, 600) };
        using var strip = new TokenStripForm(motionEnabled: true);
        var presentation = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example() with { TurnInProgress = true },
            DisplayField.Total, DisplayField.CacheHitRate, OverlaySettings.CreateDefault().VisibleFields);
        strip.SetPresentation(presentation);
        host.Show(); unrelated.Show(); Application.DoEvents();
        var handle = host.Handle; GetWindowThreadProcessId(handle, out var processId);
        var workArea = IntRect.FromRectangle(Screen.FromHandle(handle).WorkingArea);
        var target = new CodexWindowTarget(new(handle, IntRect.FromRectangle(host.Bounds), IntRect.FromRectangle(host.Bounds),
            null, workArea, (uint)host.DeviceDpi, new(46, 30, 8, 8, 4)));
        CodexWindowLocator.RememberConfirmedTarget(target, processId);
        // Keep real monitor geometry for locator validation; let offscreen test windows
        // lay out in a test area without activating or covering the user's desktop.
        OverlayLayoutResult Layout(bool expanded = false) => OverlayLayoutCalculator.Calculate(new(target.HostWindow with
            { WorkingArea = new(-40_000, -40_000, 60_000, 60_000) }, AnchorMode.InsideTopRight,
            expanded, presentation.ExpandedRows.Count, true));
        strip.ApplyLayout(Layout()); strip.Show(); Application.DoEvents();
        var moveEvents = 0; var visibilityEvents = 0; var destroyed = false;
        using var events = new NativeWindowEvents(change =>
        {
            if (change == HostWindowEvent.Location) moveEvents++;
            if (change == HostWindowEvent.Visibility) visibilityEvents++;
            if (change == HostWindowEvent.Destroyed) { destroyed = true; strip.Hide(); return; }
            if (change == HostWindowEvent.Foreground) return;
            if (CodexWindowLocator.TryRefreshKnownGeometry(target, requireForeground: false, out var refreshed))
            { target = refreshed; strip.ApplyLayout(Layout(strip.CurrentLayout?.State == OverlayVisualState.Expanded)); if (!strip.Visible) strip.Show(); }
            else strip.Hide();
        }, skipOwnProcess: false);
        events.Watch(handle, processId);
        check("native location hook installed for synthetic host", events.HasLocationHook);
        var originalRegion = strip.Region; var initialForeground = GetForegroundWindow();
        var originalDelta = new Point(strip.Left - host.Left, strip.Top - host.Top);
        for (var i = 1; i <= 24; i++)
        {
            var expected = new Point(-20_000 + i * 9, -20_000 + i * 5);
            var countBefore = moveEvents; var watch = Stopwatch.StartNew(); host.Location = expected;
            var moved = PumpUntil(() => moveEvents > countBefore && strip.Left == expected.X + originalDelta.X && strip.Top == expected.Y + originalDelta.Y);
            watch.Stop(); if (moved) times.Add(watch.Elapsed.TotalMilliseconds);
            check("native host translation " + i + " follows without a position poll timer", moved);
        }
        check("translation retains client region instead of rebuilding shape", ReferenceEquals(originalRegion, strip.Region));
        check("following a move preserves foreground focus", GetForegroundWindow() == initialForeground);
        var count = moveEvents; unrelated.Left += 40; PumpFor(40);
        check("unrelated process-window locations do not move watched overlay", moveEvents == count);
        var invalidations = 0; strip.Invalidated += (_, _) => invalidations++;
        strip.SetPresentation(presentation with { ExpandedRows = presentation.ExpandedRows.ToArray() });
        check("equivalent presentation does not schedule another repaint", invalidations == 0);
        var oldWidth = target.HostWindow.WindowBounds.Width; host.Width += 120;
        check("resize refreshes live host dimensions", PumpUntil(() => target.HostWindow.WindowBounds.Width == oldWidth + 120));
        strip.ApplyLayout(Layout(expanded: true)); strip.FinishFeedbackAnimation();
        var expandedRegion = strip.Region; var expandedState = strip.FeedbackFrame;
        host.Left += 20;
        check("expanded panel moves with the capsule and stays open", PumpUntil(() => strip.Bounds == Layout(expanded: true).WindowBounds.ToRectangle()) && strip.CurrentLayout!.State == OverlayVisualState.Expanded);
        check("translation retains expanded region and feedback state", ReferenceEquals(expandedRegion, strip.Region) && strip.FeedbackFrame.Open == expandedState.Open && strip.FeedbackFrame.PanelReveal == 1);
        host.WindowState = FormWindowState.Minimized;
        check("minimize event hides overlay without waiting for locator tick", PumpUntil(() => !strip.Visible));
        check("minimized host fails fast geometry validation", !CodexWindowLocator.TryRefreshKnownGeometry(target, false, out _));
        host.WindowState = FormWindowState.Normal;
        check("restore event resumes visible attachment", PumpUntil(() => strip.Visible));
        host.Hide(); check("hide event hides attachment immediately", PumpUntil(() => !strip.Visible));
        host.Show(); check("show event restores attachment", PumpUntil(() => strip.Visible));
        check("native visibility events were observed", visibilityEvents >= 4);
        check("fast path refuses a confirmed-PID mismatch", RejectDifferentPid(target, processId));
        host.Close(); check("destroy event hides and invalidates old host", PumpUntil(() => destroyed && !strip.Visible));
        check("destroyed handle cannot pass fast geometry refresh", !CodexWindowLocator.TryRefreshKnownGeometry(target, false, out _));
        check("native callback completed without exception", events.LastError is null);
        check("all host movement latency samples collected", times.Count == 24);
        check("observed synthetic host-follow P95 below 100 ms", times.Count == 24 && Summary(times).P95Milliseconds < 100);
        events.Dispose(); var afterDispose = moveEvents; unrelated.Top += 15; PumpFor(20);
        check("disposed hooks stop delivering callbacks", moveEvents == afterDispose);
        strip.Hide(); unrelated.Hide();
    }

    private static bool RejectDifferentPid(CodexWindowTarget target, uint processId)
    {
        var mismatch = new CodexWindowTarget(target.HostWindow); CodexWindowLocator.RememberConfirmedTarget(mismatch, processId + 1);
        return !CodexWindowLocator.TryRefreshKnownGeometry(mismatch, false, out _);
    }
    private static bool PumpUntil(Func<bool> condition, int timeout = 2000)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); if (condition()) return true; Thread.Sleep(1); } while (watch.ElapsedMilliseconds < timeout);
        return condition();
    }
    private static void PumpFor(int duration)
    { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < duration) { Application.DoEvents(); Thread.Sleep(1); } }
    private static LatencySummary Summary(List<double> values)
    {
        if (values.Count == 0) return new(0, 0, 0, 0);
        var sorted = values.Order().ToArray();
        double Percentile(double p) => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)], 3);
        return new(sorted.Length, Percentile(.5), Percentile(.95), Math.Round(sorted[^1], 3));
    }
    private sealed class ProbeHostForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
