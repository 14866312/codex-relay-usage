using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

// Chromium builds its accessibility tree on demand. Right after a Codex restart the
// MSAA page read can see only an unpopulated document node until a UIA client walks
// the window's raw view to the document boundary, so the monitor asks for one bounded
// walk instead of waiting minutes. The walk reads control types only: never names,
// values, text, inputs or conversation content, and it stops at the first document.
internal enum PageRecoveryOutcome { None, DocumentReached, BoundaryMissing, Timeout, WindowGone, Failed }

internal static class PageRecoveryRules
{
    // Diagnostics stay free of titles, session ids and urls: only the outcome name.
    internal static string? OutcomeName(PageRecoveryOutcome outcome) => outcome switch
    {
        PageRecoveryOutcome.DocumentReached => "document",
        PageRecoveryOutcome.BoundaryMissing => "boundary-missing",
        PageRecoveryOutcome.Timeout => "timeout",
        PageRecoveryOutcome.WindowGone => "window-gone",
        PageRecoveryOutcome.Failed => "failed",
        _ => null
    };
}

// One window lifetime: the handle, process and process start time the pending read
// belongs to. A late walk result can never be attributed to a rebuilt window or a
// reused process id because the worker checks all three before walking.
internal readonly record struct PageRecoveryTarget(long Handle, uint ProcessId, long ProcessStartTicks)
{
    // The monitor's window lifecycle is "<handle>/<pid>/<process start ticks>".
    internal static bool TryParse(string? lifecycle, out PageRecoveryTarget target)
    {
        target = default;
        var parts = lifecycle?.Split('/');
        if (parts is not { Length: 3 }
            || !long.TryParse(parts[0], out var handle) || handle == 0
            || !uint.TryParse(parts[1], out var processId) || processId == 0
            || !long.TryParse(parts[2], out var startTicks) || startTicks == 0)
            return false;
        target = new(handle, processId, startTicks);
        return true;
    }
}

internal sealed record PageRecoverySnapshot(int Completed, PageRecoveryOutcome Outcome, double? Milliseconds)
{
    internal static PageRecoverySnapshot Empty { get; } = new(0, PageRecoveryOutcome.None, null);
}

/// <summary>
/// One background walker on its own MTA thread. The monitor queues at most one request
/// on top of the running walk, so a stuck renderer cannot stack threads or walkers; the
/// worker never touches the UI thread and the monitor never waits for it.
/// </summary>
internal sealed class CodexAccessibilityRecovery : IDisposable
{
    internal const int ConnectionTimeoutMilliseconds = 250;
    internal const int TransactionTimeoutMilliseconds = 250;
    internal const int MaximumNodes = 128;
    internal const int MaximumDepth = 12;
    internal const int MaximumChildrenPerNode = 64;
    internal static readonly TimeSpan TraversalBudget = TimeSpan.FromMilliseconds(1500);
    private const int DocumentControlTypeId = 50030;

    private readonly Func<PageRecoveryTarget, PageRecoveryOutcome> _walk;
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private PageRecoveryTarget? _queued;
    private PageRecoverySnapshot _snapshot = PageRecoverySnapshot.Empty;
    private int _disposed;

    // The test injection replaces the native walk; production passes nothing.
    internal CodexAccessibilityRecovery(Func<PageRecoveryTarget, PageRecoveryOutcome>? walk = null)
    {
        _walk = walk ?? WalkToDocumentBoundary;
        _worker = new Thread(Run) { IsBackground = true, Name = "Page accessibility recovery" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    // At most one queued request on top of the running walk. A request is dropped when
    // the worker is still busy, which keeps a hung renderer from stacking work.
    internal bool TryRequest(PageRecoveryTarget target)
    {
        lock (_sync)
        {
            if (_disposed != 0 || _queued is not null) return false;
            _queued = target;
        }
        _wake.Set();
        return true;
    }

    internal PageRecoverySnapshot Snapshot() { lock (_sync) return _snapshot; }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            PageRecoveryTarget target;
            lock (_sync)
            {
                if (_disposed != 0) return;
                if (_queued is not { } queued) continue;
                _queued = null;
                target = queued;
            }
            var started = Stopwatch.GetTimestamp();
            var outcome = PageRecoveryOutcome.Failed;
            try { outcome = _walk(target); }
            // One bad walk must never kill the worker: a dead worker would look like
            // "always busy" and silently stop the ladder, with nothing in diagnostics.
            catch (Exception e) when (e is not OutOfMemoryException) { }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_sync) _snapshot = new(_snapshot.Completed + 1, outcome, elapsed);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wake.Set();
        // A walk stuck in a dead renderer must not hold up shutdown; the worker is a
        // background thread and exits as soon as the native call returns.
        if (_worker.Join(500)) _wake.Dispose();
    }

    // Walks the raw view from the window element to the document boundary and stops
    // there. The walker visits at most MaximumNodes elements, never deeper than
    // MaximumDepth, never more than MaximumChildrenPerNode children per element, and
    // gives up after TraversalBudget even if the native timeouts do not fire.
    internal static PageRecoveryOutcome WalkToDocumentBoundary(PageRecoveryTarget target)
    {
        var window = new IntPtr(target.Handle);
        if (!IsWindow(window)) return PageRecoveryOutcome.WindowGone;
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId != target.ProcessId || !ProcessStartMatches(target)) return PageRecoveryOutcome.WindowGone;
        var automation = _automation ??= CreateAutomation();
        if (automation is null) return PageRecoveryOutcome.Failed;
        if (automation.ElementFromHandle(window, out var root) < 0 || root is null) return PageRecoveryOutcome.WindowGone;
        if (automation.get_RawViewWalker(out var walker) < 0 || walker is null) return PageRecoveryOutcome.Failed;
        return WalkRawView(walker, root);
    }

    private static PageRecoveryOutcome WalkRawView(IUIAutomationTreeWalker walker, IUIAutomationElement root)
    {
        var deadline = Stopwatch.GetTimestamp();
        var stack = new Stack<(IUIAutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0)
        {
            if (Stopwatch.GetElapsedTime(deadline) >= TraversalBudget) return PageRecoveryOutcome.Timeout;
            var (element, depth) = stack.Pop();
            if (++visited > MaximumNodes) return PageRecoveryOutcome.BoundaryMissing;
            if (element.get_CurrentControlType(out var controlType) < 0) continue;
            if (controlType == DocumentControlTypeId) return PageRecoveryOutcome.DocumentReached;
            if (depth >= MaximumDepth || walker.GetFirstChildElement(element, out var child) < 0) continue;
            for (var index = 0; child is not null && index < MaximumChildrenPerNode; index++)
            {
                if (walker.GetNextSiblingElement(child, out var next) < 0) next = null;
                stack.Push((child, depth + 1));
                child = next;
            }
        }
        return PageRecoveryOutcome.BoundaryMissing;
    }

    // Regression hook: asking the root element alone must not populate the page; only
    // the bounded walk to the document boundary does.
    internal static int? RootControlType(PageRecoveryTarget target)
    {
        var automation = _automation ??= CreateAutomation();
        if (automation is null
            || automation.ElementFromHandle(new IntPtr(target.Handle), out var root) < 0 || root is null
            || root.get_CurrentControlType(out var controlType) < 0) return null;
        return controlType;
    }

    // Regression hook: the 250 ms call timeouts must be in effect for the walk.
    internal static (uint ConnectionTimeout, uint TransactionTimeout)? CurrentTimeouts()
    {
        var automation = _automation ??= CreateAutomation();
        if (automation is null) return null;
        return automation.get_ConnectionTimeout(out var connection) == 0
            && automation.get_TransactionTimeout(out var transaction) == 0 ? (connection, transaction) : null;
    }

    private static IUIAutomation2? CreateAutomation()
    {
        var coclass = Type.GetTypeFromCLSID(new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), throwOnError: false);
        if (coclass is null || Activator.CreateInstance(coclass) is not IUIAutomation2 automation) return null;
        return automation.put_ConnectionTimeout(ConnectionTimeoutMilliseconds) == 0
            && automation.put_TransactionTimeout(TransactionTimeoutMilliseconds) == 0 ? automation : null;
    }

    private static bool ProcessStartMatches(PageRecoveryTarget target)
    {
        try
        {
            using var process = Process.GetProcessById((int)target.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == target.ProcessStartTicks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    // The UIA client is apartment-bound, so it is created once per walker thread.
    [ThreadStatic] private static IUIAutomation2? _automation;
}

// Native UIA client declarations. The vtable layout follows the Windows SDK IDL
// (Include/10.0.26100.0/um/UIAutomationClient.idl). Every method before the ones the
// walk calls is declared as a numbered placeholder so the slots line up; placeholders
// are never called.
[ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2
{
    void _SkipCompareElements();
    void _SkipCompareRuntimeIds();
    void _SkipGetRootElement();
    [PreserveSig] int ElementFromHandle(IntPtr window, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
    void _SkipElementFromPoint();
    void _SkipGetFocusedElement();
    void _SkipGetRootElementBuildCache();
    void _SkipElementFromHandleBuildCache();
    void _SkipElementFromPointBuildCache();
    void _SkipGetFocusedElementBuildCache();
    void _SkipCreateTreeWalker();
    void _SkipControlViewWalker();
    void _SkipContentViewWalker();
    [PreserveSig] int get_RawViewWalker([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTreeWalker? walker);
    void _SkipRawViewCondition();
    void _SkipControlViewCondition();
    void _SkipContentViewCondition();
    void _SkipCreateCacheRequest();
    void _SkipCreateTrueCondition();
    void _SkipCreateFalseCondition();
    void _SkipCreatePropertyCondition();
    void _SkipCreatePropertyConditionEx();
    void _SkipCreateAndCondition();
    void _SkipCreateAndConditionFromArray();
    void _SkipCreateAndConditionFromNativeArray();
    void _SkipCreateOrCondition();
    void _SkipCreateOrConditionFromArray();
    void _SkipCreateOrConditionFromNativeArray();
    void _SkipCreateNotCondition();
    void _SkipAddAutomationEventHandler();
    void _SkipRemoveAutomationEventHandler();
    void _SkipAddPropertyChangedEventHandlerNativeArray();
    void _SkipAddPropertyChangedEventHandler();
    void _SkipRemovePropertyChangedEventHandler();
    void _SkipAddStructureChangedEventHandler();
    void _SkipRemoveStructureChangedEventHandler();
    void _SkipAddFocusChangedEventHandler();
    void _SkipRemoveFocusChangedEventHandler();
    void _SkipRemoveAllEventHandlers();
    void _SkipIntNativeArrayToSafeArray();
    void _SkipIntSafeArrayToNativeArray();
    void _SkipRectToVariant();
    void _SkipVariantToRect();
    void _SkipSafeArrayToRectNativeArray();
    void _SkipCreateProxyFactoryEntry();
    void _SkipProxyFactoryMapping();
    void _SkipGetPropertyProgrammaticName();
    void _SkipGetPatternProgrammaticName();
    void _SkipPollForPotentialSupportedPatterns();
    void _SkipPollForPotentialSupportedProperties();
    void _SkipCheckNotSupported();
    void _SkipReservedNotSupportedValue();
    void _SkipReservedMixedAttributeValue();
    void _SkipElementFromIAccessible();
    void _SkipElementFromIAccessibleBuildCache();
    void _SkipAutoSetFocusGet();
    void _SkipAutoSetFocusPut();
    [PreserveSig] int get_ConnectionTimeout(out uint milliseconds);
    [PreserveSig] int put_ConnectionTimeout(uint milliseconds);
    [PreserveSig] int get_TransactionTimeout(out uint milliseconds);
    [PreserveSig] int put_TransactionTimeout(uint milliseconds);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _SkipSetFocus();
    void _SkipGetRuntimeId();
    void _SkipFindFirst();
    void _SkipFindAll();
    void _SkipFindFirstBuildCache();
    void _SkipFindAllBuildCache();
    void _SkipBuildUpdatedCache();
    void _SkipGetCurrentPropertyValue();
    void _SkipGetCurrentPropertyValueEx();
    void _SkipGetCachedPropertyValue();
    void _SkipGetCachedPropertyValueEx();
    void _SkipGetCurrentPatternAs();
    void _SkipGetCachedPatternAs();
    void _SkipGetCurrentPattern();
    void _SkipGetCachedPattern();
    void _SkipGetCachedParent();
    void _SkipGetCachedChildren();
    void _SkipCurrentProcessId();
    [PreserveSig] int get_CurrentControlType(out int controlType);
}

[ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    void _SkipGetParentElement();
    [PreserveSig] int GetFirstChildElement(IUIAutomationElement element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? first);
    void _SkipGetLastChildElement();
    [PreserveSig] int GetNextSiblingElement(IUIAutomationElement element, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? next);
}
