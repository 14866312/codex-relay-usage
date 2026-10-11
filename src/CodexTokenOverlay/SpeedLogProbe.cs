namespace CodexTokenOverlay;

// Opt-in read-only diagnostic for the generation-only speed definition. Timing aggregates
// are reported; session IDs, model names and token counts never leave the process.
internal static class SpeedLogProbe
{
    internal sealed record Result(bool ReadSucceeded, bool HasSpeed, double? OutputSpeed, bool SpeedGenerationOnly,
        double? GenerationSeconds, double? ToolSeconds, double? GenerationShare);
    public static Result Run(string root, string threadId)
    {
        using var monitor = new TokenLogMonitor(root) { PreferredThreadId = threadId };
        var snapshot = monitor.Poll(forceFullScan: true);
        if (snapshot is null) return new(false, false, null, false, null, null, null);
        var generation = snapshot.GenerationSeconds;
        var measuredSeconds = generation + (snapshot.ToolSeconds ?? 0);
        var share = generation is > 0 && measuredSeconds > 0 ? generation / measuredSeconds : null;
        return new(true, snapshot.OutputSpeed is > 0, snapshot.OutputSpeed, snapshot.SpeedGenerationOnly,
            generation, snapshot.ToolSeconds, share);
    }
}
