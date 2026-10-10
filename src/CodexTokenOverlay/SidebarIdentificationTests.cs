using System.Text.Json;

namespace CodexTokenOverlay;

internal static class SidebarIdentificationTests
{
    private const string Home = @"C:\synthetic\codex";
    private const string Title = "相同的完整会话标题";
    private static object Assignment(string project, string kind = "local") => new { projectKind = kind, projectId = project };
    private static Dictionary<string, object> Assignments() => new()
    { ["a"] = Assignment("p1"), ["b"] = Assignment("server-two"), ["remote"] = Assignment("p1"), ["cloud"] = Assignment("p1", "chatgpt") };
    private static JsonElement State(Dictionary<string, object>? assignments = null, bool duplicateNames = false,
        bool conflictingAliases = false, string? mappingHome = null) => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["local-projects"] = new Dictionary<string, object>
        { ["p1"] = new { name = "项目一" }, ["p2"] = new { name = duplicateNames ? "项目一" : "项目二" }, ["p3"] = new { name = "项目三" } },
        ["app-server-project-id-by-legacy-project-id-by-host"] = new Dictionary<string, object>
        { ["local:" + (mappingHome ?? Home)] = conflictingAliases
            ? new Dictionary<string, string> { ["p1"] = "collision", ["p2"] = "collision", ["p3"] = "collision" }
            : new Dictionary<string, string> { ["p1"] = "server-one", ["p2"] = "server-two" } },
        ["thread-project-assignments"] = assignments ?? Assignments(),
        ["thread-project-membership-host-ids"] = new Dictionary<string, string> { ["remote"] = "other-host" }
    });
    private static CodexViewIdentity View(int row, string? project = null, string title = Title, string owner = "owner") =>
        new(1, title, DocumentKey: owner, Sidebar: new(owner, new[] { new SidebarRowIdentity(row, title, project) }));
    private static SidebarMetadataNode Node(AccessibleRole role, string attrs, string? name = null, int? id = null, params SidebarMetadataNode[] children)
    {
        var node = new SidebarMetadataNode { Role = (int)role, Attributes = attrs, Name = name, Id = id };
        node.Children.AddRange(children); return node;
    }
    private static SidebarMetadataNode Row(int id, string attrs = "tag:div;xml-roles:button;current:page;") =>
        Node(AccessibleRole.ListItem, "tag:div;xml-roles:listitem;", children: new[] { Node(AccessibleRole.PushButton, attrs, Title, id) });
    private static SidebarMetadataNode Project(string name, int row) => Node(AccessibleRole.ListItem, "xml-roles:listitem;", children: new[]
    { Node(AccessibleRole.Grouping, "tag:div;xml-roles:listitem;", children: new[]
        { Node(AccessibleRole.PushButton, "tag:div;xml-roles:button;current:page;", name, row + 100),
          Node(AccessibleRole.List, "xml-roles:list;", id: row + 200, children: new[] { Row(row) }) }) });

    internal static void Run(Action<string, bool> check, string directory)
    {
        CopiedLinkTests(check);
        IdentificationPipelineTests.Run(check, directory);
        StructureTests(check); ProjectTests(check, directory); ResolverTests(check); PublicationTests(check);
    }
    private static void StructureTests(Action<string, bool> check)
    {
        const string current = "tag:div;xml-roles:button;current:page;";
        check("current-page thread marker is accepted", AccessibleMetadata.IsCurrentThreadRow((int)AccessibleRole.PushButton, current));
        check("selected and focused are not current-page markers", !AccessibleMetadata.IsCurrentThreadRow((int)AccessibleRole.PushButton, "tag:div;xml-roles:button;selected:true;focused:true;"));
        check("home navigation button is excluded even when current", !AccessibleMetadata.IsCurrentThreadRow((int)AccessibleRole.PushButton, "tag:button;xml-roles:button;current:page;"));
        check("current link and missing role are excluded", !AccessibleMetadata.IsCurrentThreadRow((int)AccessibleRole.Link, current)
            && !AccessibleMetadata.IsCurrentThreadRow((int)AccessibleRole.PushButton, "tag:div;current:page;"));
        var collapsed = Node(AccessibleRole.Grouping, "xml-roles:listitem;", children: new[]
            { Node(AccessibleRole.PushButton, current, "折叠项目", 60) });
        var ambiguous = Node(AccessibleRole.Grouping, "xml-roles:listitem;", children: new[]
            { Node(AccessibleRole.PushButton, current, "结构不明", 61),
              Node(AccessibleRole.List, "xml-roles:list;", children: new[] { Row(62) }), Node(AccessibleRole.List, "xml-roles:list;", children: new[] { Row(63) }) });
        var root = Node(AccessibleRole.Grouping, "tag:aside;", children: new[]
        { Node(AccessibleRole.PushButton, "tag:button;xml-roles:button;current:page;", "首页", 1),
          Node(AccessibleRole.List, "xml-roles:list;", children: new[] { Project("项目一", 10), Project("项目二", 20),
              Node(AccessibleRole.ListItem, "xml-roles:listitem;", children: new[] { collapsed }),
              Node(AccessibleRole.ListItem, "xml-roles:listitem;", children: new[] { ambiguous }) }),
          Node(AccessibleRole.List, "xml-roles:list;", children: new[] { Row(30), Row(31, "tag:div;xml-roles:button;selected:true;") }) });
        var rows = SidebarStructure.Rows(root);
        check("project wrappers expose thread rows rather than project headings", rows.Select(r => r.Row.Id).Order().SequenceEqual(new int?[] { 10, 20, 30, 31 }));
        check("own child list supplies project scope", rows.Single(r => r.Row.Id == 10).Header?.Name == "项目一"
            && rows.Single(r => r.Row.Id == 20).Header?.Name == "项目二" && rows.Single(r => r.Row.Id == 10).Group?.Id == 210);
        check("recent list has no inferred selected-project scope", rows.Single(r => r.Row.Id == 30).Group is null);
        check("only current-page rows participate in the visible identity", rows.Where(r => AccessibleMetadata.IsCurrentThreadRow(r.Row.Role, r.Row.Attributes)).Count() == 3);
        var a = new SidebarViewIdentity("owner", new[] { new SidebarRowIdentity(10, Title, "项目一"), new SidebarRowIdentity(30, Title, null) });
        check("view key is independent of current-row enumeration order", a.Key == (a with { Rows = a.Rows.Reverse().ToArray() }).Key);
        check("same title with a different row changes visible identity", a.Key != new SidebarViewIdentity("owner", new[] { new SidebarRowIdentity(11, Title, "项目一"), new SidebarRowIdentity(30, Title, null) }).Key);
        check("view key changes for owner or project changes", a.Key != (a with { Owner = "another-document" }).Key
            && a.Key != new SidebarViewIdentity("owner", new[] { new SidebarRowIdentity(10, Title, "项目二"), new SidebarRowIdentity(30, Title, null) }).Key);
    }
    private static void ProjectTests(Action<string, bool> check, string directory)
    {
        var projects = new LocalProjectIndex(); projects.Load(State(), Home);
        check("legacy and server project IDs have the same local scope", projects.ProjectKey("a") == "p1" && projects.ProjectKey("b") == "p2"
            && projects.UniqueProject("项目二") == "p2");
        check("remote memberships and cloud project kinds are not local sessions", projects.IsRemote("remote") && projects.IsRemote("cloud") && projects.ProjectKey("remote") is null);
        check("missing assignment is unknown rather than remote", projects.ProjectKey("missing") is null && !projects.IsRemote("missing"));
        projects.Load(State(duplicateNames: true), Home);
        check("duplicate project labels cannot establish a unique scope", projects.UniqueProject("项目一") is null);
        var assignments = Assignments(); assignments["collision-thread"] = Assignment("collision");
        projects.Load(State(assignments, conflictingAliases: true), Home);
        check("a third conflicting alias cannot resurrect an ambiguous project ID", projects.ProjectKey("collision-thread") is null);
        projects.Load(State(mappingHome: Home + "-other"), Home);
        check("project aliases from another home are excluded", projects.ProjectKey("b") is null && projects.ProjectKey("a") == "p1");
        projects.Load(JsonSerializer.SerializeToElement(new[] { 1 }), Home);
        check("non-object global state clears project mappings", projects.UniqueProject("项目一") is null);
        var home = Path.Combine(directory, "project-index"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var path = Path.Combine(home, ".codex-global-state.json"); projects.SetRoot(home); projects.Refresh();
        check("missing project file has no cached assignment", projects.ProjectKey("a") is null);
        File.WriteAllText(path, State(mappingHome: home).GetRawText()); projects.Refresh();
        check("project state loads from the configured Codex home", projects.ProjectKey("a") == "p1" && projects.ProjectKey("b") == "p2");
        projects.SetRoot(Path.Combine(home, "sessions")); projects.Refresh();
        check("home and sessions paths resolve the same project state", projects.ProjectKey("b") == "p2");
        File.WriteAllText(path, "{broken"); projects.Refresh();
        check("half-written global state clears stale project scope", projects.ProjectKey("a") is null);
        File.WriteAllText(path, State(mappingHome: home).GetRawText()); projects.Refresh();
        check("project state recovers after a complete replacement", projects.ProjectKey("a") == "p1");
        File.Delete(path); projects.Refresh();
        check("deleted project state clears learned project assignments", projects.ProjectKey("a") is null);
    }
    private static void ResolverTests(Action<string, bool> check)
    {
        var projects = new LocalProjectIndex(); projects.Load(State(), Home);
        var titles = new Dictionary<string, string> { ["a"] = Title, ["b"] = Title };
        var resolver = new SidebarSessionResolver(); var a = View(10, "项目一"); var b = View(20, "项目二");
        check("cross-project duplicate title resolves the actual first project", resolver.Resolve(a, titles, projects) is { ThreadId: "a", Method: "侧栏项目" });
        check("cross-project duplicate title switches to the actual second project", resolver.Resolve(b, titles, projects).ThreadId == "b");
        check("return to first project restores its current session", resolver.Resolve(a, titles, projects).ThreadId == "a");
        check("unscoped duplicate title cannot guess an identity", resolver.Resolve(View(30), titles, projects).ThreadId is null);
        check("explicit valid route has priority over title and sidebar", resolver.Resolve(b with { ThreadId = "exact-route" }, titles, projects) is { ThreadId: "exact-route", Method: "页面路由" });
        check("accessibility errors and multiple windows prevent route selection", resolver.Resolve(a with { Error = "unavailable", ThreadId = "a" }, titles, projects).ThreadId is null
            && resolver.Resolve(a with { WindowCount = 2 }, titles, projects).ThreadId is null);
        var staleRow = a with { Sidebar = new("owner", new[] { new SidebarRowIdentity(10, "另一个标题", "项目一") }) };
        check("disagreeing sidebar blocks even unique-title fallback", resolver.Resolve(staleRow, new Dictionary<string, string> { ["a"] = Title }, projects).ThreadId is null);
        var abbreviated = a with { Sidebar = new("owner", new[] { new SidebarRowIdentity(10, "相同的完整…", "项目一") }) };
        check("abbreviated row only validates the full document title", resolver.Resolve(abbreviated, titles, projects).ThreadId == "a"
            && resolver.Resolve(abbreviated with { Title = "相同的完整…" }, titles, projects).ThreadId is null);
        check("short or unmarked prefixes cannot validate a row", !SidebarSessionResolver.RowTitleMatches("相同…", Title) && !SidebarSessionResolver.RowTitleMatches("相同的完整", Title));
        check("two current rows from distinct projects fail closed", resolver.Resolve(a with { Sidebar = new("owner", new[]
            { new SidebarRowIdentity(10, Title, "项目一"), new SidebarRowIdentity(20, Title, "项目二") }) }, titles, projects).ThreadId is null);
        titles["c"] = Title;
        check("unknown assignment cannot be discarded to force a unique project match", new SidebarSessionResolver().Resolve(a, titles, projects).ThreadId is null);
        var assignments = Assignments(); assignments["c"] = Assignment("p1"); projects.Load(State(assignments), Home);
        var c = View(40, "项目一"); resolver = new();
        check("same-project duplicate titles require a binding", resolver.Resolve(a, titles, projects).ThreadId is null);
        check("binding rejects a different project's selected session", !resolver.TryBind(a, "b", titles, projects, out _));
        check("explicit binding identifies the first same-project row", resolver.TryBind(a, "a", titles, projects, out _)
            && resolver.Resolve(a, titles, projects) is { ThreadId: "a", Method: "侧栏绑定" });
        check("binding does not pin a different unbound row with the same title", resolver.Resolve(c, titles, projects).ThreadId is null);
        check("a second same-project row can bind independently", resolver.TryBind(c, "c", titles, projects, out _) && resolver.Resolve(c, titles, projects).ThreadId == "c");
        check("bound same-title rows follow A to C to A", resolver.Resolve(a, titles, projects).ThreadId == "a"
            && resolver.Resolve(c, titles, projects).ThreadId == "c" && resolver.Resolve(a, titles, projects).ThreadId == "a");
        check("simultaneously current conflicting bindings fail closed", resolver.Resolve(a with { Sidebar = new("owner", new[]
            { new SidebarRowIdentity(10, Title, "项目一"), new SidebarRowIdentity(40, Title, "项目一") }) }, titles, projects).ThreadId is null);
        check("remounted sidebar row does not inherit another row's binding", resolver.Resolve(View(11, "项目一"), titles, projects).ThreadId is null);
        resolver.TryBind(a, "a", titles, projects, out _);
        check("document or process identity change invalidates bindings", resolver.Resolve(View(10, "项目一", owner: "new-owner"), titles, projects).ThreadId is null);
        resolver.TryBind(a, "a", titles, projects, out _); resolver.Reset();
        check("log root reset invalidates all native-row bindings", resolver.Resolve(a, titles, projects).ThreadId is null);
        resolver.TryBind(a, "a", titles, projects, out _); titles["a"] = "已改名";
        check("rename invalidates rather than retaining old bound session", resolver.Resolve(a, titles, projects).ThreadId == "c");
        titles["a"] = Title; resolver = new(); resolver.TryBind(a, "a", titles, projects, out _);
        assignments["a"] = Assignment("p2"); projects.Load(State(assignments), Home);
        check("project reassignment invalidates old bound identity", resolver.Resolve(a, titles, projects).ThreadId == "c");
        resolver = new(); projects.Load(State(duplicateNames: true), Home);
        check("duplicate project labels block automatic and explicit project binding", resolver.Resolve(a, titles, projects).ThreadId is null && !resolver.TryBind(a, "a", titles, projects, out _));
        projects.Load(State(), Home); titles = new() { ["a"] = Title }; resolver = new();
        check("hidden sidebar preserves safe unique-title fallback", resolver.Resolve(new(1, Title), titles, projects) is { ThreadId: "a", Method: "唯一标题" });
        check("unscoped unique row can be learned automatically", resolver.Resolve(View(30), titles, projects) is { ThreadId: "a", Method: "侧栏识别" });
        titles["b"] = Title;
        check("learned unique row survives a later duplicate title", resolver.Resolve(View(30), titles, projects).ThreadId == "a");
        check("unscoped duplicate can be explicitly bound", resolver.TryBind(View(31), "b", titles, projects, out _) && resolver.Resolve(View(31), titles, projects).ThreadId == "b");
        check("binding rejects a missing title index and a hidden sidebar", !resolver.TryBind(a, "absent", titles, projects, out _)
            && !resolver.TryBind(new(1, Title), "a", titles, projects, out _));
        titles["remote"] = Title; titles["cloud"] = Title;
        check("manual row binding rejects remote and cloud sessions", !resolver.TryBind(View(32), "remote", titles, projects, out _)
            && !resolver.TryBind(View(32), "cloud", titles, projects, out _));
        check("home pages do not inherit bindings", resolver.Resolve(View(30, title: "Codex"), titles, projects).ThreadId is null);
        // The one-tap control is offered only for same-title ambiguity, never for a page
        // that simply has no matching conversation yet.
        var plainProjects = new LocalProjectIndex(); plainProjects.Load(State(), Home);
        var ambiguous = new SidebarSessionResolver().Resolve(View(30), new Dictionary<string, string> { ["a"] = Title, ["b"] = Title }, plainProjects);
        check("same-title ambiguity is reported for the identify control",
            ambiguous.SameTitleAmbiguous && ambiguous.ThreadId is null && ambiguous.Error is not null);
        var unmatched = new SidebarSessionResolver().Resolve(View(30, title: "没有对应会话的页面"), new Dictionary<string, string> { ["a"] = Title }, plainProjects);
        check("unmatched page title does not offer the identify control", !unmatched.SameTitleAmbiguous);
        var notIndexed = View(40) with { Sidebar = new("owner", new[] { new SidebarRowIdentity(40, Title, null) }, SameTitleRowCount: 2) };
        check("visible sidebar duplicates offer identify before title index updates",
            new SidebarSessionResolver().Resolve(notIndexed, new Dictionary<string, string>(), plainProjects).SameTitleAmbiguous);
    }
    private static void PublicationTests(Action<string, bool> check)
    {
        var viewA = View(10); var viewB = View(20);
        var target = new SidebarBindingTarget(2, 10, SidebarSessionResolver.ViewKey(viewA), Title);
        check("unchanged fresh binding target is accepted", target.Matches(2, 10, viewA, true));
        check("binding dialog detects a same-title row switch", !target.Matches(2, 10, viewB, true));
        check("binding dialog rejects A B A even after the original row returns", !target.Matches(2, 12, viewA, true));
        check("binding dialog rejects log-root changes or disconnection", !target.Matches(3, 10, viewA, true) && !target.Matches(2, 10, viewA, false));
        var route = new ActiveThreadRouteStatus("test", 1, true, 1, null, SidebarSessionResolver.ViewKey(viewA), "侧栏绑定");
        var selection = new SessionSelection(); selection.Observe(route, null, 0); var first = selection.Request();
        var cost = SessionCostTests.SampleResult();
        var snapshot = DiagnosticsRunner.Example() with { ThreadId = "test", Ledger = new("test", cost.LedgerId, cost.LedgerVersion, [], null, new(null, null, null, null), false, []) };
        // Use the actual result ledger shape without requiring any real prices/logs.
        selection.TryPublish(first, snapshot, null);
        check("matching current-row token and cost results publish", selection.Snapshot is not null
            && CostPublication.Matches(first, selection.Revision, snapshot, cost.PricingVersion, cost));
        var unchanged = CodexVisibleThreadMonitor.Advance(route, route with { Version = 99 });
        check("unchanged sidebar sampling does not clear valid token data", unchanged.Version == route.Version && !selection.Observe(unchanged, null, 0) && selection.Snapshot is not null);
        var moved = CodexVisibleThreadMonitor.Advance(route, route with { ViewKey = SidebarSessionResolver.ViewKey(viewB) });
        selection.Observe(moved, null, 0);
        check("native row change clears previous values even if thread ID and title are unchanged", moved.Version > route.Version && selection.Snapshot is null && !selection.TryPublish(first, snapshot, null));
        check("native row change rejects old cost results", !CostPublication.Matches(first, selection.Revision, snapshot, cost.PricingVersion, cost));
        var returned = CodexVisibleThreadMonitor.Advance(moved, route); selection.Observe(returned, null, 0);
        check("native-row A B A rejects original token and fee completion", !selection.TryPublish(first, snapshot, null)
            && !CostPublication.Matches(first, selection.Revision, snapshot, cost.PricingVersion, cost));
        check("identification method is visible in follow status", FollowSelection.Status(route, null).Contains("侧栏绑定") && FollowSelection.Status(route, "manual") == "手动锁定");
    }

    private static void CopiedLinkTests(Action<string, bool> check)
    {
        const string a = "11111111-1111-7111-8111-111111111111", b = "22222222-2222-7222-8222-222222222222";
        var projects = new LocalProjectIndex();
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [a] = Title, [b] = Title
        };
        var resolver = new SidebarSessionResolver(); var first = View(10); var second = View(20);
        var linkA = "codex://threads/" + a; var linkB = "codex://threads/" + b;
        check("copied link identifies the exact current same-title row",
            resolver.ResolveCopiedLink(first, first, linkA, titles, projects).ThreadId == a
            && resolver.Resolve(first, titles, projects).Method == "当前对话链接");
        check("another same-title row does not inherit exact link identity", resolver.Resolve(second, titles, projects).ThreadId is null);
        check("each same-title row retains its own copied identity",
            resolver.ResolveCopiedLink(second, second, linkB, titles, projects).ThreadId == b
            && resolver.Resolve(first, titles, projects).ThreadId == a && resolver.Resolve(second, titles, projects).ThreadId == b);
        titles[a] = "标题索引暂未更新";
        check("exact copied identity survives a stale title index", resolver.Resolve(first, titles, projects).ThreadId == a);
        check("row change during copying cannot bind the previous page",
            resolver.ResolveCopiedLink(first, second, linkA, titles, projects).ThreadId is null);
        check("process restart invalidates a copied identity", resolver.Resolve(View(10, owner: "new-owner"), titles, projects).Method != "当前对话链接");
        check("invalid copied content never establishes identity", resolver.ResolveCopiedLink(first, first, "ordinary clipboard text", titles, projects).ThreadId is null);
        foreach (var invalid in new[] { "https://chatgpt.com/c/" + a, "codex://threads/" + a + "/extra", "codex://threads/" + a + "?host=remote", "codex://threads/not-a-guid", "codex://user@threads/" + a })
            check("reject nonlocal or malformed copied link " + invalid, CurrentConversationLink.ThreadId(invalid) is null);
        check("copied link canonicalizes uppercase UUID", CurrentConversationLink.ThreadId(linkA.ToUpperInvariant()) == a);
        check("hidden sidebar cannot retain ambiguous copied identity",
            resolver.ResolveCopiedLink(new(1, Title, DocumentKey: "owner"), new(1, Title, DocumentKey: "owner"), linkA, titles, projects).ThreadId is null);
        var remote = new LocalProjectIndex();
        remote.Load(JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["thread-project-membership-host-ids"] = new Dictionary<string, string> { [a] = "remote-host" } }), Home);
        check("remote thread link cannot be bound as a local conversation", resolver.ResolveCopiedLink(first, first, linkA, titles, remote).ThreadId is null);
    }
}
