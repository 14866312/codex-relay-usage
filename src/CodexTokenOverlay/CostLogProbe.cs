namespace CodexTokenOverlay;

// Opt-in read-only diagnostic. Hypothetical rates stay in memory; no IDs, models, token counts or costs are emitted.
internal static class CostLogProbe
{
    internal sealed record Result(bool ReadSucceeded, string Status, int RecordedCalls, int PricedCalls,
        int ModelCount, int TierCount, bool FormulaMatches, bool CachedRecalculationMatches, IReadOnlyList<string> Issues);
    public static Result Run(string root, string threadId)
    {
        using var monitor = new TokenLogMonitor(root);
        var ledger = monitor.Poll(threadId, forceFullScan: true)?.Ledger;
        if (ledger is null) return new(false, "未读取到会话", 0, 0, 0, 0, false, false, Array.Empty<string>());
        var models = ledger.Calls.Select(c => c.Model).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var profiles = models.Select((model, i) => new ModelPriceProfile("probe-" + i, "诊断示例", new[] { model },
            PriceEntryMode.EffectivePrices, 1m, true, 272_000, new(1m, 1m, 1m, 1m), new(2m, 2m, 2m, 2m))).ToArray();
        var calculator = new SessionCostCalculator(); var prices = new PricingRevision(1, new(1, profiles));
        var result = calculator.Calculate(ledger, prices);
        // Mirrors the calculator's rule: Gemini's thinking tokens bill at the output rate.
        decimal Expected(UsageCall c) => ((decimal)c.Usage.Input!.Value + SessionCostCalculator.BillableOutput(c.Usage, c.Model)!.Value)
            / 1_000_000m * (c.Usage.Input > 272_000 ? 2m : 1m);
        var expected = ledger.Calls.Where(c => c.Issue is null && c.Usage.ValidationIssue is null && c.Model is not null
                && (SessionCostCalculator.IsGemini(c.Model) || c.Usage.Reasoning <= c.Usage.Output)
                && SessionCostCalculator.BillableOutput(c.Usage, c.Model) is not null)
            .Sum(Expected);
        var again = calculator.Calculate(ledger, prices);
        return new(true, result.StateText, result.RecordedCalls, result.PricedCalls, models.Length, result.Groups.Count,
            result.Amounts?.Total == expected, ReferenceEquals(result, again) && calculator.LastEvaluatedCallCount == 0,
            result.Issues.Concat(result.Unpriced.Select(c => c.Reason ?? "未提供")).Distinct().ToArray());
    }
}
