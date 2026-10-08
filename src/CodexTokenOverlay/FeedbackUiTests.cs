using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal static class FeedbackUiTests
{
    internal static DiagnosticsRunner.SelfTestResult Run(string? previewDirectory = null)
    {
        var checks = new List<DiagnosticsRunner.TestResult>();
        void Check(string name, bool ok) => checks.Add(new(name, ok, ok ? null : "assertion failed"));
        try
        {
            foreach (var theme in new[] { OverlayThemeKind.Light, OverlayThemeKind.Dark })
            {
                using var form = new TokenStripForm(motionEnabled: true);
                var presentation = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example(), DisplayField.Total, DisplayField.CacheHitRate,
                    OverlaySettings.CreateDefault().VisibleFields) with { FollowText = "自动跟随" };
                form.SetPresentation(presentation); form.ApplyTheme(OverlayThemePalette.For(theme));
                var host = TitleBarPlacementTests.Host((uint)form.DeviceDpi);
                OverlayLayoutResult Layout(bool expanded)
                {
                    var layout = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.TitleBarTopRight, expanded, presentation.ExpandedRows.Count, true));
                    // Send messages only to this synthetic offscreen window, without moving the real cursor.
                    return layout with { WindowBounds = layout.WindowBounds with { X = -30_000 - layout.CapsuleBounds.X, Y = -30_000 - layout.CapsuleBounds.Y } };
                }
                var collapsed = Layout(false); var expanded = Layout(true);
                form.ApplyLayout(collapsed); form.Show(); Application.DoEvents();
                var handle = form.Handle;
                var capsule = collapsed.CapsuleBounds; var inside = new Point(capsule.X + capsule.Width / 2, capsule.Y + capsule.Height / 2);
                var outside = new Point(capsule.Right + 20, capsule.Bottom + 20);
                var clicks = 0;
                form.CapsuleClicked += (_, _) => { clicks++; form.ApplyLayout(form.CurrentLayout!.State == OverlayVisualState.Collapsed ? expanded : collapsed); };
                Check(theme + " no-activate click policy", SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3);
                var originalBounds = form.Bounds;
                using var normal = Capture(form);
                Send(handle, 0x0202, inside);
                Check(theme + " unmatched release does not open", clicks == 0 && form.CurrentLayout!.State == OverlayVisualState.Collapsed);
                Send(handle, 0x02A3, Point.Empty); form.FinishFeedbackAnimation();
                Send(handle, 0x0200, inside);
                Check(theme + " hover starts brief animation and hand cursor", form.IsFeedbackTimerRunning && form.Cursor == Cursors.Hand);
                form.FinishFeedbackAnimation();
                using var hover = Capture(form);
                Check(theme + " hover visibly changes surface without moving window", PixelsDiffer(normal, hover) && form.Bounds == originalBounds);
                Check(theme + " idle stops animation timer", !form.IsFeedbackTimerRunning);
                Send(handle, 0x0201, inside, 1);
                using var pressed = Capture(form);
                Check(theme + " mouse down is visible before opening", form.IsPointerPressed && form.Capture && PixelsDiffer(hover, pressed) && clicks == 0);
                SaveStates(previewDirectory, theme, normal, hover, pressed);
                Send(handle, 0x0200, outside, 1);
                Check(theme + " dragging outside removes pressed appearance", form.FeedbackFrame.Press == 0);
                Send(handle, 0x0202, outside); form.FinishFeedbackAnimation();
                Check(theme + " outside release cancels click and capture", clicks == 0 && !form.IsPointerPressed && !form.Capture);
                Send(handle, 0x0201, inside, 1); Send(handle, 0x0202, inside);
                Check(theme + " native down-up toggles exactly once", clicks == 1 && form.CurrentLayout!.State == OverlayVisualState.Expanded);
                Check(theme + " expansion begins partially revealed", form.FeedbackFrame.PanelReveal < 1 && form.IsFeedbackTimerRunning);
                var panel = expanded.PanelBounds;
                var bottom = new Point(panel.X + panel.Width / 2, panel.Bottom - 2);
                Check(theme + " unrevealed panel does not intercept pointer", !form.Region!.IsVisible(bottom));
                PumpUntilIdle(form);
                Check(theme + " opening animation completes and timer stops", form.FeedbackFrame.PanelReveal == 1 && form.FeedbackFrame.Open == 1 && !form.IsFeedbackTimerRunning);
                Check(theme + " revealed panel receives pointer", form.Region!.IsVisible(bottom));
                if (previewDirectory is not null) Save(form, Path.Combine(previewDirectory, theme.ToString().ToLowerInvariant() + "-toolbar-open.png"));
                var openCapsule = expanded.CapsuleBounds;
                var openInside = new Point(openCapsule.X + openCapsule.Width / 2, openCapsule.Y + openCapsule.Height / 2);
                Send(handle, 0x02A3, Point.Empty); form.FinishFeedbackAnimation();
                Check(theme + " moving to details keeps open state", form.FeedbackFrame.Hover == 0 && form.FeedbackFrame.Open == 1);
                Send(handle, 0x0201, openInside, 1); Send(handle, 0x0202, openInside); form.FinishFeedbackAnimation();
                Check(theme + " second click closes and clears panel region", clicks == 2 && form.CurrentLayout!.State == OverlayVisualState.Collapsed && form.FeedbackFrame.PanelReveal == 0);
                Send(handle, 0x0201, inside, 1); form.Capture = false;
                Send(handle, 0x0202, inside);
                Check(theme + " capture loss cancels unfinished click", clicks == 2 && !form.IsPointerPressed);
                Send(handle, 0x0201, inside, 1); form.Hide();
                Check(theme + " hiding clears pointer and timer", !form.IsPointerPressed && !form.Capture && form.FeedbackFrame.Hover == 0 && !form.IsFeedbackTimerRunning);
                form.Show(); Send(handle, 0x0202, inside);
                Check(theme + " showing again cannot deliver stale click", clicks == 2);
                form.ApplyLayout(expanded); form.FinishFeedbackAnimation();
                var detailsClicks = 0; form.CostDetailsRequested += (_, _) => detailsClicks++;
                var detailsButton = form.DetailsButtonBounds;
                var detailPoint = new Point(detailsButton.Left + detailsButton.Width / 2, detailsButton.Top + detailsButton.Height / 2);
                Send(handle, 0x0202, detailPoint);
                Check(theme + " details button ignores unmatched release", detailsClicks == 0);
                Send(handle, 0x0200, detailPoint); using var detailHover = Capture(form);
                Send(handle, 0x0201, detailPoint, 1); using var detailPressed = Capture(form);
                Check(theme + " details button shows press before action", form.Capture && PixelsDiffer(detailHover, detailPressed) && detailsClicks == 0);
                Send(handle, 0x0200, outside, 1); Send(handle, 0x0202, outside);
                Check(theme + " details outside release cancels action", detailsClicks == 0 && !form.Capture);
                Send(handle, 0x0201, detailPoint, 1); form.Capture = false; Send(handle, 0x0202, detailPoint);
                Check(theme + " details capture loss cancels action", detailsClicks == 0);
                Send(handle, 0x0201, detailPoint, 1); Send(handle, 0x0202, detailPoint); Send(handle, 0x0202, detailPoint);
                Check(theme + " details button opens exactly once", detailsClicks == 1 && clicks == 2);
                var close = form.PanelCloseBounds; var closePoint = new Point(close.Left + close.Width / 2, close.Top + close.Height / 2);
                Send(handle, 0x0201, closePoint, 1); Send(handle, 0x0202, closePoint); form.FinishFeedbackAnimation();
                Check(theme + " panel close button collapses exactly once", clicks == 3 && form.CurrentLayout!.State == OverlayVisualState.Collapsed);
                form.Hide();
            }
            using var reduced = new TokenStripForm(motionEnabled: false);
            var reducedPresentation = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example(), DisplayField.Total, DisplayField.CacheHitRate, OverlaySettings.CreateDefault().VisibleFields);
            reduced.SetPresentation(reducedPresentation);
            var reducedLayout = OverlayLayoutCalculator.Calculate(new(TitleBarPlacementTests.Host((uint)reduced.DeviceDpi), AnchorMode.TitleBarTopRight, false, reducedPresentation.ExpandedRows.Count, true));
            reduced.ApplyLayout(reducedLayout with { WindowBounds = reducedLayout.WindowBounds with { X = -32_000, Y = -32_000 } });
            reduced.Show();
            Send(reduced.Handle, 0x0200, new(reducedLayout.CapsuleBounds.Width / 2, reducedLayout.CapsuleBounds.Height / 2));
            Check("reduced motion retains immediate hover without animation", reduced.FeedbackFrame.Hover == 1 && !reduced.IsFeedbackTimerRunning);
            reduced.Hide();

            // Deterministic timing verifies intermediate hover, reversal and rapid open/close.
            var feedback = new OverlayFeedback();
            feedback.Move(true, 1000, true); feedback.Advance(1040);
            Check("hover has intermediate eased frames", feedback.Frame.Hover > 0 && feedback.Frame.Hover < 1);
            var beforeReversal = feedback.Frame.Hover; feedback.Move(false, 1040, true);
            Check("reversing hover starts from current frame", feedback.Frame.Hover == beforeReversal);
            feedback.Advance(1200); Check("hover reversal settles and stops", feedback.Frame.Hover == 0 && !feedback.IsAnimating);
            feedback.SetExpanded(true, 1300, true); feedback.Advance(1340);
            feedback.SetExpanded(false, 1340, true); feedback.SetExpanded(true, 1350, true); feedback.Advance(1600);
            Check("rapid close-reopen reaches final open state", feedback.Frame.Open == 1 && feedback.Frame.PanelReveal == 1 && !feedback.IsAnimating);
        }
        catch (Exception ex) { checks.Add(new("unhandled feedback UI exception", false, ex.ToString())); }
        return new(checks.Count(c => c.Passed), checks.Count(c => !c.Passed), checks);
    }

    private static void PumpUntilIdle(TokenStripForm form)
    {
        var deadline = Environment.TickCount64 + 1500;
        while (form.IsFeedbackTimerRunning && Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static Bitmap Capture(TokenStripForm form)
    {
        using var raw = new Bitmap(form.Width, form.Height); form.DrawToBitmap(raw, new(0, 0, raw.Width, raw.Height));
        var shaped = new Bitmap(raw.Width, raw.Height);
        using var graphics = Graphics.FromImage(shaped); graphics.Clear(Color.Transparent);
        if (form.Region is not null) graphics.Clip = form.Region;
        graphics.DrawImageUnscaled(raw, 0, 0); return shaped;
    }

    private static bool PixelsDiffer(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return true;
        for (var y = 2; y < a.Height - 2; y += 3)
        for (var x = 2; x < a.Width - 2; x += 3)
            if (a.GetPixel(x, y) != b.GetPixel(x, y)) return true;
        return false;
    }

    private static void Save(TokenStripForm form, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var bitmap = Capture(form); bitmap.Save(path, ImageFormat.Png);
    }

    private static void SaveStates(string? directory, OverlayThemeKind theme, params Bitmap[] frames)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        const int padding = 24, labelHeight = 28;
        var width = frames.Max(f => f.Width) + padding * 2;
        var rowHeight = frames.Max(f => f.Height) + padding + labelHeight;
        using var canvas = new Bitmap(width, rowHeight * frames.Length + padding);
        using var graphics = Graphics.FromImage(canvas); graphics.Clear(OverlayThemePalette.For(theme).ToolbarSurface);
        using var font = new Font("Microsoft YaHei UI", 10, GraphicsUnit.Point);
        using var brush = new SolidBrush(OverlayThemePalette.For(theme).Label);
        var labels = new[] { "默认", "鼠标悬停", "按住鼠标" };
        for (var i = 0; i < frames.Length; i++)
        {
            graphics.DrawString(labels[i], font, brush, padding, padding + rowHeight * i);
            graphics.DrawImageUnscaled(frames[i], padding, padding + rowHeight * i + labelHeight);
        }
        canvas.Save(Path.Combine(directory, theme.ToString().ToLowerInvariant() + "-toolbar-states.png"), ImageFormat.Png);
    }

    private static void Send(IntPtr handle, int message, Point point, int buttons = 0) =>
        SendMessage(handle, message, new IntPtr(buttons), new IntPtr((point.Y << 16) | (point.X & 0xffff)));

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
