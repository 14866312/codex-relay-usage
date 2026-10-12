using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal sealed record SessionEntry(string ThreadId, string LogPath, DateTime WriteUtc, long Length, string? Cwd, string? Title,
    SessionRelationInfo? Relation = null);

internal sealed class TokenLogMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Dictionary<string, SessionEntry> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private string _root;
    internal const int ReaderCacheCapacity = 8;
    // Related members keep their own readers so a group larger than the selection cache
    // never restarts from byte zero; the group monitor releases members it dropped.
    // Root plus the documented 32-member target must survive a refresh without restarting.
    internal const int MemberReaderCapacity = 64;
    private readonly Dictionary<string, IncrementalSessionReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _readerOrder = new();
    private readonly Dictionary<string, IncrementalSessionReader> _memberReaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _memberOrder = new();
    private RelatedSessionCatalogSnapshot _relations = RelatedSessionCatalogSnapshot.Empty;
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
    public RelatedSessionCatalogSnapshot Relations { get { lock (_sync) return _relations; } }
    // Refreshes the catalog only when the directory changed or the scan went stale.
    internal RelatedSessionCatalogSnapshot RefreshRelations(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || DateTime.UtcNow - _lastScan >= TimeSpan.FromSeconds(1)) Scan(cancellationToken);
            return _relations;
        }
    }
    public void SetRoot(string root)
    {
        lock (_sync)
        {
            var normalized = NormalizeRoot(root);
            if (string.Equals(_root, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _root = normalized;
            foreach (var watcher in _watchers) watcher.Dispose();
            foreach (var cached in _readers.Values) cached.Dispose();
            foreach (var cached in _memberReaders.Values) cached.Dispose();
            _watchers.Clear(); _catalog.Clear(); _readers.Clear(); _readerOrder.Clear(); _lastScan = default;
            _memberReaders.Clear(); _memberOrder.Clear(); _relations = RelatedSessionCatalogSnapshot.Empty;
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep polling even when a root cannot be watched; diagnostics can show it was ignored.
                DiagnosticsSink.Record("watcher:" + root, e);
            }
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
                            IncrementalSessionReader.String(meta, "cwd"), titles.GetValueOrDefault(id),
                            SessionRelationInfo.Parse(id, meta));
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
        _relations = RelatedSessionCatalog.Build(next.Values, _relations);
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
    // Reads exactly the given session; a null id keeps returning the empty result an
    // unselected monitor used to give. Which session that is lives in SessionSelection.
    public TokenSnapshot? Poll(string? threadId, bool forceFullScan = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var selected = CleanId(threadId);
            if (selected is null) return null;
            if (forceFullScan || Interlocked.Exchange(ref _dirty, 0) != 0 || DateTime.UtcNow - _lastScan >= TimeSpan.FromSeconds(1)) Scan(cancellationToken);
            if (!_catalog.TryGetValue(selected, out var entry))
            {
                if (_readers.Remove(selected, out var missing)) missing.Dispose();
                _readerOrder.Remove(selected); LastError ??= "等待所选会话日志"; return null;
            }
            if (!_readers.TryGetValue(selected, out var reader) || !string.Equals(reader.Path, entry.LogPath, StringComparison.OrdinalIgnoreCase))
                _readers[selected] = reader = new IncrementalSessionReader(entry.LogPath, selected);
            _readerOrder.Remove(selected); _readerOrder.AddLast(selected);
            while (_readers.Count > ReaderCacheCapacity)
            {
                _readers.Remove(_readerOrder.First!.Value, out var evicted);
                _readerOrder.RemoveFirst();
                evicted?.Dispose();
            }
            try { var snapshot = reader.Read(cancellationToken: cancellationToken); LastError = null; return snapshot; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { LastError = "会话日志暂时无法读取"; return reader.Snapshot; }
        }
    }
    internal long? ReaderBytesRead(string threadId)
    { lock (_sync) return _readers.TryGetValue(threadId, out var reader) ? reader.TotalBytesRead : null; }
    internal long? MemberBytesRead(string threadId)
    { lock (_sync) return _memberReaders.TryGetValue(threadId, out var reader) ? reader.TotalBytesRead : null; }

    // Reads one related member incrementally with a byte budget, so a large member log is
    // consumed in bounded slices and never blocks the main conversation's publication.
    public (TokenSnapshot? Snapshot, string? Error) PollMember(string threadId, int byteBudget, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed) return (null, null);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || DateTime.UtcNow - _lastScan >= TimeSpan.FromSeconds(1)) Scan(cancellationToken);
            if (!_catalog.TryGetValue(threadId, out var entry)) return (null, "成员日志尚未出现");
            if (!_memberReaders.TryGetValue(threadId, out var reader) || !string.Equals(reader.Path, entry.LogPath, StringComparison.OrdinalIgnoreCase))
                _memberReaders[threadId] = reader = new IncrementalSessionReader(entry.LogPath, threadId);
            _memberOrder.Remove(threadId); _memberOrder.AddLast(threadId);
            while (_memberReaders.Count > MemberReaderCapacity)
            {
                _memberReaders.Remove(_memberOrder.First!.Value, out var evicted);
                _memberOrder.RemoveFirst();
                evicted?.Dispose();
            }
            try { return (reader.Read(cancellationToken: cancellationToken, byteBudget: byteBudget), null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return (reader.Snapshot, "成员日志暂时无法读取"); }
        }
    }

    // Drops readers for members that left the group; their positions are rebuilt on return.
    internal void ReleaseMembers(IReadOnlyCollection<string> keep)
    {
        lock (_sync)
        {
            foreach (var id in _memberReaders.Keys.Where(id => !keep.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                _memberReaders.Remove(id, out var reader);
                reader?.Dispose(); _memberOrder.Remove(id);
            }
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var watcher in _watchers) watcher.Dispose();
            foreach (var cached in _readers.Values) cached.Dispose();
            foreach (var cached in _memberReaders.Values) cached.Dispose();
            _readers.Clear(); _readerOrder.Clear(); _watchers.Clear();
            _memberReaders.Clear(); _memberOrder.Clear();
        }
    }
}

internal sealed class IncrementalSessionReader(string path, string threadId) : IDisposable
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
    private DateTime _turnStartUtc, _lastRecordUtc;
    private string? _lastRecordTurn;
    private double _speedOutput, _speedSeconds, _completionGenerationSeconds;
    private string? _speedTurn, _completionTurn;
    private double _generationSeconds, _toolSeconds;
    private string? _currentTurn;
    private long? _total, _input, _cache, _output, _reasoning, _contextUsed, _contextWindow;
    private string? _model, _parseIssue;
    private SessionUsageLedger _ledger = new(threadId);
    private LiveTokenUsageAccumulator _liveUsage = new();
    public string Path { get; } = path;
    public TokenSnapshot? Snapshot { get; private set; }
    internal long TotalBytesRead { get; private set; }
    internal long ReadOffset => _offset;
    // The parse buffer is the only unmanaged-ish resource; evicting a cached reader
    // must release it rather than waiting for a finalizer that never runs.
    public void Dispose() => _partial.Dispose();

    // byteBudget stops the read at a complete line boundary so a caller can consume a large
    // member log in bounded slices; 0 reads to the current end of file.
    public TokenSnapshot? Read(Action? blockProcessed = null, int byteBudget = 0, CancellationToken cancellationToken = default)
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
        var budgetStart = TotalBytesRead;
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
            // The budget is checked between whole blocks, never mid-chunk, so the resume
            // offset always lands on data the reader has actually consumed.
            if (byteBudget > 0 && TotalBytesRead - budgetStart >= byteBudget) break;
        }
        info.Refresh(); _writeUtc = info.LastWriteTimeUtc; _creationUtc = info.CreationTimeUtc; _knownLength = stream.Length;
        var moreDataPending = _offset < stream.Length;
        var ledger = _ledger.Snapshot();
        var live = _liveUsage.Select(new(_input, _cache, null, _output, _reasoning, _total, _parseIssue), _usageRecorded);
        var issues = new[] { _parseIssue, _liveUsage.Issue }.OfType<string>().Concat(ledger.Issues).Distinct().ToArray();
        Snapshot = new TokenSnapshot(threadId, Path, live?.Total ?? (live is null ? _total : TokenSnapshot.Sum(live.Input, live.Output)),
            live is null ? _input : live.Input, live is null ? _cache : live.CacheRead,
            live is null ? _output : live.Output, live is null ? _reasoning : live.Reasoning,
            _contextUsed, _contextWindow, _updatedUtc, _model, _turns.Count,
            issues.Length == 0 ? live?.ParseIssue : string.Join("；", issues), _usageRecorded || live is not null,
            live is null ? _totalTokenInvalid : live.ParseIssue?.Contains("total_tokens", StringComparison.Ordinal) == true, ledger,
            _runningTurns.Count > 0, _liveUsage.Source, OutputSpeed,
            SpeedGenerationOnly, _generationSeconds > 0 ? _generationSeconds : null, _toolSeconds > 0 ? _toolSeconds : null,
            moreDataPending);
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
        _turnStartUtc = _lastRecordUtc = default; _lastRecordTurn = null; _speedOutput = 0; _speedSeconds = 0;
        _completionGenerationSeconds = 0; _completionTurn = null; _speedTurn = null;
        _generationSeconds = 0; _toolSeconds = 0; _currentTurn = null;
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
                // Speed is a per-turn measurement, not a ledger entry: relays that omit
                // response_id dedup to one call per thread, yet every record still reports
                // the turn's growing output. Observe the speed before the dedup gate.
                var recordUsage = payload.TryGetProperty("usage", out var rawUsage) && rawUsage.ValueKind == JsonValueKind.Object
                    ? UsageAmounts.Parse(rawUsage) : new UsageAmounts(null, null, null, null);
                ObserveSpeed(root, payload, recordUsage);
                if (_ledger.ObserveRecord(payload))
                {
                    var usageRecord = recordUsage;
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
                _turnStartUtc = Timestamp(root) ?? default;
                _lastRecordUtc = default; _lastRecordTurn = turn;
                _completionGenerationSeconds = 0; _completionTurn = null;
                // Item timings and the reported numerator belong to one turn; a new turn
                // never inherits the previous turn's seconds.
                _currentTurn = turn; _generationSeconds = 0; _toolSeconds = 0;
                _speedOutput = 0; _speedSeconds = 0;
                return;
            }
            if (eventType is "task_complete" or "task_completed")
            {
                var turn = String(payload, "turn_id");
                if (turn is not null) _runningTurns.Remove(turn);
                else _runningTurns.Clear();
                ObserveCompletion(payload, turn);
                return;
            }
            if (eventType == "turn_aborted")
            {
                if (String(payload, "turn_id") is { } aborted) _runningTurns.Remove(aborted);
                else _runningTurns.Clear();
                _completionGenerationSeconds = 0; _completionTurn = null;
                return;
            }
            if (eventType == "item_completed")
            {
                ObserveItem(payload);
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
        if (Timestamp(root) is { } stamp) _updatedUtc = stamp;
    }
    private static DateTime? Timestamp(JsonElement root) =>
        DateTime.TryParse(String(root, "timestamp"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var stamp)
            ? stamp.ToUniversalTime() : null;
    // Average reported output per generation second of the current turn. A completed turn
    // reports its own duration and first-token wait, which is the window the relay bills and
    // reports as tokens per second; only a reported usage record advances this value.
    private void ObserveSpeed(JsonElement root, JsonElement payload, UsageAmounts usageRecord)
    {
        var turn = String(payload, "turn_id");
        var stamp = Timestamp(root);
        if (stamp is null) return;
        // A speed belongs to one turn; a record from another turn never keeps the old numerator.
        if (turn is not null && !string.Equals(turn, _speedTurn, StringComparison.Ordinal))
        { _speedOutput = 0; _speedSeconds = 0; }
        if (_turnStartUtc == default || (turn is not null && _lastRecordTurn is not null && turn != _lastRecordTurn))
        {
            _turnStartUtc = stamp.Value; _lastRecordUtc = default; _speedOutput = 0; _speedSeconds = 0;
            if (_completionTurn is not null && !string.Equals(_completionTurn, turn, StringComparison.Ordinal))
            { _completionGenerationSeconds = 0; _completionTurn = null; }
        }
        _lastRecordTurn = turn ?? _lastRecordTurn;
        var turnOutput = usageRecord.Output;
        if (payload.TryGetProperty("turn_token_usage", out var reported) && reported.ValueKind == JsonValueKind.Object)
            turnOutput = Number(reported, "output_tokens", []) ?? turnOutput;
        if (turnOutput is not { } output || output < 0) return;
        // A reported completion window outranks wall-clock elapsed; it excludes the first-token wait.
        var window = _completionGenerationSeconds >= 1 &&
            (_completionTurn is null || turn is null || string.Equals(_completionTurn, turn, StringComparison.Ordinal))
            ? _completionGenerationSeconds : (stamp.Value - _turnStartUtc).TotalSeconds;
        if (window < 1) return;
        _lastRecordUtc = stamp.Value; _speedOutput = output; _speedSeconds = window; _speedTurn = turn ?? _speedTurn;
    }
    // duration_ms minus time_to_first_token_ms is the relay's generation window. Very short
    // generations keep the full duration so a near-zero window never inflates the rate.
    private void ObserveCompletion(JsonElement payload, string? turn)
    {
        _completionGenerationSeconds = 0; _completionTurn = null;
        if (Number(payload, "duration_ms", []) is not { } durationMs || durationMs <= 0) return;
        var window = (durationMs - (Number(payload, "time_to_first_token_ms", []) ?? 0)) / 1000d;
        if (window < 1) window = durationMs / 1000d;
        if (window <= 0) return;
        _completionGenerationSeconds = window; _completionTurn = turn;
        // Usage records usually precede the completion event, so refresh the current turn now.
        if (turn is not null && _speedOutput > 0 && string.Equals(_speedTurn, turn, StringComparison.Ordinal))
            _speedSeconds = window;
    }
    // Item-completed events carry the per-item generation window the relay reports as
    // tokens per second. Only items of the current turn with a sane duration count;
    // everything else falls back to the wall-clock or completion window.
    private void ObserveItem(JsonElement payload)
    {
        if (_currentTurn is null || !string.Equals(String(payload, "turn_id"), _currentTurn, StringComparison.Ordinal)) return;
        if (!payload.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;
        if (Number(payload, "started_at_ms", []) is not { } started || Number(payload, "completed_at_ms", []) is not { } completed) return;
        var durationMs = completed - started;
        if (durationMs <= 0 || durationMs > 3_600_000) return;
        switch (String(item, "type"))
        {
            case "Reasoning" or "AgentMessage": _generationSeconds += durationMs / 1000d; break;
            case "CommandExecution" or "FileChange" or "McpToolCall": _toolSeconds += durationMs / 1000d; break;
        }
    }
    private double? OutputSpeed
    {
        get
        {
            if (_speedOutput <= 0) return null;
            if (_generationSeconds >= 1) return _speedOutput / _generationSeconds;
            return _speedSeconds >= 1 ? _speedOutput / _speedSeconds : null;
        }
    }
    private bool SpeedGenerationOnly => _speedOutput > 0 && _generationSeconds >= 1;
    internal static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement element, string name, List<string> issues)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        issues.Add(name + " 非整数或超出范围"); return null;
    }
}
