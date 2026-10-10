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
    private sealed record IdentifyRequest(SidebarBindingTarget Target, TaskCompletionSource<string?> Completion,
        CancellationTokenSource Cancellation)
    {
        public bool Started { get; set; }
    }
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _runner;
    private readonly Func<CodexViewIdentity>? _readViewOverride;
    private readonly Func<CodexViewIdentity, CancellationToken, CopiedConversationLink> _copyLink;
    private readonly TimeSpan _identifyTimeout;
    private ActiveThreadRouteStatus _status = new(null, 0, false, 0, null);
    private string _root;
    private long _rootRevision;
    private CodexViewIdentity? _view;
    private BindingRequest? _bindingRequest;
    private IdentifyRequest? _identifyRequest;
    private long _lastReadTimestamp = Stopwatch.GetTimestamp();
    private int _disposed;
    public event Action? StatusChanged;

    public CodexVisibleThreadMonitor(string root, Func<CodexViewIdentity>? readView = null,
        Func<CodexViewIdentity, CancellationToken, CopiedConversationLink>? copyLink = null, TimeSpan? identifyTimeout = null)
    {
        _root = root;
        _readViewOverride = readView; _copyLink = copyLink ?? CurrentConversationLink.Read;
        _identifyTimeout = identifyTimeout ?? TimeSpan.FromSeconds(6);
        _runner = new Thread(Run) { IsBackground = true, Name = "Codex visible conversation" };
        _runner.SetApartmentState(ApartmentState.STA);
        _runner.Start();
    }
    public ActiveThreadRouteStatus GetStatus()
    {
        lock (_sync)
        {
            // A blocked renderer must not leave the previous conversation visible indefinitely.
            var identifyingEmptyPage = _identifyRequest is { Started: true } && _status.ThreadId is null;
            if (!identifyingEmptyPage && _status.ActiveWindowCount > 0 && Stopwatch.GetElapsedTime(_lastReadTimestamp) > TimeSpan.FromSeconds(1) &&
                (_status.ThreadId is not null || _status.LastError != "当前页面读取超时"))
                _status = _status with { ThreadId = null, LastError = "当前页面读取超时", Version = _status.Version + 1 };
            return _status;
        }
    }
    public void Wake()
    {
        lock (_sync)
            if (_disposed == 0) _wake.Set();
    }
    public void SetRoot(string root)
    {
        lock (_sync)
        {
            if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return;
            CancelIdentify("日志目录已变化，请重新点击识别");
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
    // Capture the current native row before asking Codex to copy its exact deep link.
    // Background stream subscriptions deliberately play no part in identification.
    public async Task<string?> IdentifyVisibleAsync()
    {
        IdentifyRequest request;
        lock (_sync)
        {
            if (_disposed != 0) return "工具已退出";
            var target = CaptureBindingTarget();
            if (target is null) return "请展开 Codex 侧栏，回到目标对话后点击识别";
            CancelIdentify("识别操作已被更新的操作替换");
            request = new(target, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously), new());
            _identifyRequest = request;
        }
        _wake.Set();
        try { return await request.Completion.Task.WaitAsync(_identifyTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                if (request.Completion.Task.IsCompletedSuccessfully) return request.Completion.Task.Result;
                const string error = "当前页面核对超时，请稍后重新识别";
                if (ReferenceEquals(_identifyRequest, request)) CancelIdentify(error);
                request.Completion.TrySetResult(error);
                return error;
            }
        }
    }
    // Caller owns _sync. An in-flight request disposes its own cancellation source.
    private void CancelIdentify(string error)
    {
        if (_identifyRequest is not { } request) return;
        _identifyRequest = null;
        request.Cancellation.Cancel(); request.Completion.TrySetResult(error);
        if (!request.Started) request.Cancellation.Dispose();
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
        using var debugRoute = new CodexDebugRouteReader();
        long observedRoot = -1;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                string root; long rootRevision; lock (_sync) { root = _root; rootRevision = _rootRevision; }
                IdentifyRequest? identify = null;
                try
                {
                    var exact = _readViewOverride is null ? debugRoute.Read(_cancellation.Token) : null;
                    if (exact is not null)
                    {
                        var changed = false;
                        lock (_sync)
                        {
                            if (rootRevision == _rootRevision)
                            {
                                _view = null;
                                _lastReadTimestamp = Stopwatch.GetTimestamp();
                                var previous = _status;
                                _status = Advance(previous, exact);
                                changed = _status.Version != previous.Version;
                                _bindingRequest?.Completion.TrySetResult("已启用页面唯一 ID 自动识别，无需绑定");
                                _bindingRequest = null;
                                if (_identifyRequest is { } queued)
                                {
                                    _identifyRequest = null;
                                    queued.Completion.TrySetResult(exact.ThreadId is not null ? null : exact.LastError ?? "当前页面不是本地对话");
                                    queued.Cancellation.Dispose();
                                }
                            }
                        }
                        if (changed) StatusChanged?.Invoke();
                        _wake.WaitOne(exact.ActiveWindowCount == 0 ? 1000 : 100);
                        continue;
                    }
                    index.SetRoot(root); index.Refresh(_cancellation.Token);
                    projects.SetRoot(root); projects.Refresh();
                    if (observedRoot != rootRevision) { resolver.Reset(); sidebar.Reset(); observedRoot = rootRevision; }
                    var view = _readViewOverride?.Invoke() ?? CodexViewIdentityReader.Read(sidebar);
                    var before = view;
                    CopiedConversationLink? copied = null;
                    lock (_sync)
                    {
                        if (_identifyRequest is { } queued)
                        {
                            identify = queued; identify.Started = true;
                            if (rootRevision != _rootRevision || !queued.Target.Matches(rootRevision, _status.Version, view,
                                view.WindowCount == 1 && view.Error is null))
                                copied = new(null, "识别期间切换了对话，请在目标对话重新点击识别");
                        }
                    }
                    if (identify is not null && copied is null)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, identify.Cancellation.Token);
                        copied = _copyLink(view, linked.Token);
                        view = _readViewOverride?.Invoke() ?? CodexViewIdentityReader.Read(sidebar);
                    }
                    var statusChanged = false;
                    lock (_sync)
                    {
                        if (rootRevision == _rootRevision)
                        {
                            var connected = view.WindowCount == 1 && view.Error is null;
                            if (_bindingRequest is { } binding)
                            {
                                _bindingRequest = null;
                                string? error = "选择期间当前对话发生变化，请重新绑定";
                                if (binding.Target.Matches(rootRevision, _status.Version, view, connected))
                                    resolver.TryBind(view, binding.ThreadId, index.Titles, projects, out error);
                                binding.Completion.TrySetResult(error);
                            }
                            string? identifyError = null;
                            var publishIdentify = identify is not null && ReferenceEquals(_identifyRequest, identify)
                                && !identify.Completion.Task.IsCompleted && !identify.Cancellation.IsCancellationRequested;
                            if (publishIdentify)
                            {
                                identifyError = !identify!.Target.Matches(rootRevision, _status.Version, view, connected)
                                    ? "识别期间切换了对话，请在目标对话重新点击识别"
                                    : copied?.Error ?? resolver.ResolveCopiedLink(before, view, copied?.Link, index.Titles, projects).Error;
                            }
                            var resolved = resolver.Resolve(view, index.Titles, projects);
                            var errorText = view.Error ?? (resolved.ThreadId is null && view.WindowCount == 1 ? index.Error ?? resolved.Error : null);
                            var next = new ActiveThreadRouteStatus(resolved.ThreadId, view.WindowCount, connected, 0, errorText,
                                SidebarSessionResolver.ViewKey(view), resolved.Method, resolved.SameTitleAmbiguous);
                            _view = view; _lastReadTimestamp = Stopwatch.GetTimestamp();
                            var previous = _status; _status = Advance(previous, next);
                            statusChanged = _status.Version != previous.Version;
                            // Publish the exact identity before completing the click task.
                            if (publishIdentify) { _identifyRequest = null; identify!.Completion.TrySetResult(identifyError); }
                        }
                        else if (ReferenceEquals(_identifyRequest, identify)) CancelIdentify("日志目录已变化，请重新点击识别");
                    }
                    if (statusChanged) StatusChanged?.Invoke();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException or InvalidOperationException)
                {
                    lock (_sync)
                    {
                        _view = null; _lastReadTimestamp = Stopwatch.GetTimestamp();
                        _status = Advance(_status, new(null, 0, false, 0, "当前页面暂时无法读取"));
                        if (ReferenceEquals(_identifyRequest, identify)) CancelIdentify("当前页面暂时无法读取，请稍后识别");
                    }
                    StatusChanged?.Invoke();
                }
                finally { identify?.Cancellation.Dispose(); }
                _wake.WaitOne(GetStatus().ActiveWindowCount == 0 ? 1000 : 150);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel(); _wake.Set();
        lock (_sync) { _bindingRequest?.Completion.TrySetResult("工具已退出"); _bindingRequest = null; CancelIdentify("工具已退出"); }
        // An inaccessible or hung renderer may take longer to answer; never block the UI indefinitely.
        if (_runner.Join(500)) { _wake.Dispose(); _cancellation.Dispose(); }
    }
}
