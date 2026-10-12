using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

// Deterministic checks for the context-window directory: parsing, resolution priority,
// the reading lifecycle around model switches, and the presentation block.
internal static class ContextWindowTests
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static void Run(Action<string, bool> check, string directory)
    {
        Catalog(check);
        Resolution(check);
        ReadingLifecycle(check, directory);
        Presentation(check);
        ProbeRoundTrip(check);
        Layout(check);
    }

    private static void Catalog(Action<string, bool> check)
    {
        var entries = ContextWindowCatalog.Parse("""
            { "fetched_at": "2026-10-12", "models": [
              { "slug": "cn/deepseek-v4.1-flash", "context_window": 1000000, "max_context_window": 1000000, "effective_context_window_percent": 95 },
              { "slug": "no-percent", "context_window": 128000 },
              { "slug": "broken", "context_window": "many" },
              { "context_window": 1000 }
            ] }
            """);
        check("directory keeps slug, windows and ratio", entries.Count == 3
            && entries["cn/deepseek-v4.1-flash"] == new ContextWindowEntry(1_000_000, 1_000_000, 95, ContextWindowRules.CatalogSource));
        check("directory tolerates a missing ratio and a non-numeric window",
            entries["no-percent"] is { ContextWindow: 128_000, EffectivePercent: null }
            && entries["broken"] is { ContextWindow: null, MaxContextWindow: null });
        var rejected = false;
        try { ContextWindowCatalog.Parse("{ not json"); } catch (JsonException) { rejected = true; }
        check("damaged directory is surfaced to the caller", rejected);
    }

    private static void Resolution(Action<string, bool> check)
    {
        var catalog = ContextWindowCatalog.Parse("""
            { "models": [
              { "slug": "cn/deepseek-v4.1-flash", "context_window": 1000000, "max_context_window": 1000000, "effective_context_window_percent": 95 },
              { "slug": "codex-auto-review", "context_window": 272000, "max_context_window": 872000, "effective_context_window_percent": 95 },
              { "slug": "no-percent", "context_window": 128000 },
              { "slug": "zero-percent", "context_window": 128000, "effective_context_window_percent": 0 },
              { "slug": "over-percent", "context_window": 128000, "effective_context_window_percent": 150 },
              { "slug": "negative-window", "context_window": -5, "max_context_window": 100000 },
              { "slug": "full-window", "context_window": 128000, "effective_context_window_percent": 100 }
            ] }
            """);
        var logged = ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", 950_000, catalog);
        check("log window wins over the directory",
            logged is { EffectiveWindow: 950_000, EffectiveSource: "日志", ConfiguredWindow: 1_000_000, ConfiguredSource: "目录" }
            && !logged.ConfiguredReference);
        check("directory window is scaled by the reserved ratio",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", null, catalog) is { EffectiveWindow: 950_000, EffectiveSource: "目录" });
        check("configured window prefers context_window over max_context_window",
            ContextWindowRules.Resolve("codex-auto-review", null, catalog) is { EffectiveWindow: 258_400, ConfiguredWindow: 272_000 });
        check("missing ratio falls back to ninety-five percent",
            ContextWindowRules.Resolve("no-percent", null, catalog).EffectiveWindow == 121_600);
        check("invalid ratio falls back to ninety-five percent",
            ContextWindowRules.Resolve("zero-percent", null, catalog).EffectiveWindow == 121_600
            && ContextWindowRules.Resolve("over-percent", null, catalog).EffectiveWindow == 121_600);
        check("invalid configured window falls back to the other column",
            ContextWindowRules.Resolve("negative-window", null, catalog).EffectiveWindow == 95_000);
        check("zero log window falls back to the directory",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", 0, catalog) is { EffectiveWindow: 950_000, EffectiveSource: "目录" });
        check("disagreeing directory window is marked a reference",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", 128_000, catalog) is { EffectiveWindow: 128_000, ConfiguredReference: true });
        check("a log window off the directory derivation is marked a reference",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", 1_000_000, catalog).ConfiguredReference);
        check("a directory without a reserve agrees with the same log window",
            !ContextWindowRules.Resolve("full-window", 128_000, catalog).ConfiguredReference);
        var unusable = ContextWindowCatalog.Parse("""
            { "models": [ { "slug": "cn/deepseek-v4.1-flash", "context_window": -1, "max_context_window": 0 } ] }
            """);
        check("an unusable directory row steps aside for the embedded table",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", null, unusable, EmbeddedModelWindows.Entries) is { EffectiveWindow: 950_000, EffectiveSource: "内置映射" });
        check("unknown model keeps the ceiling unknown",
            ContextWindowRules.Resolve("relay/unknown", 121_600, catalog) is { EffectiveWindow: 121_600, ConfiguredWindow: null, ConfiguredSource: null });
        check("embedded directory answers without a cache file",
            ContextWindowRules.Resolve("cn/deepseek-v4.1-flash", null, null, EmbeddedModelWindows.Entries) is { EffectiveWindow: 950_000, EffectiveSource: "内置映射" });
        check("no window anywhere stays unknown",
            ContextWindowRules.Resolve("relay/unknown", null, null, EmbeddedModelWindows.Entries) is { EffectiveWindow: null, ConfiguredWindow: null });
    }

    private static void ReadingLifecycle(Action<string, bool> check, string directory)
    {
        var home = Path.Combine(directory, "window-home");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        WriteCatalog(home, "relay/window", 1_000_000, 95);
        var path = Path.Combine(sessions, "window.jsonl");
        File.WriteAllText(path, Meta("window") + TurnContext("relay/window") + TokenCount(100_000, 100_000), Utf8);
        using var monitor = new TokenLogMonitor(sessions);
        var snapshot = monitor.Poll("window", true)!;
        check("a log without its own window takes the directory window",
            snapshot is { ContextUsedTokens: 100_000, ContextWindowTokens: 950_000, ContextWindowSource: "目录", ConfiguredWindowTokens: 1_000_000, ConfiguredWindowSource: "目录" });
        check("remaining ceiling excludes the latest call estimate", snapshot.RemainingContextTokens == 850_000);
        WriteCatalog(home, "relay/window", 128_000, 95);
        snapshot = monitor.Poll("window", true)!;
        check("a directory-only change republishes the window",
            snapshot is { ContextWindowTokens: 121_600, ContextUsedTokens: 100_000 });
        File.AppendAllText(path, TokenCount(220_000, 120_000, 121_600), Utf8);
        snapshot = monitor.Poll("window", true)!;
        check("a log window outranks the directory",
            snapshot is { ContextWindowTokens: 121_600, ContextWindowSource: "日志" } && !snapshot.ConfiguredWindowReference);
        File.AppendAllText(path, TokenCount(228_000, 8_000), Utf8);
        snapshot = monitor.Poll("window", true)!;
        check("a smaller reading after compaction is accepted",
            snapshot.ContextUsedTokens == 8_000 && Math.Abs(snapshot.ContextPercent!.Value - 8_000d / 121_600d * 100d) < 0.01);
        File.AppendAllText(path, TurnContext("relay/other"), Utf8);
        snapshot = monitor.Poll("window", true)!;
        check("a model switch clears the old reading and window",
            snapshot is { ContextUsedTokens: null, ContextWindowTokens: null, Model: "relay/other" });
        check("a model switch keeps the cumulative totals", snapshot.TotalTokens == 228_000);
        var otherHome = Path.Combine(directory, "window-other");
        var otherSessions = Path.Combine(otherHome, "sessions");
        Directory.CreateDirectory(otherSessions);
        WriteCatalog(otherHome, "relay/window", 1_000_000, 95);
        File.WriteAllText(Path.Combine(otherSessions, "window.jsonl"),
            Meta("window") + TurnContext("relay/window") + TokenCount(10_000, 10_000, 950_000), Utf8);
        monitor.SetRoot(otherSessions);
        snapshot = monitor.Poll("window", true)!;
        check("a data-root switch rebuilds the directory from the new home",
            snapshot is { ContextWindowTokens: 950_000, ContextWindowSource: "日志", ConfiguredWindowTokens: 1_000_000, ConfiguredWindowSource: "目录" });
    }

    private static void Presentation(Action<string, bool> check)
    {
        var snapshot = DiagnosticsRunner.Example() with
        {
            Model = "cn/deepseek-v4.1-flash", ContextUsedTokens = 335_412, ContextWindowTokens = 950_000,
            ConfiguredWindowTokens = 1_000_000, ConfiguredWindowSource = ContextWindowRules.CatalogSource,
            ContextWindowSource = ContextWindowRules.LogSource
        };
        var defaults = OverlaySettings.CreateDefault().VisibleFields;
        var percentOnly = OverlayPresentationBuilder.Create(snapshot, DisplayField.Total, DisplayField.CacheHitRate, defaults);
        check("percent row keeps one decimal and the only progress bar",
            percentOnly.ExpandedRows.Single(row => row.Field == DisplayField.ContextPercent).Value == "35.3%"
            && percentOnly.ShowContextProgress
            && percentOnly.ExpandedRows.Count(row => row.Field == DisplayField.ContextPercent) == 1);
        check("default block carries used over window",
            percentOnly.ExpandedRows.Any(row => row.ExpandedLabel == "已用 / 可用窗口" && row.Value == "335,412 / 950,000 tok"));
        check("block names the directory window",
            percentOnly.ExpandedRows.Any(row => row.ExpandedLabel == "模型配置窗口（目录）" && row.Value == "1,000,000 tok"));
        check("block shows the remaining ceiling",
            percentOnly.ExpandedRows.Any(row => row.ExpandedLabel == "剩余可用（估算）" && row.Value == "614,588 tok"));
        check("block shows the full model id",
            percentOnly.ExpandedRows.Any(row => row.ExpandedLabel == "模型" && row.Value == "cn/deepseek-v4.1-flash"));
        var both = OverlayPresentationBuilder.Create(snapshot, DisplayField.Total, DisplayField.CacheHitRate, defaults | DisplayField.Context);
        check("both context fields keep one row per fact",
            both.ExpandedRows.Count(row => row.Field is DisplayField.Context or DisplayField.ContextPercent) == 2
            && both.ExpandedRows.Count(row => row.Field == DisplayField.ContextPercent) == 1
            && !both.ExpandedRows.Any(row => row.ExpandedLabel == "已用 / 可用窗口")
            && both.ExpandedRows.Any(row => row.Field == DisplayField.Context && row.Value == "335,412 / 950,000 tok"));
        var contextOnly = OverlayPresentationBuilder.Create(snapshot, DisplayField.Total, DisplayField.CacheHitRate,
            DisplayField.Total | DisplayField.Context);
        check("context-only block keeps the amounts without a bar",
            !contextOnly.ShowContextProgress && !contextOnly.ExpandedRows.Any(row => row.Field == DisplayField.ContextPercent)
            && contextOnly.ExpandedRows.Any(row => row.Field == DisplayField.Context && row.Value == "335,412 / 950,000 tok"));
        var reference = OverlayPresentationBuilder.Create(snapshot with { ConfiguredWindowReference = true },
            DisplayField.Total, DisplayField.CacheHitRate, defaults);
        check("a disagreeing directory window is labelled a reference",
            reference.ExpandedRows.Any(row => row.ExpandedLabel == "模型配置窗口（目录参考）"));
        var unknown = OverlayPresentationBuilder.Create(snapshot with
            { ContextWindowTokens = null, ConfiguredWindowTokens = null, ConfiguredWindowSource = null, ContextWindowSource = null },
            DisplayField.Total, DisplayField.CacheHitRate, defaults);
        check("an unknown ceiling invents neither a percent nor a remainder",
            unknown.ExpandedRows.Single(row => row.Field == DisplayField.ContextPercent).Value == "—"
            && unknown.ExpandedRows.Any(row => row.ExpandedLabel == "模型配置窗口" && row.Value == "— / 未知上限")
            && unknown.ExpandedRows.Any(row => row.ExpandedLabel == "剩余可用（估算）" && row.Value == "—")
            && unknown.ExpandedRows.Any(row => row.ExpandedLabel == "已用 / 可用窗口" && row.Value.StartsWith("335,412", StringComparison.Ordinal)));
        var over = OverlayPresentationBuilder.Create(snapshot with { ContextUsedTokens = 1_000_000 },
            DisplayField.Total, DisplayField.CacheHitRate, defaults);
        check("an over-window estimate keeps its true percent and is flagged",
            Math.Abs(over.ContextPercent - 105.2631) < 0.01
            && over.ExpandedRows.Single(row => row.Field == DisplayField.ContextPercent).Value == "105.3%"
            && over.ExpandedRows.Any(row => row.ExpandedLabel == "异常" && row.Value.Contains("超过上下文窗口")));
        var withoutBlock = OverlayPresentationBuilder.Create(snapshot with { ContextUsedTokens = 1_000_000 },
            DisplayField.Total, DisplayField.CacheHitRate, DisplayField.Total | DisplayField.Output);
        check("the anomaly row belongs to the context block",
            !withoutBlock.ExpandedRows.Any(row => row.ExpandedLabel == "异常"));
        check("token units promote before printing 1000K",
            OverlayPresentationBuilder.FormatTokenCount(999_950) == "1M"
            && OverlayPresentationBuilder.FormatTokenCount(999_949) == "999.9K"
            && OverlayPresentationBuilder.FormatTokenCount(950_000) == "950K"
            && OverlayPresentationBuilder.FormatTokenCount(1_040_000) == "1M"
            && OverlayPresentationBuilder.FormatTokenCount(1_000_000) == "1M");
        check("percent keeps at most one decimal",
            OverlayPresentationBuilder.Percent(35.34) == "35.3%" && OverlayPresentationBuilder.Percent(14) == "14%"
            && OverlayPresentationBuilder.Percent(null) == "—");
    }

    // The JSON probe is the documented surface for reading the presentation without a
    // desktop; the new window fields must round-trip through it like the rest.
    private static void ProbeRoundTrip(Action<string, bool> check)
    {
        var json = """
            { "cases": [ { "name": "context", "operation": "Create", "primaryField": 1, "secondaryField": 64, "visibleFields": 64,
              "snapshot": { "threadId": "probe", "logPath": "synthetic", "totalTokens": null, "inputTokens": null,
                "cachedInputTokens": null, "outputTokens": null, "reasoningOutputTokens": null, "contextUsedTokens": 335412,
                "contextWindowTokens": 950000, "updatedAtUtc": "2026-10-12T00:00:00Z", "model": "cn/deepseek-v4.1-flash",
                "configuredWindowTokens": 1000000, "contextWindowSource": "日志", "configuredWindowSource": "目录", "configuredWindowReference": false } } ] }
            """;
        var request = JsonSerializer.Deserialize<PresentationProbeRequest>(json, JsonOptions)!;
        var result = PresentationProbe.Execute(request);
        check("presentation probe carries the window fields", result.Cases.Count == 1
            && result.Cases[0].Presentation.ExpandedRows.Any(row => row.Value == "1,000,000 tok")
            && result.Cases[0].Presentation.ExpandedRows.Any(row => row.Value == "614,588 tok"));
    }

    private static void Layout(Action<string, bool> check)
    {
        var presentation = OverlayPresentationBuilder.Create(
            DiagnosticsRunner.Example() with { Model = "cn/deepseek-v4.1-flash", ContextUsedTokens = 335_412, ContextWindowTokens = 950_000 },
            DisplayField.Total, DisplayField.CacheHitRate, OverlaySettings.CreateDefault().VisibleFields);
        check("default block adds the four context rows", presentation.ExpandedRows.Count == 10);
        OverlayLayoutResult Layout(int height) => OverlayLayoutCalculator.Calculate(new(
            DiagnosticsRunner.Host(96) with
            {
                WorkingArea = new IntRect(0, 0, 1000, height),
                WindowBounds = new IntRect(0, 0, 1000, height),
                ExtendedFrameBounds = new IntRect(0, 0, 1000, height)
            },
            AnchorMode.InsideBottomRight, true, presentation.ExpandedRows.Count, true));
        var roomy = Layout(1200);
        check("a comfortable screen expands the whole block at readable rows",
            roomy.State == OverlayVisualState.Expanded && roomy.ExpandedRowHeight >= 24);
        var cramped = Layout(300);
        check("a cramped screen refuses to squeeze the block below 24 dip",
            cramped.State != OverlayVisualState.Expanded && cramped.ExpandedRowHeight == 0);
        foreach (var height in new[] { 300, 480, 620, 900, 1200 })
        {
            var layout = Layout(height);
            check("context block keeps the 24 dip row floor on a " + height + " px screen",
                layout.State != OverlayVisualState.Expanded || layout.ExpandedRowHeight >= 24);
        }
    }

    private static string Meta(string id) =>
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd = "synthetic" } }) + "\n";
    private static string Row(string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = "2026-10-12T08:00:00Z", type, payload }) + "\n";
    private static string TurnContext(string model) => Row("turn_context", new { turn_id = "turn", model });
    private static string TokenCount(long total, long last, long? window = null) =>
        Row("event_msg", new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new { input_tokens = total, cached_input_tokens = 0L, output_tokens = 0L, total_tokens = total },
                last_token_usage = new { input_tokens = last, cached_input_tokens = 0L, output_tokens = 0L, total_tokens = last },
                model_context_window = window
            }
        });
    private static void WriteCatalog(string home, string slug, long window, double percent) =>
        File.WriteAllText(Path.Combine(home, "models_cache.json"), JsonSerializer.Serialize(new
        {
            fetched_at = "2026-10-12T00:00:00Z",
            models = new[] { new { slug, context_window = window, max_context_window = window, effective_context_window_percent = percent } }
        }), Utf8);
}
