using System.Globalization;
using System.Text.Json;

namespace CodexTokenOverlay;

// Explicit thread relationships parsed from the session_meta line only.
// Titles, messages, prompts and turn contents are never part of this model.
internal enum RelatedSourceKind { Unknown, User, Subagent, AgentCreated, Other }

internal sealed record SessionRelationInfo(
    string ThreadId, string? ParentThreadId, RelatedSourceKind Source, string? ThreadSource,
    string? ForkedFromId, int? SubagentDepth, string? AgentPath, string? AgentRole, string? Conflict)
{
    public bool IsSubagent => Source == RelatedSourceKind.Subagent;

    internal static SessionRelationInfo Parse(string threadId, JsonElement meta)
    {
        var threadSource = IncrementalSessionReader.String(meta, "thread_source");
        var declaredParent = IncrementalSessionReader.String(meta, "parent_thread_id");
        var forkedFrom = IncrementalSessionReader.String(meta, "forked_from_id");
        string? spawnParent = null, agentPath = null, agentRole = null;
        int? depth = null;
        var kind = threadSource switch
        {
            "subagent" => RelatedSourceKind.Subagent,
            "agent_created_thread" => RelatedSourceKind.AgentCreated,
            "user" => RelatedSourceKind.User,
            null => RelatedSourceKind.Unknown,
            _ => RelatedSourceKind.Other
        };
        if (meta.TryGetProperty("source", out var source))
        {
            if (source.ValueKind == JsonValueKind.String)
            {
                var name = source.GetString();
                if (kind == RelatedSourceKind.Unknown)
                    kind = name is "vscode" or "exec" or "cli" ? RelatedSourceKind.User : RelatedSourceKind.Other;
            }
            else if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out var subagent) &&
                subagent.ValueKind == JsonValueKind.Object && subagent.TryGetProperty("thread_spawn", out var spawn) &&
                spawn.ValueKind == JsonValueKind.Object)
            {
                kind = RelatedSourceKind.Subagent;
                spawnParent = IncrementalSessionReader.String(spawn, "parent_thread_id");
                agentPath = IncrementalSessionReader.String(spawn, "agent_path");
                agentRole = IncrementalSessionReader.String(spawn, "agent_role");
                if (spawn.TryGetProperty("depth", out var reported) && reported.ValueKind == JsonValueKind.Number &&
                    reported.TryGetInt32(out var value)) depth = value;
            }
        }
        // A plain fork or a sidebar thread carries no spawn relationship and never joins a group.
        var parent = kind == RelatedSourceKind.Subagent ? spawnParent ?? declaredParent : null;
        var conflict = kind == RelatedSourceKind.Subagent && spawnParent is not null && declaredParent is not null &&
            !string.Equals(spawnParent, declaredParent, StringComparison.OrdinalIgnoreCase) ? "父关系冲突" : null;
        return new(threadId, parent, kind, threadSource, forkedFrom, depth, agentPath, agentRole, conflict);
    }

    internal string Fingerprint => string.Join('|', ThreadId, ParentThreadId ?? "", ((int)Source).ToString(CultureInfo.InvariantCulture),
        ThreadSource ?? "", ForkedFromId ?? "", Conflict ?? "");
}

// Relationship-only view of the session catalog. Version advances only when the relation set changes.
internal sealed record RelatedSessionCatalogSnapshot(long Version,
    IReadOnlyDictionary<string, SessionRelationInfo> Threads, string Fingerprint = "")
{
    public static readonly RelatedSessionCatalogSnapshot Empty =
        new(0, new Dictionary<string, SessionRelationInfo>(StringComparer.OrdinalIgnoreCase));

    public SessionRelationInfo? Find(string? threadId) =>
        threadId is not null && Threads.TryGetValue(threadId, out var info) ? info : null;
}

internal static class RelatedSessionCatalog
{
    internal static RelatedSessionCatalogSnapshot Build(IEnumerable<SessionEntry> entries, RelatedSessionCatalogSnapshot? previous)
    {
        var threads = new Dictionary<string, SessionRelationInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (entry.Relation is { } relation) threads[entry.ThreadId] = relation;
        var fingerprint = string.Join('\n', threads.Values.Select(t => t.Fingerprint).OrderBy(t => t, StringComparer.Ordinal));
        var version = previous is not null && previous.Fingerprint == fingerprint ? previous.Version : (previous?.Version ?? 0) + 1;
        return new(version, threads, fingerprint);
    }
}

internal enum RelatedMemberKind { Root, Subagent, SideChat, Unconfirmed }

internal sealed record RelatedMemberCandidate(string ThreadId, string? ParentThreadId, RelatedMemberKind Kind,
    string? Reason, SessionRelationInfo? Relation);

// Membership is decided by explicit relations only. Same title, same directory, same time,
// a plain fork or an agent-created thread never prove belonging; conflicts and cycles stay unconfirmed.
internal static class SessionGroupMembership
{
    private const int MaximumDepth = 32;

    internal static IReadOnlyList<RelatedMemberCandidate> Resolve(RelatedSessionCatalogSnapshot catalog,
        string rootThreadId, IReadOnlyList<RuntimeUsageObservation>? runtimeObservations = null)
    {
        var results = new List<RelatedMemberCandidate>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootThreadId };
        results.Add(new(rootThreadId, null, RelatedMemberKind.Root, null, catalog.Find(rootThreadId)));
        var children = BuildChildIndex(catalog);
        // Side chats exist only in the runtime observation; a subagent they spawned still has a log.
        foreach (var observation in runtimeObservations ?? [])
        {
            if (!observation.IsSideConversation || included.Contains(observation.ThreadId)) continue;
            if (observation.ParentThreadId is null || !included.Contains(observation.ParentThreadId))
            {
                included.Add(observation.ThreadId);
                results.Add(new(observation.ThreadId, observation.ParentThreadId, RelatedMemberKind.Unconfirmed, "侧聊父关系未知", null));
                continue;
            }
            included.Add(observation.ThreadId);
            results.Add(new(observation.ThreadId, observation.ParentThreadId, RelatedMemberKind.SideChat, null, null));
        }
        var ancestry = included.ToDictionary(id => id, id => new[] { id }, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(included);
        var depth = 0;
        while (queue.Count > 0 && depth <= MaximumDepth)
        {
            depth++;
            var next = new Queue<string>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!children.TryGetValue(current, out var list)) continue;
                foreach (var child in list)
                {
                    var chain = ancestry[current];
                    if (chain.Contains(child.ThreadId, StringComparer.OrdinalIgnoreCase))
                    {
                        if (included.Add(child.ThreadId))
                            results.Add(new(child.ThreadId, child.ParentThreadId, RelatedMemberKind.Unconfirmed, "关系形成环", child));
                        continue;
                    }
                    if (child.Conflict is not null)
                    {
                        if (included.Add(child.ThreadId))
                            results.Add(new(child.ThreadId, child.ParentThreadId, RelatedMemberKind.Unconfirmed, child.Conflict, child));
                        continue;
                    }
                    if (!included.Add(child.ThreadId)) continue;
                    results.Add(new(child.ThreadId, child.ParentThreadId, RelatedMemberKind.Subagent, null, child));
                    ancestry[child.ThreadId] = [.. chain, child.ThreadId];
                    next.Enqueue(child.ThreadId);
                }
            }
            queue = next;
        }
        return results;
    }

    private static Dictionary<string, List<SessionRelationInfo>> BuildChildIndex(RelatedSessionCatalogSnapshot catalog)
    {
        var children = new Dictionary<string, List<SessionRelationInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in catalog.Threads.Values)
        {
            if (!info.IsSubagent || info.ParentThreadId is null) continue;
            if (!children.TryGetValue(info.ParentThreadId, out var list)) children[info.ParentThreadId] = list = [];
            list.Add(info);
        }
        return children;
    }
}
