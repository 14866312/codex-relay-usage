using System.Text.Json;

namespace CodexTokenOverlay;

internal sealed record LocalThreadProject(string ProjectId, bool IsLocal);
internal sealed class LocalProjectIndex
{
    private string _path = "";
    private string _home = "";
    private DateTime _writeUtc;
    private long _length = -1;
    private Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LocalThreadProject> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _remote = new(StringComparer.OrdinalIgnoreCase);
    public void SetRoot(string root)
    {
        var normalized = TokenLogMonitor.NormalizeRoot(root);
        var home = Path.GetDirectoryName(normalized) ?? normalized;
        var path = Path.Combine(home, ".codex-global-state.json");
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        _home = home; _path = path; _writeUtc = default; _length = -1; Clear();
    }
    public void Refresh()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) { Clear(); _length = -1; return; }
            if (info.LastWriteTimeUtc == _writeUtc && info.Length == _length) return;
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 32 * 1024 * 1024) { Clear(); return; }
            using var json = JsonDocument.Parse(file);
            Load(json.RootElement, _home);
            _writeUtc = info.LastWriteTimeUtc; _length = info.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Clear(); _writeUtc = default; _length = -1; }
    }
    internal void Load(JsonElement state, string home)
    {
        Clear();
        if (state.ValueKind != JsonValueKind.Object) return;
        var ambiguousAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.TryGetProperty("local-projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
            foreach (var project in projects.EnumerateObject())
            {
                if (project.Value.ValueKind != JsonValueKind.Object) continue;
                var name = IncrementalSessionReader.String(project.Value, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                _labels[project.Name] = name; _canonical[project.Name] = project.Name;
            }
        if (state.TryGetProperty("app-server-project-id-by-legacy-project-id-by-host", out var remaps) && remaps.ValueKind == JsonValueKind.Object)
            foreach (var host in remaps.EnumerateObject())
            {
                if (!string.Equals(host.Name, "local:" + home, StringComparison.OrdinalIgnoreCase) || host.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var alias in host.Value.EnumerateObject())
                {
                    if (alias.Value.ValueKind != JsonValueKind.String || !_labels.TryGetValue(alias.Name, out var label)) continue;
                    var id = alias.Value.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (ambiguousAliases.Contains(id)) continue;
                    // Conflicting aliases cannot establish a project scope.
                    if (_canonical.TryGetValue(id, out var existing) && !string.Equals(existing, alias.Name, StringComparison.OrdinalIgnoreCase))
                    { _labels.Remove(id); _canonical.Remove(id); ambiguousAliases.Add(id); continue; }
                    _labels[id] = label; _canonical[id] = alias.Name;
                }
            }
        if (state.TryGetProperty("thread-project-membership-host-ids", out var hosts) && hosts.ValueKind == JsonValueKind.Object)
            foreach (var thread in hosts.EnumerateObject())
                if (thread.Value.ValueKind == JsonValueKind.String && thread.Value.GetString() is { } host && host != "local") _remote.Add(thread.Name);
        if (state.TryGetProperty("thread-project-assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Object)
            foreach (var thread in assignments.EnumerateObject())
            {
                if (thread.Value.ValueKind != JsonValueKind.Object) continue;
                var id = IncrementalSessionReader.String(thread.Value, "projectId");
                if (id is null) continue;
                var local = IncrementalSessionReader.String(thread.Value, "projectKind") == "local"
                    && IncrementalSessionReader.String(thread.Value, "projectOrigin") != "chatgpt" && !_remote.Contains(thread.Name);
                _assignments[thread.Name] = new(id, local);
            }
    }
    public bool IsRemote(string id) => _remote.Contains(id) || (_assignments.TryGetValue(id, out var a) && !a.IsLocal);
    public string? ProjectKey(string id) => _assignments.TryGetValue(id, out var a) && a.IsLocal
        && _canonical.TryGetValue(a.ProjectId, out var canonical) ? canonical : null;
    public string? UniqueProject(string label)
    {
        var ids = _labels.Where(p => p.Value == label).Select(p => _canonical[p.Key]).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }
    private void Clear() { _labels.Clear(); _canonical.Clear(); _assignments.Clear(); _remote.Clear(); }
}

internal sealed record VisibleSessionResolution(string? ThreadId, string? Error, string? Method = null, bool SameTitleAmbiguous = false);
internal sealed record SidebarBindingTarget(long RootRevision, long ViewVersion, string ViewKey, string Title)
{
    public bool Matches(long rootRevision, long viewVersion, CodexViewIdentity view, bool connected) => connected
        && RootRevision == rootRevision && ViewVersion == viewVersion && ViewKey == SidebarSessionResolver.ViewKey(view);
}

internal sealed class SidebarSessionResolver
{
    private sealed record Binding(string ThreadId, string Title, string? ProjectKey, string Method, bool Exact = false);
    private readonly Dictionary<int, Binding> _bindings = [];
    private string? _owner;
    public void Reset() { _bindings.Clear(); _owner = null; }
    internal static string ViewKey(CodexViewIdentity view) => view.Sidebar is { Rows.Count: > 0 } sidebar
        ? sidebar.Key : $"{view.DocumentKey}/{view.ThreadId}/{view.Title}";
    internal VisibleSessionResolution ResolveCopiedLink(CodexViewIdentity before, CodexViewIdentity after,
        string? copiedLink, IReadOnlyDictionary<string, string> titles, LocalProjectIndex projects)
    {
        if (before.WindowCount != 1 || after.WindowCount != 1 || before.Error is not null || after.Error is not null
            || before.DocumentKey is null || before.DocumentKey != after.DocumentKey || ViewKey(before) != ViewKey(after)
            || before.Title != after.Title)
            return new(null, "识别期间切换了对话，请在目标对话重新点击识别");
        if (after.Sidebar is not { Rows.Count: > 0 } || string.IsNullOrWhiteSpace(after.Title)
            || after.Sidebar.Rows.Any(row => !RowTitleMatches(row.Title, after.Title)))
            return new(null, "请展开 Codex 侧栏后重新点击识别");
        var id = CurrentConversationLink.ThreadId(copiedLink);
        if (id is null) return new(null, "Codex 未提供有效的本地对话链接");
        if (projects.IsRemote(id)) return new(null, "当前对话不属于本机日志，暂不能统计");
        // An exact local link is authoritative even if the title index has not caught
        // up with a rename. Store it against this document's current native row only.
        ObserveOwner(after);
        Remember(after, id, projects, "当前对话链接", exact: true);
        return new(id, null, "当前对话链接");
    }
    private void ObserveOwner(CodexViewIdentity view)
    {
        if (_owner == view.DocumentKey) return;
        _bindings.Clear(); _owner = view.DocumentKey;
    }
    internal static bool RowTitleMatches(string row, string title) => row == title ||
        (row.EndsWith('…') && row.Length > 4 && title.StartsWith(row[..^1], StringComparison.Ordinal));
    private static string? ProjectScope(CodexViewIdentity view, LocalProjectIndex projects, out bool conflict)
    {
        conflict = false;
        var labels = view.Sidebar?.Rows.Where(r => r.Project is not null).Select(r => r.Project!).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (labels.Length == 0) return null;
        if (labels.Length != 1) { conflict = true; return null; }
        var project = projects.UniqueProject(labels[0]);
        conflict = project is null;
        return project;
    }
    public VisibleSessionResolution Resolve(CodexViewIdentity view, IReadOnlyDictionary<string, string> titles, LocalProjectIndex projects)
    {
        ObserveOwner(view);
        if (view.WindowCount != 1 || view.Error is not null) return new(null, view.Error);
        if (view.ThreadId is not null) return new(view.ThreadId, null, "页面路由");
        if (string.IsNullOrWhiteSpace(view.Title) || view.Title is "Codex" or "ChatGPT") return new(null, "当前页面未提供会话标识");
        var rows = view.Sidebar?.Rows ?? [];
        if (rows.Any(r => !RowTitleMatches(r.Title, view.Title))) return new(null, "侧栏与当前页面不一致，正在重新核对");
        var exactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!_bindings.TryGetValue(row.NodeId, out var binding) || !binding.Exact) continue;
            if (binding.Title != view.Title || projects.IsRemote(binding.ThreadId)
                || binding.ProjectKey != projects.ProjectKey(binding.ThreadId))
            { _bindings.Remove(row.NodeId); continue; }
            exactIds.Add(binding.ThreadId);
        }
        if (exactIds.Count == 1) return new(exactIds.Single(), null, "当前对话链接");
        if (exactIds.Count > 1) return new(null, "当前侧栏存在识别冲突，请重新点击识别", SameTitleAmbiguous: true);
        var scope = ProjectScope(view, projects, out var conflict);
        if (conflict) return new(null, "侧栏项目无法唯一匹配本地项目");
        var candidates = titles.Where(p => p.Value == view.Title).Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sameTitleInSidebar = view.Sidebar?.SameTitleRowCount > 1;
        var incomplete = false;
        if (scope is not null)
        {
            // An unknown assignment cannot safely be excluded as a same-project duplicate.
            incomplete = candidates.Any(id => !projects.IsRemote(id) && projects.ProjectKey(id) is null);
            candidates = candidates.Where(id => !projects.IsRemote(id) && (projects.ProjectKey(id) == scope || projects.ProjectKey(id) is null)).ToArray();
        }
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!_bindings.TryGetValue(row.NodeId, out var binding)) continue;
            if (binding.Title != view.Title || !titles.TryGetValue(binding.ThreadId, out var title) || title != view.Title
                || binding.ProjectKey != projects.ProjectKey(binding.ThreadId) || projects.IsRemote(binding.ThreadId)
                || !candidates.Contains(binding.ThreadId, StringComparer.OrdinalIgnoreCase))
            { _bindings.Remove(row.NodeId); continue; }
            bound.Add(binding.ThreadId);
        }
        if (bound.Count > 1) return new(null, "侧栏会话绑定冲突，请重新绑定");
        if (bound.Count == 1)
        {
            var id = bound.Single();
            var method = rows.Select(r => _bindings.GetValueOrDefault(r.NodeId)).First(b => b?.ThreadId == id)!.Method;
            return new(id, null, method);
        }
        if (incomplete) return new(null, "当前会话的项目归属尚未更新", SameTitleAmbiguous: candidates.Length > 1 || sameTitleInSidebar);
        if (candidates.Length != 1 || projects.IsRemote(candidates[0]))
            return new(null, candidates.Length > 1 || sameTitleInSidebar ? "同名对话，请点击识别" : "当前对话暂未提供会话标识",
                // Only several conversations sharing the page title can be resolved by
                // following the page; a title with no conversation at all cannot.
                SameTitleAmbiguous: candidates.Length > 1 || sameTitleInSidebar);
        var source = scope is not null ? "侧栏项目" : rows.Count > 0 ? "侧栏识别" : "唯一标题";
        Remember(view, candidates[0], projects, source);
        return new(candidates[0], null, source);
    }
    public bool TryBind(CodexViewIdentity view, string id, IReadOnlyDictionary<string, string> titles, LocalProjectIndex projects, out string? error)
    {
        ObserveOwner(view);
        error = "当前侧栏无法稳定识别，请打开侧栏后重试";
        if (view.WindowCount != 1 || view.Error is not null || view.Sidebar is not { Rows.Count: > 0 } || view.DocumentKey is null
            || string.IsNullOrWhiteSpace(view.Title) || view.Sidebar.Rows.Any(r => !RowTitleMatches(r.Title, view.Title))) return false;
        error = "所选会话与当前页面标题不一致";
        if (!titles.TryGetValue(id, out var title) || title != view.Title) return false;
        var scope = ProjectScope(view, projects, out var conflict);
        error = "所选会话与当前侧栏项目不一致，或项目归属未提供";
        if (conflict || projects.IsRemote(id) || (scope is not null && projects.ProjectKey(id) != scope)) return false;
        Remember(view, id, projects, "侧栏绑定"); error = null; return true;
    }
    private void Remember(CodexViewIdentity view, string id, LocalProjectIndex projects, string method, bool exact = false)
    {
        if (view.DocumentKey is null || view.Sidebar is not { Rows.Count: > 0 } sidebar) return;
        if (_bindings.Count + sidebar.Rows.Count > 512) _bindings.Clear();
        foreach (var row in sidebar.Rows) _bindings[row.NodeId] = new(id, view.Title!, projects.ProjectKey(id), method, exact);
    }
}
