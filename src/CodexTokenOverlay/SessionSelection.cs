namespace CodexTokenOverlay;

internal sealed record SessionReadRequest(long Revision, string? ThreadId);
internal sealed record ConversationProbeSample(DateTime TimestampUtc, ActiveThreadRouteStatus Route,
    long SelectionRevision, string? SelectedThreadId, string? PublishedThreadId, long? TotalTokens,
    int? TurnCount, string? Error);

// UI-owned selection state. A request cannot publish across A -> B -> A or a log-directory change.
internal sealed class SessionSelection
{
    private (string? ThreadId, string? ManualId, long RouteVersion, long ConfigurationRevision)? _key;
    public long Revision { get; private set; }
    public string? ThreadId { get; private set; }
    public TokenSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public bool Observe(ActiveThreadRouteStatus route, string? manualId, long configurationRevision)
    {
        var key = (FollowSelection.Resolve(route, manualId), manualId, manualId is null ? route.Version : 0, configurationRevision);
        if (_key == key) return false;
        _key = key; Revision++; ThreadId = key.Item1; Snapshot = null; Error = null;
        return true;
    }
    public SessionReadRequest Request() => new(Revision, ThreadId);
    public bool TryPublish(SessionReadRequest request, TokenSnapshot? snapshot, string? error)
    {
        if (request.Revision != Revision || !string.Equals(request.ThreadId, ThreadId, StringComparison.OrdinalIgnoreCase) ||
            (snapshot is not null && !string.Equals(snapshot.ThreadId, ThreadId, StringComparison.OrdinalIgnoreCase))) return false;
        Snapshot = snapshot; Error = error; return true;
    }
}
