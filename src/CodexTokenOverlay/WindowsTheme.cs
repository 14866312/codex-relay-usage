using Microsoft.Win32;

namespace CodexTokenOverlay;

internal enum OverlayThemeKind
{
    Dark,
    Light
}

internal sealed record OverlayThemePalette(
    Color Background,
    Color Label,
    Color Value,
    Color Accent,
    Color Border,
    Color Divider,
    Color ProgressTrack,
    Color ProgressStart,
    Color ProgressEnd,
    Color TargetHighlight)
{
    private static readonly OverlayThemePalette DarkPalette = new(
        Color.FromArgb(34, 36, 41),
        Color.FromArgb(157, 161, 170),
        Color.FromArgb(245, 245, 247),
        Color.FromArgb(185, 174, 255),
        Color.FromArgb(36, 255, 255, 255),
        Color.FromArgb(80, 84, 93),
        Color.FromArgb(70, 74, 83),
        Color.FromArgb(142, 126, 255),
        Color.FromArgb(181, 169, 255),
        Color.FromArgb(142, 126, 255));

    private static readonly OverlayThemePalette LightPalette = new(
        Color.FromArgb(252, 253, 255),
        Color.FromArgb(92, 96, 105),
        Color.FromArgb(28, 29, 33),
        Color.FromArgb(91, 72, 190),
        Color.FromArgb(32, 0, 0, 0),
        Color.FromArgb(208, 210, 216),
        Color.FromArgb(221, 222, 227),
        Color.FromArgb(111, 91, 218),
        Color.FromArgb(150, 132, 232),
        Color.FromArgb(111, 91, 218));

    public static OverlayThemePalette For(OverlayThemeKind kind) =>
        kind == OverlayThemeKind.Light ? LightPalette : DarkPalette;

    // A toolbar surface is quieter than the separate details card.
    public Color ToolbarSurface => IsLight ? Color.FromArgb(238, 244, 249) : Color.FromArgb(32, 33, 36);
    public Color ToolbarHover => IsLight ? Color.FromArgb(225, 233, 241) : Color.FromArgb(49, 50, 54);
    public Color ToolbarPressed => IsLight ? Color.FromArgb(208, 219, 231) : Color.FromArgb(61, 63, 68);
    public Color ToolbarOpen => IsLight ? Color.FromArgb(229, 237, 245) : Color.FromArgb(44, 46, 51);
    public Color ToolbarBorder => Color.FromArgb(IsLight ? 14 : 22, IsLight ? Color.Black : Color.White);
    public Color ToolbarActiveBorder => Color.FromArgb(IsLight ? 42 : 58, IsLight ? Color.Black : Color.White);
    public bool IsLight => Background.GetBrightness() > .5f;
    public Color InputSurface => IsLight ? Color.FromArgb(246, 248, 252) : Color.FromArgb(27, 29, 34);
    public Color Primary => IsLight ? Color.FromArgb(112, 96, 232) : Color.FromArgb(125, 107, 240);
    public Color PrimaryHover => IsLight ? Color.FromArgb(96, 79, 217) : Color.FromArgb(146, 128, 255);
    public Color PrimaryPressed => Color.FromArgb(83, 64, 196);
    public Color Danger => IsLight ? Color.FromArgb(212, 66, 80) : Color.FromArgb(255, 127, 138);
    public Color Warning => IsLight ? Color.FromArgb(180, 116, 24) : Color.FromArgb(238, 186, 88);
    public Color Success => IsLight ? Color.FromArgb(37, 161, 110) : Color.FromArgb(99, 210, 160);
    public Color ContextColor(double percent) => percent >= 95 ? Danger : percent >= 80 ? Warning : Accent;

    public Color ToolbarFill(OverlayFeedbackFrame frame) =>
        Blend(Blend(Blend(ToolbarSurface, ToolbarOpen, frame.Open), ToolbarHover, frame.Hover), ToolbarPressed, frame.Press);

    public Color ToolbarStroke(OverlayFeedbackFrame frame) =>
        Blend(ToolbarBorder, ToolbarActiveBorder, Math.Max(frame.Open * .7, Math.Max(frame.Hover, frame.Press)));

    private static Color Blend(Color from, Color to, double amount)
    {
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * Math.Clamp(amount, 0, 1));
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

internal interface IOverlayThemeSource : IDisposable
{
    OverlayThemeKind Current { get; }
    event EventHandler? Changed;
}

internal sealed class WindowsOverlayThemeSource : IOverlayThemeSource
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly object _gate = new();
    private readonly Func<object?> _readValue;
    private readonly Action<UserPreferenceChangedEventHandler> _unsubscribe;
    private readonly UserPreferenceChangedEventHandler _preferenceChangedHandler;
    private EventHandler? _changed;
    private OverlayThemeKind _current;
    private bool _subscribed;
    private bool _disposed;

    public WindowsOverlayThemeSource()
        : this(
            ReadRegistryValue,
            handler => SystemEvents.UserPreferenceChanged += handler,
            handler => SystemEvents.UserPreferenceChanged -= handler)
    {
    }

    internal WindowsOverlayThemeSource(
        Func<object?> readValue,
        Action<UserPreferenceChangedEventHandler> subscribe,
        Action<UserPreferenceChangedEventHandler> unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        _readValue = readValue;
        _unsubscribe = unsubscribe;
        _preferenceChangedHandler = HandleUserPreferenceChanged;
        _current = ReadKind(_readValue);
        try
        {
            subscribe(_preferenceChangedHandler);
            _subscribed = true;
        }
        catch
        {
            _subscribed = false;
        }
    }

    public OverlayThemeKind Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _changed += value;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public static OverlayThemeKind ResolveKind(object? value) => value switch
    {
        byte numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        sbyte numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        short numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        ushort numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        int numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        uint numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        long numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        ulong numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        _ => OverlayThemeKind.Dark
    };

    public static OverlayThemeKind ReadKind(Func<object?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        try
        {
            return ResolveKind(readValue());
        }
        catch
        {
            return OverlayThemeKind.Dark;
        }
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        var next = ReadKind(_readValue);
        EventHandler? changed;
        lock (_gate)
        {
            if (_disposed || next == _current)
            {
                return;
            }

            _current = next;
            changed = _changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        var shouldUnsubscribe = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shouldUnsubscribe = _subscribed;
            _subscribed = false;
            _changed = null;
        }

        if (!shouldUnsubscribe)
        {
            return;
        }

        try
        {
            _unsubscribe(_preferenceChangedHandler);
        }
        catch
        {
            // Theme observation is best-effort and must not terminate the overlay.
        }
    }

    private static object? ReadRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
        return key?.GetValue(AppsUseLightThemeValueName);
    }

    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Refresh();
    }
}

internal sealed class OverlayThemeBinding : IDisposable
{
    private readonly object _gate = new();
    private readonly Control _dispatcher;
    private readonly IOverlayThemeSource _source;
    private readonly Action<OverlayThemePalette> _apply;
    private readonly int _dispatcherThreadId;
    private OverlayThemeKind _desiredKind;
    private OverlayThemeKind? _lastAppliedKind;
    private bool _callbackPending;
    private bool _disposed;

    public OverlayThemeBinding(
        Control dispatcher,
        IOverlayThemeSource source,
        Action<OverlayThemePalette> apply)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(apply);

        _dispatcher = dispatcher;
        _source = source;
        _apply = apply;
        _dispatcherThreadId = Environment.CurrentManagedThreadId;
        _desiredKind = source.Current;
        _source.Changed += HandleThemeChanged;
        RequestApply(_desiredKind);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callbackPending = false;
        }

        _source.Changed -= HandleThemeChanged;
        _source.Dispose();
    }

    private void HandleThemeChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RequestApply(_source.Current);
    }

    private void RequestApply(OverlayThemeKind kind)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _desiredKind = kind;
            if (_callbackPending || _lastAppliedKind == kind)
            {
                return;
            }

            _callbackPending = true;
        }

        if (!_dispatcher.InvokeRequired
            && Environment.CurrentManagedThreadId == _dispatcherThreadId)
        {
            ApplyPending(requireHandle: false);
            return;
        }

        if (_dispatcher.IsDisposed
            || _dispatcher.Disposing
            || !_dispatcher.IsHandleCreated)
        {
            CancelPending();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke((Action)(() => ApplyPending(requireHandle: true)));
        }
        catch (ObjectDisposedException)
        {
            CancelPending();
        }
        catch (InvalidOperationException)
        {
            CancelPending();
        }
    }

    private void ApplyPending(bool requireHandle)
    {
        if (_dispatcher.IsDisposed
            || _dispatcher.Disposing
            || (requireHandle && !_dispatcher.IsHandleCreated))
        {
            CancelPending();
            return;
        }

        OverlayThemeKind kind;
        lock (_gate)
        {
            if (_disposed)
            {
                _callbackPending = false;
                return;
            }

            kind = _desiredKind;
            _callbackPending = false;
            if (_lastAppliedKind == kind)
            {
                return;
            }

            _lastAppliedKind = kind;
        }

        _apply(OverlayThemePalette.For(kind));
    }

    private void CancelPending()
    {
        lock (_gate)
        {
            _callbackPending = false;
        }
    }
}
