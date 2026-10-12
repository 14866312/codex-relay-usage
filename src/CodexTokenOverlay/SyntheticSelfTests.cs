using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class SyntheticSelfTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static DiagnosticsRunner.SelfTestResult Run() => SelfTest();
    private static DiagnosticsRunner.SelfTestResult SelfTest()
    {
        var results = new List<DiagnosticsRunner.TestResult>();
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
        string Record(string turn, string timestamp, string response, long input, long cached, long output, long turnOutput) =>
            JsonSerializer.Serialize(new { timestamp, type = "token_usage_record", payload = new
            {
                thread_id = "speed", session_id = "speed", response_id = response, turn_id = turn,
                usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0L, output_tokens = output, reasoning_output_tokens = 0L, total_tokens = input + output },
                turn_token_usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0L, output_tokens = turnOutput, reasoning_output_tokens = 0L, total_tokens = input + turnOutput }
            } });
        string Started(string turn, string timestamp) => JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "task_started", turn_id = turn } });
        string Complete(string turn, long? durationMs, long? ttfbMs) => JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:09:00Z", type = "event_msg",
            payload = durationMs is null ? (object)new { type = "task_complete", turn_id = turn }
                : new { type = "task_complete", turn_id = turn, duration_ms = durationMs.Value, time_to_first_token_ms = ttfbMs ?? 0L } });
        string Item(string turn, string type, long startedMs, long completedMs) => JsonSerializer.Serialize(new { timestamp = "2026-10-02T11:00:01Z", type = "event_msg",
            payload = new { type = "item_completed", turn_id = turn, item = new { type }, started_at_ms = startedMs, completed_at_ms = completedMs } });
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
            using var monitor = new TokenLogMonitor(Path.Combine(directory, "sessions"));
            var s = monitor.Poll("a", true)!;
            Check("screenshot total + no duplicate cumulative sum", s.TotalTokens == 4_956_088);
            Check("screenshot uncached", s.UncachedInputTokens == 290_371);
            Check("screenshot cache rate", Math.Abs(s.CacheHitPercent!.Value - 94.04626252042) < 0.001);
            Check("reasoning already included", s.EffectiveTotalTokens == 4_956_088 && s.ReasoningOutputTokens == 20_000);
            Check("last call context, not lifetime", s.ContextPercent == 14);
            Check("turn id dedup", s.TurnCount == 2);
            Check("relay alias and UTF8 preserved", s.Model == "relay/中文-🤖");
            Check("speed absent without dated usage records", s.OutputSpeed is null);
            var speedPath = Path.Combine(directory, "sessions", "speed.jsonl");
            File.WriteAllText(speedPath, Meta("speed") + (char)10, utf8);
            Append(speedPath, Started("turn-speed", "2026-10-02T10:00:00Z"));
            Append(speedPath, Record("turn-speed", "2026-10-02T10:00:10Z", "resp-1", 1_000, 0, 100, 100));
            var speed = monitor.Poll("speed", true)!;
            Check("speed equals reported turn output per second", Math.Abs(speed.OutputSpeed!.Value - 10) < 0.001);
            Append(speedPath, Record("turn-speed", "2026-10-02T10:00:20Z", "resp-2", 1_100, 0, 50, 150));
            speed = monitor.Poll("speed", true)!;
            Check("speed advances with the latest reported cumulative output", Math.Abs(speed.OutputSpeed!.Value - 7.5) < 0.001);
            var speedPresentation = OverlayPresentationBuilder.Create(speed, DisplayField.Total, DisplayField.Speed,
                DisplayField.Total | DisplayField.Speed);
            Check("speed shown with unit and one decimal", speedPresentation.Secondary.Value == "7.5 tok/s" && speedPresentation.Secondary.HasValue);
            Append(speedPath, Record("turn-speed", "2026-10-02T10:00:25Z", "resp-3", 1_200, 0, 64, 214));
            speed = monitor.Poll("speed", true)!;
            Check("third report still uses elapsed without a completion", Math.Abs(speed.OutputSpeed!.Value - 8.56) < 0.01);
            Append(speedPath, Complete("turn-speed", 5_100, 3_424));
            speed = monitor.Poll("speed", true)!;
            Check("completion window excludes first-token wait", Math.Abs(speed.OutputSpeed!.Value - 214 / 1.676) < 0.01);
            var fastPresentation = OverlayPresentationBuilder.Create(speed, DisplayField.Total, DisplayField.Speed,
                DisplayField.Total | DisplayField.Speed);
            Check("speed above one hundred keeps a decimal", fastPresentation.Secondary.Value == "127.7 tok/s");
            Append(speedPath, Started("turn-short", "2026-10-02T10:01:00Z"));
            Append(speedPath, Record("turn-short", "2026-10-02T10:01:06Z", "resp-4", 1_300, 0, 89, 89));
            Append(speedPath, Complete("turn-short", 6_000, 5_882));
            speed = monitor.Poll("speed", true)!;
            Check("short generation falls back to full duration", Math.Abs(speed.OutputSpeed!.Value - 89 / 6.0) < 0.01);
            Append(speedPath, Started("turn-nodur", "2026-10-02T10:02:00Z"));
            Append(speedPath, Record("turn-nodur", "2026-10-02T10:02:10Z", "resp-5", 1_400, 0, 40, 40));
            Append(speedPath, Complete("turn-nodur", null, null));
            speed = monitor.Poll("speed", true)!;
            Check("completion without duration keeps elapsed speed", Math.Abs(speed.OutputSpeed!.Value - 4.0) < 0.001);
            Append(speedPath, Started("turn-abort", "2026-10-02T10:03:00Z"));
            Append(speedPath, Record("turn-abort", "2026-10-02T10:03:05Z", "resp-6", 1_500, 0, 30, 30));
            Append(speedPath, JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:03:06Z", type = "event_msg", payload = new { type = "turn_aborted", turn_id = "turn-abort" } }));
            speed = monitor.Poll("speed", true)!;
            Check("aborted turn keeps elapsed speed", Math.Abs(speed.OutputSpeed!.Value - 6.0) < 0.001);
            Append(speedPath, Started("turn-next", "2026-10-02T10:04:00Z"));
            Append(speedPath, Record("turn-next", "2026-10-02T10:04:04Z", "resp-7", 1_600, 0, 20, 20));
            speed = monitor.Poll("speed", true)!;
            Check("new turn resets to its own elapsed window", Math.Abs(speed.OutputSpeed!.Value - 5.0) < 0.001);
            Append(speedPath, Complete("turn-next", 4_000, 1_000));
            speed = monitor.Poll("speed", true)!;
            Check("completion refreshes the current turn without a new record", Math.Abs(speed.OutputSpeed!.Value - 20 / 3.0) < 0.01);
            Append(speedPath, Record("turn-next", "2026-10-02T10:04:06Z", "resp-8", 1_700, 0, 24, 44));
            speed = monitor.Poll("speed", true)!;
            Check("record after completion uses the stored generation window", Math.Abs(speed.OutputSpeed!.Value - 44 / 3.0) < 0.01);
            var idlessPath = Path.Combine(directory, "sessions", "idless.jsonl");
            File.WriteAllText(idlessPath, Meta("idless") + (char)10, utf8);
            Append(idlessPath, Started("turn-idless", "2026-10-02T10:05:00Z"));
            Append(idlessPath, JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:05:02Z", type = "token_usage_record", payload = new
            {
                thread_id = "idless", session_id = "idless", response_id = "", turn_id = "turn-idless",
                usage = new { input_tokens = 1_000L, cached_input_tokens = 0L, cache_write_input_tokens = 0L, output_tokens = 40L, reasoning_output_tokens = 0L, total_tokens = 1_040L },
                turn_token_usage = new { input_tokens = 1_000L, cached_input_tokens = 0L, cache_write_input_tokens = 0L, output_tokens = 40L, reasoning_output_tokens = 0L, total_tokens = 1_040L }
            } }));
            Append(idlessPath, JsonSerializer.Serialize(new { timestamp = "2026-10-02T10:05:04Z", type = "token_usage_record", payload = new
            {
                thread_id = "idless", session_id = "idless", response_id = "", turn_id = "turn-idless",
                usage = new { input_tokens = 1_000L, cached_input_tokens = 0L, cache_write_input_tokens = 0L, output_tokens = 40L, reasoning_output_tokens = 0L, total_tokens = 1_040L },
                turn_token_usage = new { input_tokens = 1_000L, cached_input_tokens = 0L, cache_write_input_tokens = 0L, output_tokens = 90L, reasoning_output_tokens = 0L, total_tokens = 1_090L }
            } }));
            speed = monitor.Poll("idless", true)!;
            Check("response_id-less records still advance speed", Math.Abs(speed.OutputSpeed!.Value - 22.5) < 0.001);
            Append(idlessPath, Complete("turn-idless", 4_000, 1_000));
            speed = monitor.Poll("idless", true)!;
            Check("response_id-less completion applies the generation window", Math.Abs(speed.OutputSpeed!.Value - 30.0) < 0.001);
            var averagePresentation = OverlayPresentationBuilder.Create(speed, DisplayField.Total, DisplayField.Speed,
                DisplayField.Total | DisplayField.Speed);
            Check("fallback speed keeps the average label", !speed.SpeedGenerationOnly && speed.GenerationSeconds is null &&
                averagePresentation.Secondary.Value == "30 tok/s" && averagePresentation.Secondary.ExpandedLabel == "输出速度（本轮平均）");
            var generationPath = Path.Combine(directory, "sessions", "speedgen.jsonl");
            File.WriteAllText(generationPath, Meta("speedgen") + (char)10, utf8);
            Append(generationPath, Started("turn-gen", "2026-10-02T11:00:00Z"));
            Append(generationPath, Item("turn-gen", "Reasoning", 0, 2_000));
            Append(generationPath, Item("turn-gen", "AgentMessage", 2_000, 5_000));
            Append(generationPath, Item("turn-gen", "CommandExecution", 5_000, 65_000));
            Append(generationPath, Record("turn-gen", "2026-10-02T11:01:10Z", "resp-g1", 1_000, 0, 100, 500));
            var generation = monitor.Poll("speedgen", true)!;
            Check("generation-only speed uses item seconds", Math.Abs(generation.OutputSpeed!.Value - 100) < 0.001 && generation.SpeedGenerationOnly);
            Check("generation and tool seconds reported", Math.Abs(generation.GenerationSeconds!.Value - 5) < 0.001 &&
                Math.Abs(generation.ToolSeconds!.Value - 60) < 0.001);
            var generationPresentation = OverlayPresentationBuilder.Create(generation, DisplayField.Total, DisplayField.Speed,
                DisplayField.Total | DisplayField.Speed);
            Check("generation-only speed shows the pure label", generationPresentation.Secondary.Value == "100 tok/s" &&
                generationPresentation.Secondary.ExpandedLabel == "输出速度（纯生成）");
            Append(generationPath, Item("turn-gen", "FileChange", 65_000, 70_000));
            generation = monitor.Poll("speedgen", true)!;
            Check("later tool item leaves generation speed unchanged", Math.Abs(generation.OutputSpeed!.Value - 100) < 0.001 &&
                Math.Abs(generation.ToolSeconds!.Value - 65) < 0.001);
            Append(generationPath, Item("turn-gen", "Reasoning", 100_000, 100_000));
            Append(generationPath, Item("turn-gen", "Reasoning", 100_000, 3_700_001));
            Append(generationPath, Item("turn-other", "Reasoning", 100_000, 109_000));
            Append(generationPath, Item("turn-gen", "UserMessage", 100_000, 104_000));
            generation = monitor.Poll("speedgen", true)!;
            Check("zero, oversized, foreign and unknown items ignored", Math.Abs(generation.GenerationSeconds!.Value - 5) < 0.001 &&
                Math.Abs(generation.ToolSeconds!.Value - 65) < 0.001 && Math.Abs(generation.OutputSpeed!.Value - 100) < 0.001);
            Append(generationPath, Started("turn-gen2", "2026-10-02T11:03:00Z"));
            generation = monitor.Poll("speedgen", true)!;
            Check("new turn resets item accumulators", generation.GenerationSeconds is null && generation.ToolSeconds is null &&
                generation.OutputSpeed is null && !generation.SpeedGenerationOnly);
            Append(generationPath, Item("turn-gen2", "Reasoning", 0, 4_000));
            generation = monitor.Poll("speedgen", true)!;
            Check("items without a usage record keep speed empty", generation.OutputSpeed is null && generation.GenerationSeconds is > 0);
            Append(generationPath, Record("turn-gen2", "2026-10-02T11:03:10Z", "resp-g2", 1_000, 0, 60, 300));
            generation = monitor.Poll("speedgen", true)!;
            Check("record after items uses generation seconds", Math.Abs(generation.OutputSpeed!.Value - 75) < 0.001 && generation.SpeedGenerationOnly);
            Append(generationPath, JsonSerializer.Serialize(new { timestamp = "2026-10-02T11:03:11Z", type = "event_msg", payload = new { type = "turn_aborted", turn_id = "turn-gen2" } }));
            generation = monitor.Poll("speedgen", true)!;
            Check("aborted turn retains generation accumulation", Math.Abs(generation.OutputSpeed!.Value - 75) < 0.001 &&
                Math.Abs(generation.GenerationSeconds!.Value - 4) < 0.001);
            var presentation = OverlayPresentationBuilder.Create(s, DisplayField.Total, DisplayField.CacheHitRate, OverlaySettings.CreateDefault().VisibleFields);
            Check("exact expanded total", presentation.Total!.Value == "4,956,088 tok");
            Check("expanded cached and uncached", presentation.ExpandedRows.Any(r => r.Value == "4,586,752 tok") && presentation.ExpandedRows.Any(r => r.Value == "290,371 tok"));
            Check("cache hit rate keeps two decimals", presentation.Secondary.Value == "94.05%"
                && presentation.ExpandedRows.Any(r => r.Field == DisplayField.CacheHitRate && r.Value == "94.05%"));
            var partial = Usage(new { input_tokens = 100L, output_tokens = 5L, cached_input_tokens = 0L });
            File.AppendAllText(a, partial[..(partial.Length / 2)], utf8);
            Check("partial row retains valid snapshot", monitor.Poll("a", true)!.TotalTokens == 4_956_088);
            Append(a, partial[(partial.Length / 2)..]);
            s = monitor.Poll("a", true)!;
            Check("complete row updates and total fallback", s.TotalTokens is null && s.EffectiveTotalTokens == 105);
            Check("explicit cache zero preserved", s.CachedInputTokens == 0 && s.CacheHitPercent == 0);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L })); s = monitor.Poll("a", true)!;
            Check("missing cache not zero", s.CachedInputTokens is null && s.UncachedInputTokens is null && s.CacheHitPercent is null);
            Check("missing reasoning", s.ReasoningOutputTokens is null);
            Check("missing latest call context", s.ContextPercent is null);
            Append(a, Usage(new { input_tokens = 0L, output_tokens = 0L, cached_input_tokens = 0L })); s = monitor.Poll("a", true)!;
            Check("zero denominator undefined", s.EffectiveTotalTokens == 0 && s.CacheHitPercent is null);
            Append(a, Usage(new { input_tokens = 100L, cached_input_tokens = 101L, output_tokens = 5L })); s = monitor.Poll("a", true)!;
            Check("cache greater than input flagged", s.CacheHitPercent is null && s.UncachedInputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = -1L, cached_input_tokens = 0L, output_tokens = 5L, total_tokens = -1L })); s = monitor.Poll("a", true)!;
            Check("negative flagged without clamping", s.EffectiveTotalTokens is null && s.UncachedInputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = long.MaxValue, output_tokens = 5L })); s = monitor.Poll("a", true)!;
            Check("sum overflow flagged", s.EffectiveTotalTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = "invalid", output_tokens = 5L, cached_input_tokens = 0L })); s = monitor.Poll("a", true)!;
            Check("invalid numeric type", s.InputTokens is null && s.Issue is not null);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L, total_tokens = "invalid" })); s = monitor.Poll("a", true)!;
            Check("invalid reported total prevents plausible fallback", s.TotalTokenInvalid && s.EffectiveTotalTokens is null && s.Issue is not null);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Append(a, Usage(new { input_tokens = 123L, output_tokens = 2L, total_tokens = 125L })); s = monitor.Poll("a")!;
            Check("complete appended row visible within two seconds", s.TotalTokens == 125 && elapsed.Elapsed.TotalSeconds < 2);
            Append(a, Usage(new { input_tokens = 100L, output_tokens = 5L, reasoning_output_tokens = 6L, total_tokens = 106L })); s = monitor.Poll("a", true)!;
            Check("inconsistent total and reasoning flagged", s.Issue!.Contains("不一致") && !s.ReasoningIsValid);
            Append(a, Usage(screenshot));
            Append(a, Event(new { type = "token_count", info = new { total_token_usage = (object?)null } }));
            Check("no usage info does not erase totals", monitor.Poll("a", true)!.TotalTokens == 4_956_088);
            Append(b, Usage(new { input_tokens = 10, output_tokens = 3, total_tokens = 13 }));
            Check("background file cannot replace selected", monitor.Poll("a", true)!.ThreadId == "a");
            s = monitor.Poll("empty")!;
            Check("new empty thread clears previous numbers", s.ThreadId == "empty" && s.TotalTokens is null && !s.UsageRecorded);
            s = monitor.Poll("b")!;
            Check("idle selected conversation switch", s.TotalTokens == 13);
            Check("no selected id never picks newest", monitor.Poll(null) is null);
            Check("unknown thread never picks newest", monitor.Poll("not-written-yet") is null);
            Check("manual select works without IPC", monitor.Poll("a")!.ThreadId == "a");
            var copy = Path.Combine(directory, "archived_sessions", "a.jsonl"); File.Copy(a, copy);
            Check("archive and active dedup", monitor.ListSessions().Count(e => e.ThreadId == "a") == 1);
            File.Delete(a); Check("archive move retains counts", monitor.Poll("a", true)!.TotalTokens == 4_956_088);
            File.WriteAllText(copy, Meta("a") + (char)10 + Usage(new { input_tokens = 1, output_tokens = 2 }) + (char)10, utf8);
            Check("file truncation reread", monitor.Poll("a", true)!.EffectiveTotalTokens == 3);
            var rawModel = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "relay/中文模型" } }, JsonOptions) + (char)10);
            var splitAt = Array.IndexOf(rawModel, (byte)0xE4) + 1;
            using (var append = new FileStream(copy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(rawModel.AsSpan(0, splitAt));
            Check("partial UTF8 codepoint retains previous model", monitor.Poll("a", true)!.Model is null);
            using (var append = new FileStream(copy, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(rawModel.AsSpan(splitAt));
            Check("UTF8 codepoint split across reads", monitor.Poll("a", true)!.Model == "relay/中文模型");
            var replacement = Meta("a") + (char)10 + Usage(new { input_tokens = 2, output_tokens = 1 }) + (char)10;
            File.WriteAllText(copy, replacement, utf8); monitor.Poll("a", true);
            File.WriteAllText(copy, replacement.Replace("\"input_tokens\":2", "\"input_tokens\":9"), utf8);
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(1));
            Check("same-length rewritten log restarts reader", monitor.Poll("a", true)!.EffectiveTotalTokens == 10);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2, total_tokens = 1.5 })); s = monitor.Poll("a", true)!;
            Check("fractional reported total flagged", s.TotalTokenInvalid && s.EffectiveTotalTokens is null && s.Issue is not null);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2 }, new { total_tokens = 100 }, 0)); s = monitor.Poll("a", true)!;
            Check("zero log window falls back instead of dividing", s.ContextPercent is null && s.ContextWindowTokens is null);
            Append(copy, Usage(new { input_tokens = 1, output_tokens = 2 }, new { total_tokens = 100 }, 50)); s = monitor.Poll("a", true)!;
            Check("context above window reported as abnormal", s.ContextPercent == 200 && s.Issue is not null);
            var route = new ActiveThreadRouteStatus("a", 1, true, 1, null);
            Check("route only explicit current", FollowSelection.Resolve(route, null) == "a");
            Check("pin overrides auto route", FollowSelection.Resolve(route, "b") == "b");
            Check("disconnect clears auto", FollowSelection.Resolve(route with { IsConnected = false }, null) is null);
            Check("no identity clears auto", FollowSelection.Resolve(route with { ThreadId = null }, null) is null);
            Check("multiple windows require manual", FollowSelection.Resolve(route with { ActiveWindowCount = 2 }, null) is null);
            Check("pin survives disconnect", FollowSelection.Resolve(route with { IsConnected = false }, "b") == "b");
            var settings = OverlaySettings.CreateDefault(); settings.PinnedThreadId = "b"; settings.SessionRoot = directory;
            var saved = Path.Combine(directory, "settings.json"); settings.Save(saved); var loaded = OverlaySettings.Load(saved);
            Check("settings persistence", loaded.PinnedThreadId == "b" && loaded.SessionRoot == directory && loaded.CollapsedSecondaryField == DisplayField.CacheHitRate);
            Check("corrupt settings recover", OverlaySettings.ParseJson("bad-json").Settings.CollapsedPrimaryField == DisplayField.Total);
            ClipboardRetryTests.Run(Check);
            ClipboardPreservationTests.Run(Check);
            TitleBarPlacementTests.Run(Check, directory);
            ConversationSwitchTests.Run(Check, directory);
            PageReadinessTests.Run(Check, directory);
            PageDiagnosticsTests.Run(Check, directory);
            RecoveryTests.Run(Check, directory);
            SidebarIdentificationTests.Run(Check, directory);
            DebugRouteTests.Run(Check);
            SessionCostTests.Run(Check, directory);
            LiveUsageTests.Run(Check, directory);
            RelatedUsageTests.Run(Check, directory);
            ContextWindowTests.Run(Check, directory);
            foreach (var dpi in new uint[] { 96, 120, 144, 192 })
            foreach (var scale in new[] { 60, 100, 130 })
            {
                var layout = OverlayLayoutCalculator.Calculate(new(DiagnosticsRunner.Host(dpi), AnchorMode.InsideBottomRight, true, 5, true, new Point(700, 1600), scale));
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
}
