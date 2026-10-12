using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenOverlay;

// One observation of the page state, free of titles, session ids, urls and usage
// numbers: the raw route state, the stage the strip actually shows, the wait duration,
// the window generation, strip visibility, whether numbers are available, and how the
// accessibility recovery was doing.
internal sealed record PageDiagnosticsSample(string Raw, PageWaitStage Stage, double PendingMilliseconds,
    int Generation, bool Visible, bool Numbers, PageRecoverySnapshot? Recovery = null);

internal static class PageDiagnosticsRules
{
    public static string RawKind(ActiveThreadRouteStatus route) =>
        route.ActiveWindowCount == 0 ? "no-window"
        : route.ActiveWindowCount > 1 ? "multi-window"
        : route.ThreadId is not null ? "ready"
        : route.LastError == CodexViewIdentityReader.PagePendingError ? "pending"
        : "unreadable";

    public static string StageName(PageWaitStage stage) => stage switch
    {
        PageWaitStage.Reading => "reading",
        PageWaitStage.NotReady => "not-ready",
        PageWaitStage.LongWait => "long-wait",
        _ => "none"
    };
}

/// <summary>
/// Opt-in page-state recording, off on every launch and never enabled automatically.
/// Each enabled run writes one JSONL file under the diagnostics folder, on a background
/// thread through a bounded queue. A write failure, a full queue or the single-file
/// limit stops the recording with a reason for the tray; statistics keep running.
/// </summary>
internal sealed class PageDiagnosticsRecorder : IDisposable
{
    internal const long DefaultMaximumBytes = 5 * 1024 * 1024;
    internal const int MaximumQueueLength = 512;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexRelayUsage", "diagnostics");

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), MaximumQueueLength);
    private readonly Thread _writer;
    private readonly long _maximumBytes;
    private readonly string _path;
    private readonly object _sync = new();
    private string? _stopReason;
    private long _written;
    private PageDiagnosticsSample? _last;
    private int _disposed;

    private PageDiagnosticsRecorder(string directory, long maximumBytes)
    {
        Directory.CreateDirectory(directory);
        _maximumBytes = maximumBytes;
        _path = Path.Combine(directory, "page-state-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".jsonl");
        // Create the file on this thread so a failed folder is reported by Start, not
        // by the background writer after the tray already reported success.
        using (File.Create(_path)) { }
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Page state diagnostics" };
        _writer.Start();
    }

    public static PageDiagnosticsRecorder? Start(string directory, out string? error, long maximumBytes = DefaultMaximumBytes)
    {
        try
        {
            error = null;
            return new PageDiagnosticsRecorder(directory, maximumBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            error = "无法创建诊断文件";
            return null;
        }
    }

    public string FilePath => _path;
    public string? StopReason { get { lock (_sync) return _stopReason; } }
    public bool IsRunning => StopReason is null && Volatile.Read(ref _disposed) == 0;

    // Only a changed state writes a line: the wait duration advances continuously and
    // must not turn into per-tick sampling.
    public void Observe(PageDiagnosticsSample sample)
    {
        if (!IsRunning) return;
        var previous = _last;
        _last = sample;
        if (previous is not null && previous.Raw == sample.Raw && previous.Stage == sample.Stage
            && previous.Generation == sample.Generation && previous.Visible == sample.Visible
            && previous.Numbers == sample.Numbers && previous.Recovery == sample.Recovery) return;
        var kind = previous is null ? "start" : previous.Raw == "pending" && sample.Raw != "pending" ? "recovered" : "state";
        var pending = kind == "recovered" ? previous!.PendingMilliseconds : sample.PendingMilliseconds;
        TryEnqueue(new PageDiagnosticsLine(kind, DateTime.UtcNow, sample.Raw, PageDiagnosticsRules.StageName(sample.Stage),
            Math.Round(pending), sample.Generation, sample.Visible, sample.Numbers, null, RecoveryLine(sample.Recovery)));
    }

    private static PageRecoveryLine? RecoveryLine(PageRecoverySnapshot? recovery) =>
        recovery is { Completed: > 0 } snapshot
            ? new(snapshot.Completed, PageRecoveryRules.OutcomeName(snapshot.Outcome), Math.Round(snapshot.Milliseconds ?? 0))
            : null;

    public void Stop(string reason)
    {
        lock (_sync)
        {
            if (_stopReason is not null) return;
            _stopReason = reason;
        }
        TryAdd(new PageDiagnosticsLine("stop", DateTime.UtcNow, null, null, null, null, null, null, reason));
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
    }

    private void TryEnqueue(PageDiagnosticsLine line)
    {
        if (!TryAdd(line)) Stop("队列溢出");
    }

    private bool TryAdd(PageDiagnosticsLine line)
    {
        if (_queue.IsAddingCompleted) return false;
        try { return _queue.TryAdd(JsonSerializer.Serialize(line, JsonOptions)); }
        catch (InvalidOperationException) { return false; }
    }

    private void WriteLoop()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_written + bytes > _maximumBytes)
                {
                    Stop("达到单文件上限");
                    return;
                }
                writer.WriteLine(line);
                _written += bytes;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            Stop("写入失败");
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop("工具退出");
        // The writer drains immediately in normal operation; a stuck write must never
        // block the UI thread, so the queue is only disposed once the thread has ended.
        if (_writer.Join(500)) _queue.Dispose();
    }

    private sealed record PageDiagnosticsLine(string Kind, DateTime Time, string? Raw, string? Stage,
        double? PendingMs, int? Generation, bool? Visible, bool? Numbers, string? Reason = null, PageRecoveryLine? Recovery = null);

    private sealed record PageRecoveryLine(int Completed, string? Outcome, double? Milliseconds);
}
