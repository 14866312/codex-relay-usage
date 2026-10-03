using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class SessionCostTests
{
    internal static readonly UsageAmounts SampleUsage = new(97_125, 3_610, 0, 478, 100, 97_603);
    internal static ModelPriceProfile Profile(string model = "relay/test", TokenPrices? low = null) =>
        new("sample", "示例价格", new[] { model }, PriceEntryMode.BaseWithMultiplier, .35m, false, 272_000,
            low ?? new(2m, .1m, .2m, 10m), new(4m, .2m, .4m, 15m));
    private static PricingRevision Prices(params ModelPriceProfile[] profiles) => new(1, new(1, profiles));
    internal static object Usage(UsageAmounts u) => new { input_tokens = u.Input, cached_input_tokens = u.CacheRead,
        cache_write_input_tokens = u.CacheWrite, output_tokens = u.Output, reasoning_output_tokens = u.Reasoning, total_tokens = u.Total };
    private static void Context(SessionUsageLedger ledger, string turn = "turn", string model = "relay/test") =>
        ledger.ObserveContext(JsonSerializer.SerializeToElement(new { turn_id = turn, model }));
    private static object Payload(string response, UsageAmounts usage, UsageAmounts? expected = null,
        string? turn = "turn", string? session = "session", string? thread = "test") =>
        new { thread_id = thread, session_id = session, response_id = response, turn_id = turn,
            usage = Usage(usage), thread_token_usage = expected is null ? null : Usage(expected) };
    private static void Record(SessionUsageLedger ledger, string response, UsageAmounts usage, UsageAmounts? expected = null,
        string? turn = "turn", string? session = "session", string? thread = "test") =>
        ledger.ObserveRecord(JsonSerializer.SerializeToElement(Payload(response, usage, expected, turn, session, thread)));
    private static SessionUsageLedger Single(UsageAmounts? usage = null, string model = "relay/test", bool aggregate = true)
    {
        var ledger = new SessionUsageLedger("test"); Context(ledger, model: model);
        Record(ledger, "response", usage ?? SampleUsage, aggregate ? usage ?? SampleUsage : null); return ledger;
    }
    internal static SessionCostResult SampleResult() => new SessionCostCalculator().Calculate(Single().Snapshot(), Prices(Profile()));
    internal static void Run(Action<string, bool> check, string directory)
    {
        CalculationTests(check);
        LedgerTests(check);
        ReaderTests(check, Path.Combine(directory, "cost-log"));
        ConfigurationTests(check, directory);
        PublicationTests(check);
    }
    private static void CalculationTests(Action<string, bool> check)
    {
        var calculator = new SessionCostCalculator(); var ledger = Single(); var prices = Prices(Profile());
        var r = calculator.Calculate(ledger.Snapshot(), prices);
        check("cost screenshot exactly $0.06725985", r.Status == SessionCostStatus.Complete && r.Amounts?.Total == .06725985m);
        check("cost screenshot normal/read/write/output partitions", r.Amounts == new CostAmounts(.0654605m, .00012635m, 0m, .001673m));
        var effective = Profile() with { Mode = PriceEntryMode.EffectivePrices, Multiplier = 123m, Low = new(.7m, .035m, .07m, 3.5m) };
        var direct = new SessionCostCalculator().Calculate(ledger.Snapshot(), Prices(effective));
        check("direct prices equal base times multiplier and never multiply twice", direct.Amounts == r.Amounts);
        check("unchanged ledger does not evaluate calls again", ReferenceEquals(r, calculator.Calculate(ledger.Snapshot(), prices)) && calculator.LastEvaluatedCallCount == 0);
        var doubleUsage = new UsageAmounts(194_250, 7_220, 0, 956, 200, 195_206);
        Record(ledger, "second", SampleUsage, doubleUsage);
        var appended = calculator.Calculate(ledger.Snapshot(), prices);
        check("appended ledger evaluates only new call", calculator.LastEvaluatedCallCount == 1 && appended.Amounts?.Total == .1345197m);
        var revised = calculator.Calculate(ledger.Snapshot(), new(2, prices.Settings with { Profiles = new[] { Profile() with { Multiplier = .7m } } }));
        check("multiplier edit revalues every historical call", calculator.LastEvaluatedCallCount == 2 && revised.Amounts?.Total == .2690394m);
        var tiered = Profile(low: new(1m, 0m, 0m, 0m)) with { TwoTiers = true, Multiplier = 1m, High = new(2m, 0m, 0m, 0m) };
        var boundary = Single(new(272_000, 0, 0, 0));
        var low = new SessionCostCalculator().Calculate(boundary.Snapshot(), Prices(tiered));
        var high = new SessionCostCalculator().Calculate(Single(new(272_001, 0, 0, 0)).Snapshot(), Prices(tiered));
        check("272000 chooses low tier", low.Amounts?.Total == .272m && low.Groups.Single().Tier.StartsWith('≤'));
        check("272001 whole call chooses high tier", high.Amounts?.Total == .544002m && high.Groups.Single().Tier.StartsWith('>'));
        var cachedHigh = new SessionCostCalculator().Calculate(Single(new(272_001, 272_000, 0, 0)).Snapshot(), Prices(tiered));
        check("tier input includes cache", cachedHigh.Amounts?.Total == .000002m);
        var twoLow = Single(new(200_000, 0, 0, 0)); Record(twoLow, "r2", new(200_000, 0, 0, 0), new(400_000, 0, 0, 0));
        check("session cumulative input does not select high tier", new SessionCostCalculator().Calculate(twoLow.Snapshot(), Prices(tiered)).Amounts?.Total == .4m);
        var tierCalculator = new SessionCostCalculator(); tierCalculator.Calculate(boundary.Snapshot(), Prices(tiered));
        var changedThreshold = tierCalculator.Calculate(boundary.Snapshot(), new(2, new(1, new[] { tiered with { Threshold = 200_000 } })));
        check("threshold edit revalues past calls", changedThreshold.Amounts?.Total == .544m);
        var write = new SessionCostCalculator().Calculate(Single(new(1_000, 200, 100, 50, 20, 1_050)).Snapshot(), Prices(Profile()));
        check("cache write removed from normal input and priced once", write.Amounts == new CostAmounts(.00049m, .000007m, .000007m, .000175m));
        var noReasoning = new SessionCostCalculator().Calculate(Single(SampleUsage with { Reasoning = null }).Snapshot(), prices);
        check("reasoning already included and optional", noReasoning.Amounts == r.Amounts);
        var mismatchedReasoning = Single(); Record(mismatchedReasoning, "second", SampleUsage, doubleUsage with { Reasoning = 199 });
        var reasoningResult = new SessionCostCalculator().Calculate(mismatchedReasoning.Snapshot(), prices);
        check("reported reasoning aggregate mismatch makes estimate partial without extra charge", reasoningResult.Status == SessionCostStatus.Partial && reasoningResult.Amounts?.Total == .1345197m);
        var zero = new SessionCostCalculator().Calculate(Single(new(0, 0, 0, 0)).Snapshot(), Prices(Profile(low: new())));
        check("explicit zero quantities require no prices", zero.Status == SessionCostStatus.Complete && zero.Amounts?.Total == 0m);
        var zeroPrice = new SessionCostCalculator().Calculate(Single().Snapshot(), Prices(Profile(low: new(0m, 0m, null, 0m))));
        check("explicit zero prices are configured and free", zeroPrice.Status == SessionCostStatus.Complete && zeroPrice.Amounts?.Total == 0m);
        var missingPrice = new SessionCostCalculator().Calculate(Single().Snapshot(), Prices(Profile(low: new(null, .1m, null, 10m))));
        check("blank positive input price is unpriced", missingPrice.Status == SessionCostStatus.Partial && missingPrice.Amounts is null && missingPrice.Unpriced.Single().Reason!.Contains("单价"));
        var overflow = new SessionCostCalculator().Calculate(Single().Snapshot(), Prices(Profile(low: new(decimal.MaxValue, .1m, null, 10m)) with { Multiplier = decimal.MaxValue }));
        check("decimal overflow listed without a plausible fee", overflow.Status == SessionCostStatus.Partial && overflow.Unpriced.Single().Reason!.Contains("超出范围"));
        var freeMultiplier = new SessionCostCalculator().Calculate(Single(new(long.MaxValue, 0, 0, 0)).Snapshot(),
            Prices(Profile(low: new(decimal.MaxValue)) with { Multiplier = 0m }));
        check("explicit zero multiplier costs zero without intermediate overflow", freeMultiplier.Status == SessionCostStatus.Complete && freeMultiplier.Amounts?.Total == 0m);
        var mixed = Single(); Context(mixed, "turn2", "relay/second"); Record(mixed, "r2", SampleUsage, doubleUsage, "turn2");
        var mixedPrices = Prices(Profile(), Profile("relay/second") with { Id = "second", Multiplier = .7m });
        var mixedResult = new SessionCostCalculator().Calculate(mixed.Snapshot(), mixedPrices);
        check("models accumulate separately", mixedResult.Amounts?.Total == .20177955m && mixedResult.Groups.Count == 2 && mixedResult.Status == SessionCostStatus.Complete);
        var partlyPriced = new SessionCostCalculator().Calculate(mixed.Snapshot(), prices);
        check("known fee remains a partial subtotal when another model unpriced", partlyPriced.Amounts?.Total == .06725985m && partlyPriced.PricedCalls == 1 && partlyPriced.Status == SessionCostStatus.Partial);
        var alias = Profile() with { Models = new[] { "relay/test", "other/test" } };
        check("explicit full alias binding shares scheme", new SessionCostCalculator().Calculate(Single(model: "other/test").Snapshot(), Prices(alias)).Amounts == r.Amounts);
        check("prefix is retained and never inferred", new SessionCostCalculator().Calculate(Single(model: "test").Snapshot(), Prices(alias)).Amounts is null);
        check("model matching is case sensitive", new SessionCostCalculator().Calculate(Single(model: "Relay/test").Snapshot(), Prices(alias)).Amounts is null);
        check("compact cost four decimal places", CostFormatting.Money(.06725985m, true) == "$0.0673");
        check("detailed cost eight decimal places", CostFormatting.Money(.06725985m) == "$0.06725985");
        check("small compact positive not displayed as zero", CostFormatting.Money(.000000001m, true) == "<$0.0001");
        check("small detail positive not displayed as zero", CostFormatting.Money(.000000001m) == "<$0.00000001");
        check("actual zero displayed as zero", CostFormatting.Money(0m, true) == "$0.0000");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var canceledCorrectly = false;
        try { calculator.Calculate(ledger.Snapshot(), prices, canceled.Token); } catch (OperationCanceledException) { canceledCorrectly = true; }
        check("cost computation honors cancellation", canceledCorrectly);
    }
    private static void LedgerTests(Action<string, bool> check)
    {
        var ledger = Single(); var first = ledger.Snapshot(); Record(ledger, "response", SampleUsage, SampleUsage);
        check("repeated response is not billed twice", ledger.Snapshot().Calls.Count == 1 && ledger.Snapshot().Version == first.Version);
        Record(ledger, "second", SampleUsage, new(194_250, 7_220, 0, 956));
        check("different responses with identical usage both billed", new SessionCostCalculator().Calculate(ledger.Snapshot(), Prices(Profile())).Amounts?.Total == .1345197m);
        var acrossSession = Single(); Record(acrossSession, "response", SampleUsage, new(194_250, 7_220, 0, 956), session: "session2");
        check("response dedup key includes session", acrossSession.Snapshot().Calls.Count == 2);
        var foreign = Single(); Record(foreign, "foreign", SampleUsage, SampleUsage, thread: "another-thread");
        check("other threads never enter ledger", foreign.Snapshot().Calls.Count == 1);
        var conflict = Single(); Record(conflict, "response", SampleUsage with { Output = 479, Total = 97_604 }, SampleUsage);
        var conflictResult = new SessionCostCalculator().Calculate(conflict.Snapshot(), Prices(Profile()));
        check("conflicting request excluded and listed", conflictResult.Status == SessionCostStatus.Partial && conflictResult.Amounts is null && conflictResult.Unpriced.Single().Reason!.Contains("冲突"));
        var unknown = new SessionUsageLedger("test"); Context(unknown, "another-turn"); Record(unknown, "response", SampleUsage, SampleUsage);
        check("unmatched turn never borrows most recent model", unknown.Snapshot().Calls.Single().Model is null);
        var unknownResult = new SessionCostCalculator().Calculate(unknown.Snapshot(), Prices(Profile()));
        check("unknown model has explicit missing reason", unknownResult.Unpriced.Single().Reason!.Contains("模型未知"));
        Context(unknown); var backfilled = new SessionCostCalculator().Calculate(unknown.Snapshot(), Prices(Profile()));
        check("later matching turn context backfills model", backfilled.Status == SessionCostStatus.Complete && backfilled.Amounts?.Total == .06725985m);
        Context(unknown, model: "relay/other");
        check("ambiguous turn context clears guessed model", unknown.Snapshot().Calls.Single().Model is null);
        var missingIdentity = new SessionUsageLedger("test"); Context(missingIdentity); Record(missingIdentity, "response", SampleUsage, SampleUsage, session: null);
        check("missing request identity is unpriced", new SessionCostCalculator().Calculate(missingIdentity.Snapshot(), Prices(Profile())).Unpriced.Single().Reason!.Contains("身份"));
        var fields = new[] { "Input", "CacheRead", "CacheWrite", "Output" };
        for (var i = 0; i < fields.Length; i++)
        {
            var u = i switch { 0 => SampleUsage with { Input = null }, 1 => SampleUsage with { CacheRead = null },
                2 => SampleUsage with { CacheWrite = null }, _ => SampleUsage with { Output = null } };
            var result = new SessionCostCalculator().Calculate(Single(u).Snapshot(), Prices(Profile()));
            check("missing usage " + fields[i] + " never normalized to zero", result.Amounts is null && result.Unpriced.Single().Reason!.Contains("未提供"));
        }
        foreach (var pair in new[] { ("negative", SampleUsage with { Input = -1 }), ("read/write exceed input", SampleUsage with { CacheWrite = 100_000 }),
            ("inconsistent total", SampleUsage with { Total = 1 }), ("reasoning exceeds output", SampleUsage with { Reasoning = 500 }),
            ("integer overflow", new UsageAmounts(long.MaxValue, 0, 0, 1)) })
            check("invalid usage " + pair.Item1 + " excluded", new SessionCostCalculator().Calculate(Single(pair.Item2).Snapshot(), Prices(Profile())).Amounts is null);
        var fractional = UsageAmounts.Parse(JsonSerializer.SerializeToElement(new { input_tokens = 1.5, cached_input_tokens = 0, cache_write_input_tokens = 0, output_tokens = 1 }));
        var tooBig = UsageAmounts.Parse(JsonSerializer.SerializeToElement(new { input_tokens = "9223372036854775808", cached_input_tokens = 0, cache_write_input_tokens = 0, output_tokens = 0 }));
        check("fractional quantity flagged", fractional.ParseIssue is not null);
        check("overflow/string quantity flagged", tooBig.ParseIssue is not null);
        var gap = Single(); Record(gap, "second", SampleUsage, new(300_000, 7_220, 0, 956));
        var gapResult = new SessionCostCalculator().Calculate(gap.Snapshot(), Prices(Profile()));
        check("history gap cannot claim complete estimate", gapResult.Status == SessionCostStatus.Partial && gapResult.Amounts?.Total == .1345197m && gapResult.Issues.Count > 0);
        check("missing aggregate cannot claim complete estimate", new SessionCostCalculator().Calculate(Single(aggregate: false).Snapshot(), Prices(Profile())).Status == SessionCostStatus.Partial);
        var legacy = new SessionUsageLedger("test"); legacy.ObserveLegacy(JsonSerializer.SerializeToElement(Usage(SampleUsage)));
        var legacyResult = new SessionCostCalculator().Calculate(legacy.Snapshot(), Prices(Profile()));
        check("legacy cumulative-only logs never billed", legacyResult.Status == SessionCostStatus.Unavailable && legacyResult.Amounts is null);
        var totalOnly = new SessionUsageLedger("test"); totalOnly.ObserveLegacy(JsonSerializer.SerializeToElement(new { total_tokens = 500 }));
        check("total-only legacy snapshot reports unavailable instead of waiting", new SessionCostCalculator().Calculate(totalOnly.Snapshot(), Prices(Profile())).Status == SessionCostStatus.Unavailable);
        check("empty conversation waits instead of showing zero dollars", new SessionCostCalculator().Calculate(new SessionUsageLedger("test").Snapshot(), Prices(Profile())).Status == SessionCostStatus.Waiting);
        var legacyGap = Single(); legacyGap.ObserveLegacy(JsonSerializer.SerializeToElement(Usage(new(200_000, 0, 0, 1_000))));
        check("new records cannot conceal older uncovered cumulative usage", new SessionCostCalculator().Calculate(legacyGap.Snapshot(), Prices(Profile())).Status == SessionCostStatus.Partial);
        var compaction = Single(); compaction.ObserveLegacy(JsonSerializer.SerializeToElement(new { input_tokens = 100_000, cached_input_tokens = 3_610, output_tokens = 478, total_tokens = 1 }));
        check("incoherent compaction snapshot not added as billable tokens", new SessionCostCalculator().Calculate(compaction.Snapshot(), Prices(Profile())).Status == SessionCostStatus.Complete);
    }
    private static void ReaderTests(Action<string, bool> check, string directory)
    {
        var sessions = Path.Combine(directory, "sessions"); var archives = Path.Combine(directory, "archived_sessions");
        Directory.CreateDirectory(sessions); Directory.CreateDirectory(archives);
        var path = Path.Combine(sessions, "test.jsonl"); var encoding = new UTF8Encoding(false);
        string Row(string type, object payload) => JsonSerializer.Serialize(new { type, payload }) + '\n';
        var start = Row("session_meta", new { id = "test" }) + Row("turn_context", new { turn_id = "turn", model = "relay/test" });
        var sample = Row("token_usage_record", Payload("response", SampleUsage, SampleUsage));
        File.WriteAllText(path, start + sample, encoding);
        using var monitor = new TokenLogMonitor(directory) { PreferredThreadId = "test" };
        var snapshot = monitor.Poll(true)!; var calculator = new SessionCostCalculator(); var prices = Prices(Profile());
        var r = calculator.Calculate(snapshot.Ledger!, prices);
        check("modern records parsed by actual reader", r.Amounts?.Total == .06725985m);
        var modernPresentation = OverlayPresentationBuilder.Create(snapshot, DisplayField.Total, DisplayField.Cost, DisplayField.Cost, r);
        check("modern fee visible before cumulative token snapshot arrives", !snapshot.UsageRecorded && modernPresentation.StatusText is null && modernPresentation.Secondary.Value == "估算 $0.0673");
        var legacy = Row("event_msg", new { type = "token_count", info = new { total_token_usage = Usage(SampleUsage) } });
        File.AppendAllText(path, legacy + legacy, encoding); snapshot = monitor.Poll(true)!;
        check("cumulative snapshots display tokens without extra fees", snapshot.TotalTokens == 97_603 && calculator.Calculate(snapshot.Ledger!, prices).Amounts?.Total == .06725985m);
        var next = Row("token_usage_record", Payload("second", SampleUsage, new(194_250, 7_220, 0, 956)));
        var split = next.Length / 2; File.AppendAllText(path, next[..split], encoding);
        var partial = monitor.Poll(true)!;
        check("half-line does not clear or bill incomplete record", partial.Ledger!.Calls.Count == 1 && calculator.Calculate(partial.Ledger, prices).Amounts?.Total == .06725985m);
        File.AppendAllText(path, next[split..], encoding); snapshot = monitor.Poll(true)!;
        check("completed half-line incrementally billed once", snapshot.Ledger!.Calls.Count == 2 && calculator.Calculate(snapshot.Ledger, prices).Amounts?.Total == .1345197m);
        File.AppendAllText(path, sample, encoding); snapshot = monitor.Poll(true)!;
        check("replayed older record cannot roll back aggregate", calculator.Calculate(snapshot.Ledger!, prices).Status == SessionCostStatus.Complete);
        var archive = Path.Combine(archives, "test.jsonl"); File.Copy(path, archive); snapshot = monitor.Poll(true)!;
        check("active and archived copy billed once", monitor.ListSessions().Count == 1 && calculator.Calculate(snapshot.Ledger!, prices).Amounts?.Total == .1345197m);
        File.Delete(path); snapshot = monitor.Poll(true)!;
        check("archive-only handoff retains complete fee", calculator.Calculate(snapshot.Ledger!, prices).Status == SessionCostStatus.Complete && snapshot.Ledger!.Calls.Count == 2);
        var oldId = snapshot.Ledger!.LedgerId; File.WriteAllText(archive, start, encoding); snapshot = monitor.Poll(true)!;
        check("log truncation replaces ledger and clears fee", snapshot.Ledger!.LedgerId != oldId && calculator.Calculate(snapshot.Ledger, prices).Status == SessionCostStatus.Waiting);
        File.WriteAllText(path, start + string.Concat(Enumerable.Repeat(sample, 250)), encoding);
        var reader = new IncrementalSessionReader(path, "test"); using var cancellation = new CancellationTokenSource(); var didCancel = false;
        try { reader.Read(cancellation.Token, () => cancellation.Cancel()); } catch (OperationCanceledException) { didCancel = true; }
        var resumed = reader.Read();
        check("canceled incremental read resumes with dedup intact", didCancel && resumed!.Ledger!.Calls.Count == 1 &&
            calculator.Calculate(resumed.Ledger, prices).Amounts?.Total == .06725985m);
    }
    private static void ConfigurationTests(Action<string, bool> check, string directory)
    {
        var prices = Prices(Profile() with { Models = new[] { "relay/test", "relay/alias" }, Low = new(0m, .1m, null, 10m) }).Settings;
        var path = Path.Combine(directory, "prices.json");
        check("missing price config starts blank", PricingStore.Load(path).Settings.Profiles.Count == 0);
        check("price configuration atomic save", PricingStore.TrySave(prices, out _, path));
        var loaded = PricingStore.Load(path);
        check("price config preserves blanks zeros and aliases", loaded.Error is null && loaded.Settings.ForModel("relay/alias")!.Low == prices.Profiles[0].Low);
        var before = File.ReadAllText(path);
        using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var success = PricingStore.TrySave(prices with { Profiles = new[] { Profile() with { Multiplier = .9m } } }, out var error, path);
            check("failed atomic save keeps original prices", !success && error is not null && File.ReadAllText(path) == before);
        }
        check("failed price save cleans temporary file", Directory.GetFiles(directory, "prices.json.*.tmp").Length == 0);
        File.WriteAllText(path, "{broken"); loaded = PricingStore.Load(path);
        check("corrupt price file reports failure without overwriting", loaded.Error is not null && File.ReadAllText(path) == "{broken");
        File.WriteAllText(path, "{\"Version\":999,\"Profiles\":[]}");
        check("unsupported price config version reports failure", PricingStore.Load(path).Error is not null);
        check("duplicate alias rejected", Prices(Profile(), Profile() with { Id = "different" }).Settings.ValidationIssue() is not null);
        check("negative price rejected", Prices(Profile(low: new(-1m))).Settings.ValidationIssue() is not null);
        check("negative multiplier rejected", Prices(Profile() with { Multiplier = -1m }).Settings.ValidationIssue() is not null);
        check("negative threshold rejected", Prices(Profile() with { Threshold = -1 }).Settings.ValidationIssue() is not null);
        var settings = OverlaySettings.CreateDefault(); settings.ManualPlacementEnabled = true; settings.OverlayScalePercent = 120;
        settings.VisibleFields |= DisplayField.Cost; settings.SelectCollapsedField(CollapsedSlot.Secondary, DisplayField.Cost);
        var settingsPath = Path.Combine(directory, "cost-settings.json"); settings.Save(settingsPath); var restored = OverlaySettings.Load(settingsPath);
        check("1.1.0 retains top/manual placement and scaling", restored.SettingsVersion == 3 && restored.ManualPlacementEnabled && restored.OverlayScalePercent == 120 && restored.MainAttachment == settings.MainAttachment);
        check("cost display field and compact selection persist", restored.VisibleFields.HasFlag(DisplayField.Cost) && restored.CollapsedSecondaryField == DisplayField.Cost);
        var r = SampleResult(); var shown = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example(), DisplayField.Total, DisplayField.Cost, DisplayField.Cost, r);
        check("expanded cost includes four components", shown.ExpandedRows.Count == 5 && shown.Secondary.HasValue && shown.EstimateText is not null && shown.IssueText is null);
        check("compact cost does not repeat label", OverlayPresentationBuilder.CompactText(shown.Secondary) == "估算 $0.0673");
        var hidden = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example(), DisplayField.Total, DisplayField.CacheHitRate, DisplayField.Output, r);
        check("hiding cost field also hides its four components", hidden.ExpandedRows.All(row => row.Field != DisplayField.Cost));
        var unavailable = OverlayPresentationBuilder.Create(DiagnosticsRunner.Example(), DisplayField.Cost, DisplayField.Total, DisplayField.Cost,
            new SessionCostCalculator().Calculate(Single().Snapshot(), new(1, PricingSettings.Empty)));
        check("missing prices have no numeric display value", !unavailable.Primary.HasValue && unavailable.Primary.Value.Contains('—'));
    }
    private static void PublicationTests(Action<string, bool> check)
    {
        var selection = new SessionSelection(); var route = new ActiveThreadRouteStatus("test", 1, true, 1, null);
        selection.Observe(route, null, 0); var first = selection.Request(); var ledger = Single().Snapshot();
        var result = new SessionCostCalculator().Calculate(ledger, Prices(Profile()));
        var snapshot = DiagnosticsRunner.Example() with { ThreadId = "test", Ledger = ledger };
        selection.TryPublish(first, snapshot, null);
        check("matching fee can publish", CostPublication.Matches(first, selection.Revision, selection.Snapshot, 1, result));
        selection.Observe(route with { ThreadId = "other", Version = 2 }, null, 0);
        check("switch immediately removes previous token snapshot", selection.Snapshot is null);
        check("late fee cannot publish to another conversation", !CostPublication.Matches(first, selection.Revision, snapshot, 1, result));
        selection.Observe(route with { Version = 3 }, null, 0); selection.TryPublish(selection.Request(), snapshot, null);
        check("A B A rejects original A fee", !CostPublication.Matches(first, selection.Revision, selection.Snapshot, 1, result));
        var current = selection.Request();
        check("old price result rejected while valid tokens stay", !CostPublication.Matches(current, selection.Revision, selection.Snapshot, 2, result) && selection.Snapshot!.TotalTokens == snapshot.TotalTokens);
        check("old ledger version rejected", !CostPublication.Matches(current, selection.Revision, snapshot with { Ledger = ledger with { Version = ledger.Version + 1 } }, 1, result));
        check("replacement ledger identity rejected", !CostPublication.Matches(current, selection.Revision, snapshot with { Ledger = ledger with { LedgerId = Guid.NewGuid() } }, 1, result));
        var requests = new List<SessionReadRequest>();
        for (var i = 0; i < 60; i++) { requests.Add(selection.Request()); selection.Observe(route with { Version = i + 10 }, null, 0); }
        check("sixty rapid switches reject all obsolete fees", requests.AsEnumerable().Reverse().All(req => !CostPublication.Matches(req, selection.Revision, snapshot, 1, result)));
    }
}
