namespace CodexTokenOverlay;

internal static class CostPublication
{
    public static bool Matches(SessionReadRequest request, long currentRevision, TokenSnapshot? snapshot,
        long currentPriceVersion, SessionCostResult? cost) => request.Revision == currentRevision
        && snapshot?.Ledger is { } ledger && cost is not null
        && string.Equals(request.ThreadId, snapshot.ThreadId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(cost.ThreadId, snapshot.ThreadId, StringComparison.OrdinalIgnoreCase)
        && cost.LedgerId == ledger.LedgerId && cost.LedgerVersion == ledger.Version && cost.PricingVersion == currentPriceVersion;
}
