using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class DiagnosticsRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static bool TryRun(IReadOnlyList<string> args, string root)
    {
        if (args.Count < 2) return false;
        if (args[0] == "--clipboard-ui-probe")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            var result = ClipboardUiTests.Run();
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1; return true;
        }
        if (args[0] == "--debug-route-probe" && args.Count > 2 && int.TryParse(args[2], out var processId))
        {
            using var reader = new CodexDebugRouteReader();
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var result = reader.ReadProcess(processId, $"probe/{processId}/{process.StartTime.Ticks}", default);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result is { IsConnected: true } ? 0 : 1;
            return true;
        }
        if (args[0] == "--runtime-usage-probe" && args.Count > 1)
        {
            // Whitelist projection only: identifiers, flags and numbers, never conversation text.
            using var reader = new CodexRuntimeUsageReader();
            var result = args.Count > 2 && int.TryParse(args[2], out var runtimeProcessId)
                ? reader.ReadProcess(runtimeProcessId, $"probe/{runtimeProcessId}", default)
                : reader.Read(default);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.IsAvailable ? 0 : 1;
            return true;
        }
        if (args[0] == "--live-ui-probe")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            var result = LiveUiTests.Run(args.Count > 2 ? args[2] : null);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1; return true;
        }
        if (args[0] == "--feedback-ui-probe")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            var result = FeedbackUiTests.Run(args.Count > 2 ? args[2] : null);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1; return true;
        }
        if (args[0] == "--cost-log-probe" && args.Count > 2)
        {
            var result = CostLogProbe.Run(root, args[2]);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.ReadSucceeded && result.FormulaMatches ? 0 : 1; return true;
        }
        if (args[0] == "--speed-log-probe" && args.Count > 2)
        {
            var result = SpeedLogProbe.Run(root, args[2]);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.ReadSucceeded && result.HasSpeed ? 0 : 1; return true;
        }
        if (args[0] == "--cost-ui-probe")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            var result = CostUiTests.Run(args.Count > 2 ? args[2] : null);
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1; return true;
        }
        if (args[0] == "--self-test")
        {
            var result = SelfTest();
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1;
            return true;
        }
        if (args[0] == "--ignored-exceptions")
        {
            // Diagnostics only: report what deliberately silent catch sites observed
            // during this run. Never enabled automatically in normal operation.
            File.WriteAllText(args[1], DiagnosticsSink.ToJson());
            return true;
        }
        if (args[0] == "--render-preview")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            RenderPreviews(args[1]); return true;
        }
        return false;
    }
    internal static TokenSnapshot Example() => new("example-thread-2026", "synthetic", 4_956_088, 4_877_123,
        4_586_752, 78_965, 20_000, 17_024, 121_600, new DateTime(2026, 10, 2, 10, 30, 0, DateTimeKind.Utc), "relay/custom-model", 2);
    private static void RenderPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var theme in new[] { OverlayThemeKind.Light, OverlayThemeKind.Dark })
        foreach (var expanded in new[] { false, true })
        foreach (var costs in new[] { false, true })
        {
            using var form = new TokenStripForm();
            var settings = OverlaySettings.CreateDefault();
            var example = costs ? Example() with { TotalTokens = 97_603, InputTokens = 97_125, CachedInputTokens = 3_610,
                OutputTokens = 478, ReasoningOutputTokens = 100, Model = "relay/test" } : Example();
            var presentation = OverlayPresentationBuilder.Create(example, DisplayField.Total, costs ? DisplayField.Cost : DisplayField.CacheHitRate,
                settings.VisibleFields | (costs ? DisplayField.Cost : DisplayField.None), costs ? SessionCostTests.SampleResult() : null)
                with { FollowText = "自动跟随" };
            form.SetPresentation(presentation); form.ApplyTheme(OverlayThemePalette.For(theme));
            var host = TitleBarPlacementTests.Host((uint)form.DeviceDpi);
            var layout = OverlayLayoutCalculator.Calculate(new(host, settings.AnchorMode, expanded,
                presentation.ExpandedRows.Count, true));
            form.ApplyLayout(layout);
            using var bitmap = new Bitmap(layout.WindowBounds.Width, layout.WindowBounds.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            using var shaped = new Bitmap(bitmap.Width, bitmap.Height);
            using (var graphics = Graphics.FromImage(shaped)) { graphics.Clear(Color.Transparent); if (form.Region is not null) graphics.Clip = form.Region; graphics.DrawImageUnscaled(bitmap, 0, 0); }
            shaped.Save(Path.Combine(directory, $"{theme.ToString().ToLowerInvariant()}-{(expanded ? "expanded" : "collapsed")}{(costs ? "-cost" : "")}.png"), ImageFormat.Png);
        }
        var isolated = Path.Combine(Path.GetTempPath(), "CodexRelayUsage-menu-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(isolated, "sessions"));
        try
        {
            using var context = new OverlayContext(Path.Combine(isolated, "sessions"), Path.Combine(isolated, "settings.json"), false, false);
            context.RenderTrayPreviews(directory);
        }
        finally { Directory.Delete(isolated, true); }
        RenderWaitingPreviews(directory);
    }

    // The strip clears numbers immediately but words the wait in three stages; the
    // rendered capsules are the user-visible evidence for that behavior.
    private static void RenderWaitingPreviews(string directory)
    {
        var settings = OverlaySettings.CreateDefault();
        var stages = new[] { PageWaitStage.Reading, PageWaitStage.NotReady, PageWaitStage.LongWait };
        var labels = new[] { "不足 3 秒 · 正在读取当前对话…", "达到 3 秒 · 当前页面尚未就绪", "达到 60 秒 · 等待较久，可尝试重启 Codex" };
        foreach (var theme in new[] { OverlayThemeKind.Light, OverlayThemeKind.Dark })
        {
            var frames = new List<Bitmap>();
            try
            {
                foreach (var stage in stages)
                {
                    using var form = new TokenStripForm();
                    var presentation = OverlayPresentationBuilder.CreateWaiting(PageWaitRules.Text(stage)!, DisplayField.Total,
                        DisplayField.CacheHitRate, settings.VisibleFields);
                    form.SetPresentation(presentation);
                    form.ApplyTheme(OverlayThemePalette.For(theme));
                    var host = TitleBarPlacementTests.Host((uint)form.DeviceDpi);
                    var layout = OverlayLayoutCalculator.Calculate(new(host, settings.AnchorMode, false, presentation.ExpandedRows.Count, true));
                    form.ApplyLayout(layout);
                    using var bitmap = new Bitmap(layout.WindowBounds.Width, layout.WindowBounds.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    var shaped = new Bitmap(bitmap.Width, bitmap.Height);
                    using (var clip = Graphics.FromImage(shaped))
                    {
                        clip.Clear(Color.Transparent);
                        if (form.Region is not null) clip.Clip = form.Region;
                        clip.DrawImageUnscaled(bitmap, 0, 0);
                    }
                    frames.Add(shaped);
                }
                const int padding = 24, labelHeight = 28;
                var width = frames.Max(f => f.Width) + padding * 2;
                var rowHeight = frames.Max(f => f.Height) + padding + labelHeight;
                using var canvas = new Bitmap(width, rowHeight * frames.Count + padding);
                using var graphics = Graphics.FromImage(canvas);
                graphics.Clear(OverlayThemePalette.For(theme).ToolbarSurface);
                using var font = new Font("Microsoft YaHei UI", 10, GraphicsUnit.Point);
                using var brush = new SolidBrush(OverlayThemePalette.For(theme).Label);
                for (var i = 0; i < frames.Count; i++)
                {
                    graphics.DrawString(labels[i], font, brush, padding, padding + rowHeight * i);
                    graphics.DrawImageUnscaled(frames[i], padding, padding + rowHeight * i + labelHeight);
                }
                canvas.Save(Path.Combine(directory, theme.ToString().ToLowerInvariant() + "-waiting-stages.png"), ImageFormat.Png);
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }
    internal static CodexWindowInfo Host(uint dpi) => new(new IntPtr(123), new(0, 0, 1400, 1000), new(0, 0, 1400, 1000),
        null, new(0, 0, 3000, 2000), dpi, new(46, 30, 8, 8, 4));
    internal static SelfTestResult SelfTest() => SyntheticSelfTests.Run();
    internal sealed record TestResult(string Name, bool Passed, string? Error);
    internal sealed record SelfTestResult(int Passed, int Failed, IReadOnlyList<TestResult> Results);
}
