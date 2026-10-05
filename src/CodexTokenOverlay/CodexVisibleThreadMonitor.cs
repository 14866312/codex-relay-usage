using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Accessibility;

namespace CodexTokenOverlay;

internal sealed record CodexViewIdentity(int WindowCount, string? Title, string? ThreadId = null, string? Error = null,
    string? DocumentKey = null, SidebarViewIdentity? Sidebar = null);

// Stream subscriptions survive navigation. Only the actual Codex document identifies the visible page.
internal static class CodexViewIdentityReader
{
    private const int DocumentRole = (int)AccessibleRole.Document;
    private const int InvisibleOrOffscreen = (int)(AccessibleStates.Invisible | AccessibleStates.Offscreen);
    private static readonly Guid AccessibleInterface = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static CodexViewIdentity Read(CodexSidebarAccessibility? sidebar = null)
    {
        var windows = CodexWindowLocator.GetVisibleMainWindows();
        if (windows.Count != 1) { sidebar?.Reset(); return new(windows.Count, null); }
        return ReadWindow(windows[0].Handle, sidebar);
    }

    internal static CodexViewIdentity ReadWindow(IntPtr handle, CodexSidebarAccessibility? sidebar = null)
    {
        try
        {
            var iid = AccessibleInterface;
            if (AccessibleObjectFromWindow(handle, 0xFFFFFFFC, ref iid, out var root) < 0 || root is not IAccessible accessible)
                return new(1, null, Error: "无法读取当前页面");
            var queue = new Queue<(IAccessible Node, int Depth)>();
            var visited = new HashSet<object>();
            var documents = new List<(IAccessible Node, string? Url)>();
            queue.Enqueue((accessible, 0));
            // Stop at documents: never walk the conversation's text, tool output, or browser contents.
            while (queue.Count > 0 && visited.Count < 256)
            {
                var (node, depth) = queue.Dequeue();
                if (depth > 12 || !visited.Add(node)) continue;
                if (node.get_accState(0) is int state && (state & InvisibleOrOffscreen) != 0) continue;
                if (node.get_accRole(0) is int role && role == DocumentRole)
                {
                    var url = node.get_accValue(0);
                    if (IsCodexDocument(url)) documents.Add((node, url));
                    continue;
                }
                var count = Math.Min(64, node.accChildCount);
                if (count <= 0) continue;
                var children = new object[count];
                if (AccessibleChildren(node, 0, count, children, out var actual) < 0) continue;
                foreach (var child in children.Take(actual))
                    if (child is IAccessible nested) queue.Enqueue((nested, depth + 1));
            }
            if (documents.Count != 1) { sidebar?.Reset(); return new(1, null, Error: "无法唯一识别当前页面"); }
            var document = documents[0];
            var title = document.Node.get_accName(0);
            using var metadata = new AccessibleMetadata(document.Node);
            GetWindowThreadProcessId(handle, out var pid);
            long lifetime;
            using (var process = Process.GetProcessById((int)pid)) lifetime = process.StartTime.ToUniversalTime().Ticks;
            var owner = metadata.UniqueId() is { } id ? $"{handle.ToInt64()}/{pid}/{lifetime}/{id}" : null;
            var rows = owner is null ? null : sidebar?.Read(document.Node, owner, title);
            return new(1, title, ThreadIdFromUrl(document.Url), DocumentKey: owner, Sidebar: rows);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            sidebar?.Reset();
            return new(1, null, Error: "当前页面暂时无法读取");
        }
    }

    private static bool IsCodexDocument(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "app" || uri.Scheme == "file");

    internal static string? ThreadIdFromUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsCodexDocument(value)) return null;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length == 2 && path[0] is "thread" or "threads" && Guid.TryParseExact(path[1], "D", out var id)
            ? id.ToString() : null;
    }
    internal static IReadOnlyList<IAccessible> Children(IAccessible node, int maximum)
    {
        var count = node.accChildCount;
        if (count > maximum) throw new InvalidOperationException("侧栏内容超出读取范围");
        if (count <= 0) return [];
        var children = new object[count];
        return AccessibleChildren(node, 0, count, children, out var actual) < 0 ? []
            : children.Take(actual).OfType<IAccessible>().ToArray();
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object result);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible container, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int actual);
}

internal sealed class SessionTitleIndex
{
    public IReadOnlyDictionary<string, string> Titles => _titlesById;
    private string _path = "";
    private DateTime _writeUtc;
    private long _length = -1;
    private Dictionary<string, string> _titlesById = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; private set; }

    public void SetRoot(string root)
    {
        var normalized = TokenLogMonitor.NormalizeRoot(root);
        var path = Path.Combine(Path.GetDirectoryName(normalized) ?? normalized, "session_index.jsonl");
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        _path = path; _writeUtc = default; _length = -1; _titlesById.Clear(); Error = null;
    }

    public void Refresh(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) { _titlesById.Clear(); _writeUtc = default; _length = -1; Error = "本地会话标题索引未提供"; return; }
            if (info.LastWriteTimeUtc == _writeUtc && info.Length == _length) return;
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var id = IncrementalSessionReader.String(doc.RootElement, "id");
                    var title = IncrementalSessionReader.String(doc.RootElement, "thread_name");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title)) next[id] = title;
                }
                catch (JsonException) { }
            }
            _titlesById = next; _writeUtc = info.LastWriteTimeUtc; _length = info.Length; Error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _titlesById.Clear(); _writeUtc = default; _length = -1; Error = "会话标题索引暂时无法读取";
        }
    }

    public string? Resolve(CodexViewIdentity view) => Resolve(view, _titlesById);
    internal static string? Resolve(CodexViewIdentity view, IReadOnlyDictionary<string, string> titles)
    {
        if (view.WindowCount != 1 || view.Error is not null) return null;
        if (view.ThreadId is not null) return view.ThreadId;
        if (string.IsNullOrWhiteSpace(view.Title) || view.Title is "Codex" or "ChatGPT") return null;
        var ids = titles.Where(pair => string.Equals(pair.Value, view.Title, StringComparison.Ordinal))
            .Select(pair => pair.Key).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }
}

internal sealed class CodexVisibleThreadMonitor : IDisposable
{
    private sealed record BindingRequest(SidebarBindingTarget Target, string ThreadId, TaskCompletionSource<string?> Completion);
    private readonly object _sync = new();
    private readonly CodexIpcActiveThreadMonitor _ipc = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _runner;
    private ActiveThreadRouteStatus _status = new(null, 0, false, 0, null);
    private string _root;
    private long _rootRevision;
    private CodexViewIdentity? _view;
    private BindingRequest? _bindingRequest;
    private long _lastReadTimestamp = Stopwatch.GetTimestamp();
    private int _disposed;

    public CodexVisibleThreadMonitor(string root)
    {
        _root = root;
        _runner = new Thread(Run) { IsBackground = true, Name = "Codex visible conversation" };
        _runner.SetApartmentState(ApartmentState.STA);
        _runner.Start();
    }
    public ActiveThreadRouteStatus GetStatus()
    {
        lock (_sync)
        {
            // A blocked renderer must not leave the previous conversation visible indefinitely.
            if (Stopwatch.GetElapsedTime(_lastReadTimestamp) > TimeSpan.FromSeconds(1) &&
                (_status.ThreadId is not null || _status.LastError != "当前页面读取超时"))
                _status = _status with { ThreadId = null, LastError = "当前页面读取超时", Version = _status.Version + 1 };
            return _status;
        }
    }
    public void SetRoot(string root)
    {
        lock (_sync)
        {
            if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return;
            _root = root; _rootRevision++; _view = null;
            _status = _status with { ThreadId = null, Version = _status.Version + 1 };
        }
        _wake.Set();
    }
    public SidebarBindingTarget? CaptureBindingTarget()
    {
        lock (_sync)
            return _status.IsConnected && _status.ActiveWindowCount == 1 && Stopwatch.GetElapsedTime(_lastReadTimestamp) < TimeSpan.FromSeconds(1)
                && _view is { Error: null, WindowCount: 1, Sidebar.Rows.Count: > 0, Title: not null } view
                ? new(_rootRevision, _status.Version, SidebarSessionResolver.ViewKey(view), view.Title) : null;
    }
    public async Task<string?> BindAsync(SidebarBindingTarget target, string threadId)
    {
        BindingRequest request;
        lock (_sync)
        {
            if (_disposed != 0) return "工具已退出";
            _bindingRequest?.Completion.TrySetResult("绑定操作已被更新的操作替换");
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            request = new(target, threadId, completion); _bindingRequest = request; _wake.Set();
        }
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                // Remove the queued request before reporting failure. A late
                // renderer response must never establish a binding after timeout.
                if (request.Completion.Task.IsCompletedSuccessfully) return request.Completion.Task.Result;
                if (ReferenceEquals(_bindingRequest, request)) _bindingRequest = null;
                const string error = "当前页面核对超时，请稍后重新绑定";
                request.Completion.TrySetResult(error); return error;
            }
        }
    }
    internal static ActiveThreadRouteStatus Advance(ActiveThreadRouteStatus previous, ActiveThreadRouteStatus next)
    {
        next = next with { Version = previous.Version };
        return next == previous ? previous : next with { Version = previous.Version + 1 };
    }
    private void Run()
    {
        var index = new SessionTitleIndex();
        var projects = new LocalProjectIndex();
        var resolver = new SidebarSessionResolver();
        using var sidebar = new CodexSidebarAccessibility();
        long observedRoot = -1;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                string root; long rootRevision; lock (_sync) { root = _root; rootRevision = _rootRevision; }
                ActiveThreadRouteStatus next;
                CodexViewIdentity? observedView = null;
                try
                {
                    index.SetRoot(root); index.Refresh(_cancellation.Token);
                    projects.SetRoot(root); projects.Refresh();
                    if (observedRoot != rootRevision) { resolver.Reset(); sidebar.Reset(); observedRoot = rootRevision; }
                    var view = CodexViewIdentityReader.Read(sidebar);
                    observedView = view;
                    var ipc = _ipc.GetStatus();
                    var key = SidebarSessionResolver.ViewKey(view);
                    lock (_sync)
                    {
                        if (_bindingRequest is { } request)
                        {
                            _bindingRequest = null;
                            string? bindError = "选择期间当前对话发生变化，请重新绑定";
                            if (rootRevision == _rootRevision && request.Target.Matches(rootRevision, _status.Version, view, ipc.IsConnected))
                                resolver.TryBind(view, request.ThreadId, index.Titles, projects, out bindError);
                            request.Completion.TrySetResult(bindError);
                        }
                    }
                    var resolved = resolver.Resolve(view, index.Titles, projects);
                    var id = ipc.IsConnected ? resolved.ThreadId : null;
                    var error = view.Error ?? (id is null && view.WindowCount == 1 ? index.Error ?? resolved.Error : null);
                    next = new(id, view.WindowCount, ipc.IsConnected, 0, error, key, resolved.Method);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException or InvalidOperationException)
                {
                    next = new(null, 0, _ipc.GetStatus().IsConnected, 0, "当前页面暂时无法读取");
                }
                lock (_sync)
                {
                    // A directory change that raced the accessibility read must not restore an old identity.
                    if (rootRevision == _rootRevision)
                    {
                        _view = observedView;
                        _lastReadTimestamp = Stopwatch.GetTimestamp();
                        _status = Advance(_status, next);
                    }
                }
                _wake.WaitOne(150);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel(); _wake.Set();
        lock (_sync) { _bindingRequest?.Completion.TrySetResult("工具已退出"); _bindingRequest = null; }
        // An inaccessible or hung renderer may take longer to answer; never block the UI indefinitely.
        if (_runner.Join(500)) { _wake.Dispose(); _cancellation.Dispose(); }
        _ipc.Dispose();
    }
}
