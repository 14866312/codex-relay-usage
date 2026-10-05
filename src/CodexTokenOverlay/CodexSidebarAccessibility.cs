using Accessibility;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal sealed record SidebarRowIdentity(int NodeId, string Title, string? Project);
internal sealed record SidebarViewIdentity(string Owner, IReadOnlyList<SidebarRowIdentity> Rows)
{
    public string Key => Owner + "/" + string.Join(",", Rows.OrderBy(r => r.NodeId)
        .Select(r => $"{r.NodeId}:{r.Project?.Length ?? 0}:{r.Project}:{r.Title.Length}:{r.Title}"));
}

// This is a native accessibility node identity, NOT a Codex thread ID. Its lifetime
// is limited to one process/document. Never save these identities in settings.
internal sealed class AccessibleMetadata : IDisposable
{
    private static readonly Guid AccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static readonly Guid Ia2Id = new("E89F726E-C4F4-4C19-BB19-B647D7FA8478");
    private IntPtr _ia2;
    private readonly GetAttributes? _attributes;
    private readonly GetUniqueId? _uniqueId;
    public IAccessible Accessible { get; }
    public AccessibleMetadata(IAccessible accessible)
    {
        Accessible = accessible;
        if (accessible is not NativeServiceProvider provider) return;
        var service = AccessibleId; var iid = Ia2Id;
        if (provider.QueryService(ref service, ref iid, out _ia2) < 0 || _ia2 == IntPtr.Zero) return;
        // IAccessible2 extends IAccessible; slots follow the published IA2 ABI.
        var table = Marshal.ReadIntPtr(_ia2);
        _uniqueId = Marshal.GetDelegateForFunctionPointer<GetUniqueId>(Marshal.ReadIntPtr(table, 41 * IntPtr.Size));
        _attributes = Marshal.GetDelegateForFunctionPointer<GetAttributes>(Marshal.ReadIntPtr(table, 45 * IntPtr.Size));
    }
    public string Attributes() => _attributes is not null && _attributes(_ia2, out var value) >= 0 ? value ?? "" : "";
    public int? UniqueId() => _uniqueId is not null && _uniqueId(_ia2, out var value) >= 0 && value != 0 ? value : null;
    public void Dispose() { if (_ia2 != IntPtr.Zero) { Marshal.Release(_ia2); _ia2 = IntPtr.Zero; } }
    internal static string? Attribute(string attributes, string name)
    {
        foreach (var field in attributes.Split(';'))
            if (field.StartsWith(name + ":", StringComparison.Ordinal)) return field[(name.Length + 1)..];
        return null;
    }
    internal static bool IsCurrentThreadRow(int role, string attributes) => role == (int)AccessibleRole.PushButton
        && Attribute(attributes, "tag") == "div" && Attribute(attributes, "xml-roles") == "button"
        && Attribute(attributes, "current") == "page";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUniqueId(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributes(IntPtr self, [MarshalAs(UnmanagedType.BStr)] out string? value);
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr value);
    }
}

// The tree contains application chrome only. Buttons are leaves, and main/text/
// nested documents are pruned before names or children can be read.
internal sealed class SidebarMetadataNode
{
    public required int Role { get; init; }
    public required string Attributes { get; init; }
    public string? Name { get; init; }
    public int? Id { get; init; }
    public AccessibleMetadata? Native { get; init; }
    public List<SidebarMetadataNode> Children { get; } = [];
}
internal sealed record SidebarRowNode(SidebarMetadataNode Row, SidebarMetadataNode? Group, SidebarMetadataNode? Header);

internal static class SidebarStructure
{
    internal static IReadOnlyList<SidebarRowNode> Rows(SidebarMetadataNode root)
    {
        var rows = new List<SidebarRowNode>();
        void Walk(SidebarMetadataNode node, SidebarMetadataNode? group, SidebarMetadataNode? header, bool inItem)
        {
            // A project has a listitem wrapper containing its header and its own
            // child list. A thread listitem containing only a button is not a project.
            if (group is null && node.Role != (int)AccessibleRole.ListItem
                && AccessibleMetadata.Attribute(node.Attributes, "xml-roles") == "listitem")
            {
                var outsideLists = new List<SidebarMetadataNode>();
                void Find(SidebarMetadataNode n)
                {
                    outsideLists.Add(n);
                    if (n.Role is (int)AccessibleRole.List or (int)AccessibleRole.PushButton or (int)AccessibleRole.ButtonMenu) return;
                    foreach (var child in n.Children) Find(child);
                }
                foreach (var child in node.Children) Find(child);
                var headings = outsideLists.Where(IsDivButton).ToArray();
                var lists = outsideLists.Where(n => n.Role == (int)AccessibleRole.List).ToArray();
                if (headings.Length == 1 && lists.Length == 1 && outsideLists.IndexOf(headings[0]) < outsideLists.IndexOf(lists[0]))
                {
                    Walk(lists[0], lists[0], headings[0], false);
                    return; // Do not cache the project header as a thread row.
                }
                // A collapsed or structurally ambiguous project must not expose its
                // header as a conversation, or guess the scope of its child rows.
                return;
            }
            inItem |= node.Role == (int)AccessibleRole.ListItem;
            if (IsDivButton(node) && inItem) { rows.Add(new(node, group, header)); return; }
            foreach (var child in node.Children) Walk(child, group, header, inItem);
        }
        Walk(root, null, null, false);
        return rows;
    }
    private static bool IsDivButton(SidebarMetadataNode node) => node.Role == (int)AccessibleRole.PushButton
        && AccessibleMetadata.Attribute(node.Attributes, "tag") == "div"
        && AccessibleMetadata.Attribute(node.Attributes, "xml-roles") == "button";
}

internal sealed class CodexSidebarAccessibility : IDisposable
{
    private string? _owner;
    private readonly List<AccessibleMetadata> _owned = [];
    private IReadOnlyList<SidebarRowNode> _rows = [];
    private long _lastDiscovery;
    private bool _foundSidebar;
    private bool _complete;
    private string? _lastTitle;
    public SidebarViewIdentity? Read(IAccessible document, string owner, string? title)
    {
        if (_owner != owner) { Clear(); _owner = owner; }
        if (Stopwatch.GetElapsedTime(_lastDiscovery) > TimeSpan.FromSeconds(3) || _lastDiscovery == 0) Discover(document);
        var rows = CurrentRows();
        if (rows.Count == 0 && _foundSidebar && title is not (null or "Codex" or "ChatGPT")
            && (title != _lastTitle || Stopwatch.GetElapsedTime(_lastDiscovery) > TimeSpan.FromMilliseconds(300)))
        { Discover(document); rows = CurrentRows(); }
        _lastTitle = title;
        if (!_complete) return null;
        if (!_foundSidebar) return null;
        // Recheck only current rows. A navigation during traversal is not a stable view.
        foreach (var row in rows)
        {
            var cached = _rows.Single(n => n.Row.Id == row.NodeId);
            if (!AccessibleMetadata.IsCurrentThreadRow(cached.Row.Role, cached.Row.Native!.Attributes())
                || cached.Row.Native.Accessible.get_accName(0) != row.Title)
                throw new InvalidOperationException("侧栏正在切换");
        }
        if (document.get_accName(0) != title) throw new InvalidOperationException("页面正在切换");
        return new(owner, rows);
    }
    private List<SidebarRowIdentity> CurrentRows()
    {
        var current = new List<SidebarRowIdentity>();
        foreach (var row in _rows)
        {
            if (!AccessibleMetadata.IsCurrentThreadRow(row.Row.Role, row.Row.Native!.Attributes())) continue;
            var name = row.Row.Native.Accessible.get_accName(0);
            if (string.IsNullOrWhiteSpace(name) || row.Row.Id is null) continue;
            string? project = null;
            if (row.Group is not null)
            {
                // A moved/remounted row must not inherit its old cached project.
                if (!HasAncestor(row.Row.Native.Accessible, row.Group.Id))
                    throw new InvalidOperationException("侧栏分组已改变");
                project = row.Header!.Native!.Accessible.get_accName(0);
            }
            current.Add(new(row.Row.Id.Value, name, project));
        }
        return current;
    }
    private static bool HasAncestor(IAccessible node, int? expected)
    {
        if (expected is null) return false;
        for (var depth = 0; depth < 12 && node.accParent is IAccessible parent; depth++)
        {
            using var metadata = new AccessibleMetadata(parent);
            if (metadata.UniqueId() == expected) return true;
            node = parent;
        }
        return false;
    }
    private void Discover(IAccessible document)
    {
        Clear(); var seen = new HashSet<object>(); var sidebars = new List<SidebarMetadataNode>();
        var deadline = Stopwatch.GetTimestamp(); _complete = true;
        SidebarMetadataNode? Walk(IAccessible node, int depth, bool sidebar)
        {
            if (depth > 32 || seen.Count >= 768 || Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromMilliseconds(700))
            { _complete = false; return null; }
            if (!seen.Add(node)) return null;
            var role = node.get_accRole(0) is int r ? r : 0;
            if (role is (int)AccessibleRole.Text or (int)AccessibleRole.StaticText or (int)AccessibleRole.Document) return null;
            var metadata = new AccessibleMetadata(node); _owned.Add(metadata);
            var attrs = metadata.Attributes();
            if (AccessibleMetadata.Attribute(attrs, "tag") == "main" || AccessibleMetadata.Attribute(attrs, "xml-roles") == "main") return null;
            var isSidebar = AccessibleMetadata.Attribute(attrs, "tag") == "aside";
            sidebar |= isSidebar;
            var result = new SidebarMetadataNode { Role = role, Attributes = attrs, Native = metadata,
                Id = sidebar ? metadata.UniqueId() : null };
            if (isSidebar) sidebars.Add(result);
            if (role is (int)AccessibleRole.PushButton or (int)AccessibleRole.ButtonMenu or (int)AccessibleRole.Link) return result;
            foreach (var child in CodexViewIdentityReader.Children(node, 128))
                if (Walk(child, depth + 1, sidebar) is { } nested) result.Children.Add(nested);
            return result;
        }
        try
        {
            foreach (var child in CodexViewIdentityReader.Children(document, 64)) Walk(child, 0, false);
            _foundSidebar = sidebars.Count == 1;
            _rows = _foundSidebar ? SidebarStructure.Rows(sidebars[0]) : [];
            if (_rows.Any(r => r.Row.Id is null) || _rows.Select(r => r.Row.Id).Distinct().Count() != _rows.Count) _complete = false;
            _lastDiscovery = Stopwatch.GetTimestamp();
        }
        catch { Clear(); throw; }
    }
    private void Clear()
    {
        foreach (var node in _owned) node.Dispose();
        _owned.Clear(); _rows = []; _foundSidebar = false; _complete = false; _lastDiscovery = 0;
    }
    public void Reset() { Clear(); _owner = null; }
    public void Dispose() => Reset();
}
