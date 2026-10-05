using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal enum HostWindowEvent { Location, Foreground, MoveStarted, MoveEnded, Visibility, Destroyed }

// Out-of-context callbacks run on the installing UI thread's message loop. No DLL injection,
// UI automation, process enumeration or log work is done on the movement path.
internal sealed class NativeWindowEvents : IDisposable
{
    private readonly Action<HostWindowEvent> _changed;
    private readonly WinEventCallback _callback;
    private readonly List<IntPtr> _targetHooks = [];
    private readonly uint _flags;
    private readonly IntPtr _foregroundHook;
    private GCHandle _callbackRoot;
    private readonly Queue<HostWindowEvent> _pending = new();
    private bool _dispatching;
    private IntPtr _host;
    private uint _processId;
    private bool _disposed;
    public bool HasLocationHook { get; private set; }
    public string? LastError { get; private set; }

    public NativeWindowEvents(Action<HostWindowEvent> changed, bool skipOwnProcess = true)
    {
        _changed = changed; _callback = OnEvent;
        _callbackRoot = GCHandle.Alloc(_callback);
        _flags = skipOwnProcess ? 2u : 0u; // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, _flags);
    }

    public void Watch(IntPtr host, uint processId)
    {
        if (_disposed || (_host == host && _processId == processId)) return;
        ClearTargetHooks(); _host = host; _processId = processId;
        if (host == IntPtr.Zero || processId == 0) return;
        HasLocationHook = AddHook(0x800B, 0x800B); // EVENT_OBJECT_LOCATIONCHANGE
        AddHook(0x8001, 0x8003); // destroy/show/hide
        AddHook(0x000A, 0x000B); // move/size start/end
        AddHook(0x0016, 0x0017); // minimize start/end
    }

    private bool AddHook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, IntPtr.Zero, _callback, _processId, 0, _flags);
        if (hook == IntPtr.Zero) return false;
        _targetHooks.Add(hook); return true;
    }

    private void OnEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed) return;
        if (kind == 3) { Dispatch(HostWindowEvent.Foreground); return; }
        if (window != _host || objectId != 0 || childId != 0) return;
        var change = kind switch
        {
            0x800B => HostWindowEvent.Location, 0x8001 => HostWindowEvent.Destroyed,
            0x000A => HostWindowEvent.MoveStarted, 0x000B => HostWindowEvent.MoveEnded,
            _ => HostWindowEvent.Visibility
        };
        Dispatch(change);
    }

    private void Dispatch(HostWindowEvent change)
    {
        _pending.Enqueue(change);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (!_disposed && _pending.TryDequeue(out var next))
            {
                try { _changed(next); }
                catch (Exception error) { LastError = error.GetType().Name; }
                // Exceptions must not cross the unmanaged callback boundary. The ordinary
                // locator timer can recover if a transient UI operation failed.
            }
        }
        finally { _dispatching = false; }
    }

    private void ClearTargetHooks()
    {
        foreach (var hook in _targetHooks) UnhookWinEvent(hook);
        _targetHooks.Clear(); HasLocationHook = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ClearTargetHooks();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _pending.Clear();
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventCallback(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
