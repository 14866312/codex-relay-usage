using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal sealed record SessionEntry(string ThreadId, string LogPath, DateTime WriteUtc, long Length, string? Cwd, string? Title);

internal sealed class TokenLogMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Dictionary<string, SessionEntry> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private string _root;
    private string? _preferred, _pinnedId, _activeId;
    private bool _pinned;
    internal const int ReaderCacheCapacity = 8;
    private readonly Dictionary<string, IncrementalSessionReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _readerOrder = new();
    private long _version;
    private int _dirty = 1;
    private DateTime _lastScan;
    private bool _disposed;
    public string? LastError { get; private set; }
    public event Action? DataChanged;

    public TokenLogMonitor(string? sessionRoot = null)
    {
        _root = NormalizeRoot(sessionRoot ?? SessionPathResolver.Resolve());
        CreateWatchers();
    }
    public string SessionRoot { get { lock (_sync) return _root; } }
    public long ActiveSessionVersion { get { lock (_sync) return _version; } }
    public string? ActiveThreadId { get { lock (_sync) return _activeId; } }
    public string? PreferredThreadId { get { lock (_sync) return _preferred; } set { lock (_sync) _preferred = CleanId(value); } }
    public bool PinActiveSession
    {
        get { lock (_sync) return _pinned; }
        set { lock (_sync) { _pinned = value; _pinnedId = value ? _activeId ?? _preferred : null; } }
    }
    public void SelectManual(string id)
    {
        lock (_sync) { _pinnedId = CleanId(id); _pinned = _pinnedId is not null; }
    }
    public void SetRoot(string root)
    {
        lock (_sync)
        {
            var normalized = NormalizeRoot(root);
            if (string.Equals(_root, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _root = normalized;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear(); _catalog.Clear(); _readers.Clear(); _readerOrder.Clear(); _version++; _lastScan = default;
            Interlocked.Exchange(ref _dirty, 1); CreateWatchers();
        }
    }
    internal static string NormalizeRoot(string path)
    {
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        var isHome = !name.Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
            (name.Equals(".codex", StringComparison.OrdinalIgnoreCase) || Directory.Exists(System.IO.Path.Combine(path, "sessions")) || Directory.Exists(System.IO.Path.Combine(path, "archived_sessions")));
        return isHome ? System.IO.Path.Combine(path, "sessions") : path;
    }
    private static string? CleanId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private IEnumerable<string> Roots()
    {
        yield return _root;
        var parent = Path.GetDirectoryName(_root);
        if (parent is not null) yield return Path.Combine(parent, "archived_sessions");
    }
    private void CreateWatchers()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root) || _watchers.Any(w => string.Equals(w.Path, root, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var watcher = new FileSystemWatcher(root, "*.jsonl")
                { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                // Appending to a known log needs an incremental read, not a full directory scan.
                watcher.Changed += (_, _) => DataChanged?.Invoke();
                watcher.Created += (_, _) => NotifyCatalogChange();
                watcher.Deleted += (_, _) => NotifyCatalogChange();
                watcher.Renamed += (_, _) => NotifyCatalogChange();
                watcher.Error += (_, _) => NotifyCatalogChange();
                watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
    private void NotifyCatalogChange()
    {
        Interlocked.Exchange(ref _dirty, 1);
        DataChanged?.Invoke();
    }
    public IReadOnlyList<SessionEntry> ListSessions()
    {
        lock (_sync) { Scan(); return _catalog.Values.OrderByDescending(s => s.WriteUtc).ToArray(); }
    }
    private void Scan(CancellationToken cancellationToken = default)
    {
        var next = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        var titles = ReadTitles(cancellationToken);
        LastError = null;
        foreach (var root in Roots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                var paths = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var file = new FileInfo(path);
                        // Read only metadata, never index conversation contents.
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var first = reader.ReadLine();
                        if (first is null || first.Length > 128 * 1024) continue;
                        using var doc = JsonDocument.Parse(first);
                        if (IncrementalSessionReader.String(doc.RootElement, "type") != "session_meta" ||
                            !doc.RootElement.TryGetProperty("payload", out var meta)) continue;
                        var id = IncrementalSessionReader.String(meta, "id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var entry = new SessionEntry(id, path, file.LastWriteTimeUtc, file.Length,
                            IncrementalSessionReader.String(meta, "cwd"), titles.GetValueOrDefault(id));
                        // One copy per thread. Pick the newest complete file; never add archive + active.
                        if (!next.TryGetValue(id, out var previous) || entry.WriteUtc > previous.WriteUtc ||
                            (entry.WriteUtc == previous.WriteUtc && entry.Length > previous.Length)) next[id] = entry;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = "日志目录暂时无法读取"; }
        }
        _catalog = next; _lastScan = DateTime.UtcNow;
        CreateWatchers();
    }
    private Dictionary<string, string> ReadTitles(CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_root)!, "session_index.jsonl");
            if (!File.Exists(path)) return titles;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { using var doc = JsonDocument.Parse(line); var id = IncrementalSessionReader.String(doc.RootElement, "id");
                    var title = IncrementalSessionReader.String(doc.RootElement, "thread_name");
                    if (id is not null && title is not null) titles[id] = title; } catch (JsonException) { }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return titles;
    }
    public TokenSnapshot? Poll(bool forceFullScan = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var selected = _pinned ? _pinnedId : _preferred;
            if (!string.Equals(selected, _activeId, StringComparison.OrdinalIgnoreCase))
            { _activeId = selected; _version++; }
            if (selected is null) return null;
            if (forceFullScan || Interlocked.Exchange(ref _dirty, 0) != 0 || DateTime.UtcNow - _lastScan >= TimeSpan.FromSeconds(1)) Scan(cancellationToken);
            if (!_catalog.TryGetValue(selected, out var entry)) { _readers.Remove(selected); _readerOrder.Remove(selected); LastError ??= "等待所选会话日志"; return null; }
            if (!_readers.TryGetValue(selected, out var reader) || !string.Equals(reader.Path, entry.LogPath, StringComparison.OrdinalIgnoreCase))
                _readers[selected] = reader = new IncrementalSessionReader(entry.LogPath, selected);
            _readerOrder.Remove(selected); _readerOrder.AddLast(selected);
            while (_readers.Count > ReaderCacheCapacity)
            {
                _readers.Remove(_readerOrder.First!.Value); _readerOrder.RemoveFirst();
            }
            try { var snapshot = reader.Read(cancellationToken); LastError = null; return snapshot; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { LastError = "会话日志暂时无法读取"; return reader.Snapshot; }
        }
    }
    internal long? ReaderBytesRead(string threadId)
    { lock (_sync) return _readers.TryGetValue(threadId, out var reader) ? reader.TotalBytesRead : null; }
    public void Dispose()
    {
        lock (_sync) { _disposed = true; foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
    }
}

internal sealed class IncrementalSessionReader(string path, string threadId)
{
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private long _offset, _knownLength;
    private long _snapshotOffset = -1;
    private byte[]? _buffer;
    private readonly MemoryStream _partial = new();
    private readonly HashSet<string> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningTurns = new(StringComparer.Ordinal);
    private bool _skipLine, _usageRecorded, _totalTokenInvalid;
    private DateTime _writeUtc, _creationUtc, _updatedUtc;
    private long? _total, _input, _cache, _output, _reasoning, _contextUsed, _contextWindow;
    private string? _model, _parseIssue;
    private SessionUsageLedger _ledger = new(threadId);
    private LiveTokenUsageAccumulator _liveUsage = new();
    public string Path { get; } = path;
    public TokenSnapshot? Snapshot { get; private set; }
    internal long TotalBytesRead { get; private set; }
    internal long ReadOffset => _offset;

    public TokenSnapshot? Read(CancellationToken cancellationToken = default, Action? blockProcessed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(Path);
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Truncation, replacement, or same-length rewrite restarts this reader.
        if (stream.Length < _offset || (_offset > 0 && (_creationUtc != info.CreationTimeUtc ||
            (stream.Length == _knownLength && info.LastWriteTimeUtc != _writeUtc)))) Reset();
        stream.Position = _offset;
        // Persist the fingerprint at open so a cancelled read can resume even when it ended at EOF.
        _writeUtc = info.LastWriteTimeUtc; _creationUtc = info.CreationTimeUtc; _knownLength = stream.Length;
        if (_offset == stream.Length && _snapshotOffset == _offset && Snapshot is not null) return Snapshot;
        var buffer = _buffer ??= new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != 10) continue;
                Append(buffer.AsSpan(start, i - start));
                if (!_skipLine) Process(_partial.ToArray());
                _partial.SetLength(0); _skipLine = false; start = i + 1;
            }
            Append(buffer.AsSpan(start, count - start));
            // Offset and partial-line state advance together, only between complete blocks.
            _offset += count; TotalBytesRead += count;
            blockProcessed?.Invoke();
        }
        info.Refresh(); _writeUtc = info.LastWriteTimeUtc; _creationUtc = info.CreationTimeUtc; _knownLength = stream.Length;
        var ledger = _ledger.Snapshot();
        var live = _liveUsage.Select(new(_input, _cache, null, _output, _reasoning, _total, _parseIssue), _usageRecorded);
        var issues = new[] { _parseIssue, _liveUsage.Issue }.OfType<string>().Concat(ledger.Issues).Distinct().ToArray();
        Snapshot = new TokenSnapshot(threadId, Path, live?.Total ?? (live is null ? _total : TokenSnapshot.Sum(live.Input, live.Output)),
            live is null ? _input : live.Input, live is null ? _cache : live.CacheRead,
            live is null ? _output : live.Output, live is null ? _reasoning : live.Reasoning,
            _contextUsed, _contextWindow, _updatedUtc, _model, _turns.Count,
            issues.Length == 0 ? live?.ParseIssue : string.Join("；", issues), _usageRecorded || live is not null,
            live is null ? _totalTokenInvalid : live.ParseIssue?.Contains("total_tokens", StringComparison.Ordinal) == true, ledger,
            _runningTurns.Count > 0, _liveUsage.Source);
        _snapshotOffset = _offset;
        return Snapshot;
    }
    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_skipLine) return;
        if (_partial.Length + bytes.Length > MaxLineBytes) { _partial.SetLength(0); _skipLine = true; return; }
        _partial.Write(bytes);
    }
    private void Reset()
    {
        _offset = _knownLength = 0; _partial.SetLength(0); _turns.Clear(); _skipLine = false; _usageRecorded = false; _totalTokenInvalid = false;
        _snapshotOffset = -1;
        _total = _input = _cache = _output = _reasoning = _contextUsed = _contextWindow = null;
        _model = _parseIssue = null; _updatedUtc = default; Snapshot = null;
        _ledger = new(threadId);
        _liveUsage = new(); _runningTurns.Clear();
    }
    private void Process(byte[] bytes)
    {
        try
        {
            var start = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
            using var doc = JsonDocument.Parse(bytes.AsMemory(start));
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            var type = String(root, "type");
            if (type == "turn_context") { _ledger.ObserveContext(payload); _model = String(payload, "model") ?? _model; return; }
            if (type == "token_usage_record")
            {
                if (_ledger.ObserveRecord(payload))
                {
                    var usageRecord = payload.TryGetProperty("usage", out var raw) && raw.ValueKind == JsonValueKind.Object
                        ? UsageAmounts.Parse(raw) : new UsageAmounts(null, null, null, null);
                    var aggregate = payload.TryGetProperty("thread_token_usage", out var reported) && reported.ValueKind == JsonValueKind.Object
                        ? UsageAmounts.Parse(reported) : null;
                    var hasIdentity = new[] { String(payload, "thread_id"), String(payload, "session_id"), String(payload, "response_id") }
                        .All(value => !string.IsNullOrWhiteSpace(value));
                    _liveUsage.Observe(usageRecord, aggregate, hasIdentity);
                    _contextUsed = usageRecord.Total ?? TokenSnapshot.Sum(usageRecord.Input, usageRecord.Output);
                    UpdateTimestamp(root);
                }
                return;
            }
            if (type != "event_msg") return;
            var eventType = String(payload, "type");
            if (eventType == "task_started")
            {
                var turn = String(payload, "turn_id");
                if (turn is not null) { _turns.Add(turn); _runningTurns.Add(turn); }
                return;
            }
            if (eventType is "task_complete" or "task_completed" or "turn_aborted")
            {
                if (String(payload, "turn_id") is { } turn) _runningTurns.Remove(turn);
                else _runningTurns.Clear();
                return;
            }
            if (eventType != "token_count" || !payload.TryGetProperty("info", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
            var issues = new List<string>();
            if (usage.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
            {
                _ledger.ObserveLegacy(total);
                _input = Number(total, "input_tokens", issues); _cache = Number(total, "cached_input_tokens", issues);
                _output = Number(total, "output_tokens", issues); _reasoning = Number(total, "reasoning_output_tokens", issues);
                _total = Number(total, "total_tokens", issues); _usageRecorded = true;
                _totalTokenInvalid = total.TryGetProperty("total_tokens", out var reportedTotal) && reportedTotal.ValueKind != JsonValueKind.Null &&
                    (reportedTotal.ValueKind != JsonValueKind.Number || !reportedTotal.TryGetInt64(out _));
                _parseIssue = issues.Count == 0 ? null : string.Join("；", issues);
            }
            // This is the last call estimate, not cumulative conversation usage.
            _contextUsed = null;
            if (usage.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                _contextUsed = Number(last, "total_tokens", issues) ?? TokenSnapshot.Sum(Number(last, "input_tokens", issues), Number(last, "output_tokens", issues));
            _contextWindow = Number(usage, "model_context_window", issues);
            if (issues.Count > 0) _parseIssue = string.Join("；", issues.Distinct());
            UpdateTimestamp(root);
        }
        catch (JsonException) { /* Partial lines are never passed here; a damaged full row is ignored. */ }
    }
    private void UpdateTimestamp(JsonElement root)
    {
        if (DateTime.TryParse(String(root, "timestamp"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var stamp))
            _updatedUtc = stamp.ToUniversalTime();
    }
    internal static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement element, string name, List<string> issues)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        issues.Add(name + " 非整数或超出范围"); return null;
    }
}
