using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Accessibility;

namespace CodexTokenOverlay;

internal sealed record CodexViewIdentity(int WindowCount, string? Title, string? ThreadId = null, string? Error = null);

// Stream subscriptions survive navigation. Only the actual Codex document identifies the visible page.
internal static class CodexViewIdentityReader
{
    private const int DocumentRole = (int)AccessibleRole.Document;
    private const int InvisibleOrOffscreen = (int)(AccessibleStates.Invisible | AccessibleStates.Offscreen);
    private static readonly Guid AccessibleInterface = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static CodexViewIdentity Read()
    {
        var windows = CodexWindowLocator.GetVisibleMainWindows();
        if (windows.Count != 1) return new(windows.Count, null);
        return ReadWindow(windows[0].Handle);
    }

    internal static CodexViewIdentity ReadWindow(IntPtr handle)
    {
        try
        {
            var iid = AccessibleInterface;
            if (AccessibleObjectFromWindow(handle, 0xFFFFFFFC, ref iid, out var root) < 0 || root is not IAccessible accessible)
                return new(1, null, Error: "无法读取当前页面");
            var queue = new Queue<(IAccessible Node, int Depth)>();
            var visited = new HashSet<object>();
            var documents = new List<CodexViewIdentity>();
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
                    if (IsCodexDocument(url)) documents.Add(new(1, node.get_accName(0), ThreadIdFromUrl(url)));
                    continue;
                }
                var count = Math.Min(64, node.accChildCount);
                if (count <= 0) continue;
                var children = new object[count];
                if (AccessibleChildren(node, 0, count, children, out var actual) < 0) continue;
                foreach (var child in children.Take(actual))
                    if (child is IAccessible nested) queue.Enqueue((nested, depth + 1));
            }
            return documents.Count == 1 ? documents[0] : new(1, null, Error: "无法唯一识别当前页面");
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
        {
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

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object result);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible container, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int actual);
}

internal sealed class SessionTitleIndex
{
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
    private readonly object _sync = new();
    private readonly CodexIpcActiveThreadMonitor _ipc = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _runner;
    private ActiveThreadRouteStatus _status = new(null, 0, false, 0, null);
    private string _root;
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
            _root = root;
            _status = _status with { ThreadId = null, Version = _status.Version + 1 };
        }
        _wake.Set();
    }
    private void Run()
    {
        var index = new SessionTitleIndex();
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                string root; lock (_sync) root = _root;
                ActiveThreadRouteStatus next;
                try
                {
                    index.SetRoot(root); index.Refresh(_cancellation.Token);
                    var view = CodexViewIdentityReader.Read();
                    var ipc = _ipc.GetStatus();
                    var id = ipc.IsConnected ? index.Resolve(view) : null;
                    var error = view.Error ?? (id is null && view.WindowCount == 1
                        ? index.Error ?? "当前页面标题无法唯一匹配会话" : null);
                    next = new(id, view.WindowCount, ipc.IsConnected, 0, error);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException or InvalidOperationException)
                {
                    next = new(null, 0, _ipc.GetStatus().IsConnected, 0, "当前页面暂时无法读取");
                }
                lock (_sync)
                {
                    // A directory change that raced the accessibility read must not restore an old identity.
                    if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastReadTimestamp = Stopwatch.GetTimestamp();
                        next = next with { Version = _status.Version };
                        if (next != _status) _status = next with { Version = _status.Version + 1 };
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
        // An inaccessible or hung renderer may take longer to answer; never block the UI indefinitely.
        if (_runner.Join(500)) { _wake.Dispose(); _cancellation.Dispose(); }
        _ipc.Dispose();
    }
}
