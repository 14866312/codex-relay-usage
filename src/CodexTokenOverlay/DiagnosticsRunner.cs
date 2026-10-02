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
        if (args[0] == "--self-test")
        {
            var result = SelfTest();
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
            Environment.ExitCode = result.Failed == 0 ? 0 : 1;
            return true;
        }
        if (args[0] == "--render-preview")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
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
        {
            using var form = new TokenStripForm();
            var settings = OverlaySettings.CreateDefault();
            var presentation = OverlayPresentationBuilder.Create(Example(), DisplayField.Total, DisplayField.CacheHitRate, settings.VisibleFields)
                with { FollowText = "自动跟随" };
            form.SetPresentation(presentation); form.ApplyTheme(OverlayThemePalette.For(theme));
            var host = Host((uint)form.DeviceDpi);
            var layout = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.InsideBottomRight, expanded,
                presentation.ExpandedRows.Count, true, new Point(700, 800)));
            form.ApplyLayout(layout);
            using var bitmap = new Bitmap(layout.WindowBounds.Width, layout.WindowBounds.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            using var shaped = new Bitmap(bitmap.Width, bitmap.Height);
            using (var graphics = Graphics.FromImage(shaped)) { graphics.Clear(Color.Transparent); if (form.Region is not null) graphics.Clip = form.Region; graphics.DrawImageUnscaled(bitmap, 0, 0); }
            shaped.Save(Path.Combine(directory, $"{theme.ToString().ToLowerInvariant()}-{(expanded ? "expanded" : "collapsed")}.png"), ImageFormat.Png);
        }
    }
    private static CodexWindowInfo Host(uint dpi) => new(new IntPtr(123), new(0, 0, 1400, 1000), new(0, 0, 1400, 1000),
        null, new(0, 0, 3000, 2000), dpi, new(46, 30, 8, 8, 4));
    private static SelfTestResult SelfTest()
    {
        var results = new List<TestResult>();
        void Check(string name, bool condition) { results.Add(new(name, condition, condition ? null : "assertion failed")); }
        var directory = Path.Combine(Path.GetTempPath(), "CodexRelayUsage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        Directory.CreateDirectory(Path.Combine(directory, "archived_sessions"));
        var a = Path.Combine(directory, "sessions", "a.jsonl");
        var b = Path.Combine(directory, "sessions", "b.jsonl");
        var empty = Path.Combine(directory, "sessions", "empty.jsonl");
        var utf8 = new UTF8Encoding(false);
        string Meta(string id) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, originator = "Codex Desktop", source = "vscode", cwd = "synthetic" } });
        string Event(object payload) => JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:30:00Z", type = "event_msg", payload });
        string Usage(object total, object? last = null, object? window = null) => Event(new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last, model_context_window = window } });
        void Append(string path, string text) => File.AppendAllText(path, text + (char)10, utf8);
        try
        {
            File.WriteAllText(a, Meta("a") + (char)10, utf8);
            File.WriteAllText(b, Meta("b") + (char)10, utf8);
            File.WriteAllText(empty, Meta("empty") + (char)10, utf8);
            var screenshot = new { input_tokens = 4_877_123L, cached_input_tokens = 4_586_752L, output_tokens = 78_965L, total_tokens = 4_956_088L, reasoning_output_tokens = 20_000L };
            Append(a, Event(new { type = "task_started", turn_id = "turn-1" }));
            Append(a, Event(new { type = "task_started", turn_id = "turn-1" }));
            Append(a, Event(new { type = "task_started", turn_id = "turn-2" }));
            Append(a, JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "relay/中文-🤖" } }));
            Append(a, Usage(screenshot, new { total_tokens = 17_024 }, 121_600));
            Append(a, Usage(screenshot, new { total_tokens = 17_024 }, 121_600));
            using var monitor = new TokenLogMonitor(Path.Combine(directory, "sessions")) { PreferredThreadId = "a" };
            var s = monitor.Poll(true)!;
            Check("screenshot total + no duplicate cumulative sum", s.TotalTokens == 4_956_088);
            Check("screenshot uncached", s.UncachedInputTokens == 290_371);
            Check("screenshot cache rate", Math.Abs(s.CacheHitPercent!.Value - 94.04626252042) < 0.001);
            Check("reasoning already included", s.EffectiveTotalTokens == 4_956_088 && s.ReasoningOutputTokens == 20_000);
            Check("last call context, not lifetime", s.ContextPercent == 14);
            Check("turn id dedup", s.TurnCount == 2);
            Check("relay alias and UTF8 preserved", s.Model == "relay/中文-🤖");
            var presentation = OverlayPresentationBuilder.Create(s, DisplayField.Total, DisplayField.CacheHitRate, OverlaySettings.CreateDefault().VisibleFields);
            Check("exact expanded total", presentation.Total!.Value == "4,956,088 tok");
            Check("expanded cached and uncached", presentation.ExpandedRows.Any(r => r.Value == "4,586,752 tok") && presentation.ExpandedRows.Any(r => r.Value == "290,371 tok"));
            var partial = Usage(new { input_tokens = 100L, output_tokens = 5L, cached_input_tokens = 0L });
            File.AppendAllText(a, partial[..(partial.Length / 2)], utf8);
            Check("partial row retains valid snapshot", monitor.Poll(true)!.TotalTokens == 4_956_088);
            Append(a, partial[(partial.Length / 2)..]);
            s = monitor.Poll(true)!;
            Check("complete row updates and total fallback", s.TotalTokens is null && s.EffectiveTotalTokens == 105);
            Check("explicit cache zero preserved", s.CachedInputTokens == 0 && s.CacheHitPercent == 0);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L })); s = monitor.Poll(true)!;
            Check("missing cache not zero", s.CachedInputTokens is null && s.UncachedInputTokens is null && s.CacheHitPercent is null);
            Check("missing reasoning", s.ReasoningOutputTokens is null);
            Check("missing latest call context", s.ContextPercent is null);
            Append(a, Usage(new { input_tokens = 0L, output_tokens = 0L, cached_input_tokens = 0L })); s = monitor.Poll(true)!;
            Check("zero denominator undefined", s.EffectiveTotalTokens == 0 && s.CacheHitPercent is null);
            Append(a, Usage(new { input_tokens = 100L, cached_input_tokens = 101L, output_tokens = 5L })); s = monitor.Poll(true)!;
            Check("cache greater than input flagged", s.CacheHitPercent is null && s.UncachedInputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = -1L, cached_input_tokens = 0L, output_tokens = 5L, total_tokens = -1L })); s = monitor.Poll(true)!;
            Check("negative flagged without clamping", s.EffectiveTotalTokens is null && s.UncachedInputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = long.MaxValue, output_tokens = 5L })); s = monitor.Poll(true)!;
            Check("sum overflow flagged", s.EffectiveTotalTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = "invalid", output_tokens = 5L, cached_input_tokens = 0L })); s = monitor.Poll(true)!;
            Check("invalid numeric type", s.InputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L, total_tokens = "invalid" })); s = monitor.Poll(true)!;
            Check("invalid reported total prevents plausible fallback", s.TotalTokenInvalid && s.EffectiveTotalTokens is null && s.Issue is not null);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Append(a, Usage(new { input_tokens = 123L, output_tokens = 2L, total_tokens = 125L })); s = monitor.Poll()!;
            Check("complete appended row visible within two seconds", s.TotalTokens == 125 && elapsed.Elapsed.TotalSeconds < 2);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L, reasoning_output_tokens = 6L, total_tokens = 106L })); s = monitor.Poll(true)!;
            Check("inconsistent total and reasoning flagged", s.Issue!.Contains("不一致") && !s.ReasoningIsValid);
            Append(a, Usage(screenshot));
            Append(a, Event(new { type = "token_count", info = new { total_token_usage = (object?)null } }));
            Check("no usage info does not erase totals", monitor.Poll(true)!.TotalTokens == 4_956_088);
            Append(b, Usage(new { input_tokens = 10, output_tokens = 3, total_tokens = 13 }));
            Check("background file cannot replace selected", monitor.Poll(true)!.ThreadId == "a");
            var version = monitor.ActiveSessionVersion; monitor.PreferredThreadId = "empty"; s = monitor.Poll()!;
            Check("new empty thread clears previous numbers", s.ThreadId == "empty" && s.TotalTokens is null && !s.UsageRecorded && monitor.ActiveSessionVersion > version);
            monitor.PreferredThreadId = "b"; s = monitor.Poll()!;
            Check("idle selected conversation switch", s.TotalTokens == 13);
            monitor.PinActiveSession = true; monitor.PreferredThreadId = "a";
            Check("pin overrides auto route", monitor.Poll()!.ThreadId == "b");
            monitor.PinActiveSession = false; monitor.PreferredThreadId = null;
            Check("no selected id never picks newest", monitor.Poll() is null);
            monitor.PreferredThreadId = "not-written-yet";
            Check("unknown thread never picks newest", monitor.Poll() is null);
            monitor.SelectManual("a"); Check("manual select works without IPC", monitor.Poll()!.ThreadId == "a");
            var copy = Path.Combine(directory, "archived_sessions", "a.jsonl"); File.Copy(a, copy);
            Check("archive and active dedup", monitor.ListSessions().Count(e => e.ThreadId == "a") == 1);
            File.Delete(a); Check("archive move retains counts", monitor.Poll(true)!.TotalTokens == 4_956_088);
            File.WriteAllText(copy, Meta("a") + (char)10 + Usage(new { input_tokens = 1, output_tokens = 2 }) + (char)10, utf8);
            Check("file truncation reread", monitor.Poll(true)!.EffectiveTotalTokens == 3);
            var rawModel = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "relay/中文模型" } },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + (char)10);
            var splitAt = Array.IndexOf(rawModel, (byte)0xE4) + 1;
            using (var append = new FileStream(copy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(rawModel.AsSpan(0, splitAt));
            Check("partial UTF8 codepoint retains previous model", monitor.Poll(true)!.Model is null);
            using (var append = new FileStream(copy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(rawModel.AsSpan(splitAt));
            Check("UTF8 codepoint split across reads", monitor.Poll(true)!.Model == "relay/中文模型");
            var replacement = Meta("a") + (char)10 + Usage(new { input_tokens = 2, output_tokens = 1 }) + (char)10;
            File.WriteAllText(copy, replacement, utf8); monitor.Poll(true);
            File.WriteAllText(copy, replacement.Replace("\"input_tokens\":2", "\"input_tokens\":9"), utf8);
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(1));
            Check("same-length rewritten log restarts reader", monitor.Poll(true)!.EffectiveTotalTokens == 10);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2, total_tokens = 1.5 })); s = monitor.Poll(true)!;
            Check("fractional reported total flagged", s.TotalTokenInvalid && s.EffectiveTotalTokens is null && s.Issue is not null);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2 }, new { total_tokens = 100 }, 0)); s = monitor.Poll(true)!;
            Check("zero context window is undefined and flagged", s.ContextPercent is null && s.Issue is not null);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2 }, new { total_tokens = 100 }, 50)); s = monitor.Poll(true)!;
            Check("context above window reported as abnormal", s.ContextPercent == 200 && s.Issue is not null);
            var route = new ActiveThreadRouteStatus("a", 1, true, 1, null);
            Check("route only explicit current", FollowSelection.Resolve(route, null) == "a");
            Check("disconnect clears auto", FollowSelection.Resolve(route with { IsConnected = false }, null) is null);
            Check("no identity clears auto", FollowSelection.Resolve(route with { ThreadId = null }, null) is null);
            Check("multiple windows require manual", FollowSelection.Resolve(route with { ActiveWindowCount = 2 }, null) is null);
            Check("pin survives disconnect", FollowSelection.Resolve(route with { IsConnected = false }, "b") == "b");
            var settings = OverlaySettings.CreateDefault(); settings.PinnedThreadId = "b"; settings.SessionRoot = directory;
            var saved = Path.Combine(directory, "settings.json"); settings.Save(saved); var loaded = OverlaySettings.Load(saved);
            Check("settings persistence", loaded.PinnedThreadId == "b" && loaded.SessionRoot == directory && loaded.CollapsedSecondaryField == DisplayField.CacheHitRate);
            Check("corrupt settings recover", OverlaySettings.ParseJson("bad-json").Settings.CollapsedPrimaryField == DisplayField.Total);
            foreach (var dpi in new uint[] { 96, 120, 144, 192 })
            foreach (var scale in new[] { 60, 100, 130 })
            {
                var layout = OverlayLayoutCalculator.Calculate(new(Host(dpi), AnchorMode.InsideBottomRight, true, 5, true, new Point(700, 1600), scale));
                Check($"layout {dpi} DPI {scale}% upward in screen", layout.State == OverlayVisualState.Expanded && layout.ExpansionDirection == ExpansionDirection.Up && layout.WindowBounds.Top >= 0 && layout.WindowBounds.Right <= 3000);
            }
            var interaction = new OverlayInteractionState(); interaction.OnCapsuleMouseUp();
            interaction.OnPointerSample(PointerButtons.None, true); interaction.OnPointerSample(PointerButtons.Left, false);
            Check("outside click collapses", interaction.State == OverlayVisualState.Collapsed);
        }
        catch (Exception e) { results.Add(new("unhandled test exception", false, e.ToString())); }
        finally { Directory.Delete(directory, true); }
        return new(results.Count(r => r.Passed), results.Count(r => !r.Passed), results);
    }
    internal sealed record TestResult(string Name, bool Passed, string? Error);
    internal sealed record SelfTestResult(int Passed, int Failed, IReadOnlyList<TestResult> Results);
}
