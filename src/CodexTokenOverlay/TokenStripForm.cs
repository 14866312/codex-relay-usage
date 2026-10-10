using System.Drawing.Drawing2D;

namespace CodexTokenOverlay;

internal enum OverlayEditGestureKind
{
    Move,
    Resize
}

internal sealed record OverlayEditPreviewEventArgs(
    OverlayEditGestureKind Kind,
    Point CursorScreen,
    Point FixedTopLeft,
    int ScalePercent);

internal readonly record struct OverlayRenderMetrics(
    double LabelFontPoints,
    double CompactValueFontPoints,
    double PanelHeaderFontPoints,
    double HighlightedValueFontPoints,
    int CapsuleRadius,
    int PanelRadius,
    int HorizontalPadding,
    int MetricGap,
    int DividerHeight,
    int PanelPadding,
    int HeaderHeight,
    int HighlightTopGap,
    int HighlightHeight,
    int ProgressTrackHeight,
    int ProgressVerticalGap,
    int CompactMetricGap,
    int EditHandleSize,
    int StrokeWidth)
{
    public static OverlayRenderMetrics Create(uint dpi, int scalePercent)
    {
        var effectiveDpi = dpi == 0 ? 96u : dpi;
        var sanitizedScale = ManualAttachmentRules.SanitizeScale(scalePercent);
        var userFactor = sanitizedScale / 100d;
        var pixelFactor = effectiveDpi / 96d * userFactor;
        int Scale(int dip) => (int)Math.Round(
            dip * pixelFactor,
            MidpointRounding.AwayFromZero);
        double ScaleFont(double points) => Math.Round(
            points * userFactor,
            2,
            MidpointRounding.AwayFromZero);

        return new OverlayRenderMetrics(
            ScaleFont(10.5d),
            ScaleFont(11d),
            ScaleFont(13d),
            ScaleFont(15d),
            Scale(8),
            Scale(14),
            Scale(10),
            Scale(8),
            Scale(14),
            Scale(14),
            Scale(22),
            Scale(6),
            Scale(44),
            Math.Max(1, Scale(4)),
            Scale(10),
            Scale(4),
            Math.Max(1, Scale(12)),
            Math.Max(1, Scale(1)));
    }
}

internal readonly record struct OverlayRenderDecorationState(
    bool ShowBorder,
    bool ShowDragHint,
    bool ShowResizeHandle,
    string DragHintText,
    IntRect DragHintBounds,
    double DragHintFontPoints);

internal sealed class TokenStripForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding
        | TextFormatFlags.SingleLine
        | TextFormatFlags.EndEllipsis
        | TextFormatFlags.VerticalCenter;

    private OverlayPresentation _presentation;
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Dark);
    private OverlayEditGestureKind? _editGesture;
    private Point _gestureStartCursorScreen;
    private Rectangle _gestureStartBounds;
    private Point _fixedTopLeft;
    private int _gestureStartScalePercent = ManualAttachmentRules.DefaultScalePercent;
    private OverlayEditPreviewEventArgs? _lastEditPreview;
    private readonly OverlayFeedback _feedback = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
    private readonly bool? _motionEnabled;
    private OverlayLayoutResult? _regionLayout;
    private double _regionReveal = -1;
    private bool _feedbackDisposed;
    private int _panelHover, _panelPressed;

    public TokenStripForm(bool? motionEnabled = null)
    {
        _motionEnabled = motionEnabled;
        _animationTimer.Tick += (_, _) => RefreshFeedback();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AccessibleName = "会话用量与费用";
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleDescription = "点击展开或收起当前会话的用量与费用明细";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _palette.Background;
        ForeColor = _palette.Value;
        Opacity = 1;
        DoubleBuffered = true;

        _presentation = OverlayPresentationBuilder.CreateWaiting(
            string.Empty,
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        ApplyLayout(new OverlayLayoutResult(
            OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields,
            ExpansionDirection.Down,
            96,
            new IntRect(0, 0, 196, 34),
            new IntRect(0, 0, 196, 34),
            default,
            0));
    }

    public event EventHandler? CapsuleClicked;
    public event EventHandler? CostDetailsRequested;
    public event EventHandler? IdentifyRequested;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditPreviewChanged;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditGestureCompleted;
    public event EventHandler? EditSaveRequested;
    public event EventHandler? EditCancelRequested;

    public OverlayLayoutResult? CurrentLayout { get; private set; }
    public bool IsEditMode { get; private set; }
    internal OverlayPresentation CurrentPresentation => _presentation;
    internal OverlayThemePalette CurrentThemePalette => _palette;
    internal OverlayFeedbackFrame FeedbackFrame => _feedback.Frame;
    internal bool IsPointerPressed => _feedback.PointerDown;
    internal bool IsFeedbackTimerRunning => _animationTimer.Enabled;
    internal Rectangle DetailsButtonBounds => PanelActionBounds(false);
    internal Rectangle PanelCloseBounds => PanelActionBounds(true);
    // Shown only while the visible page cannot be told apart from a same-title
    // conversation. One press re-checks and adopts the page Codex is following.
    internal Rectangle IdentifyButtonBounds => CapsuleIdentifyBounds();
    internal bool IsIdentifyHovered => _panelHover == 3;
    internal bool IsIdentifyPressed => _panelPressed == 3;
    private Rectangle CapsuleIdentifyBounds()
    {
        if (IsEditMode || CurrentLayout is null || CurrentLayout.CapsuleBounds.IsEmpty || !_presentation.ShowIdentifyAction) return Rectangle.Empty;
        var m = OverlayRenderMetrics.Create(CurrentLayout.Dpi, CurrentLayout.ScalePercent);
        var content = Rectangle.Inflate(CurrentLayout.CapsuleBounds.ToRectangle(), -m.HorizontalPadding, 0);
        content.Width = Math.Max(0, content.Width - Math.Max(5, m.EditHandleSize) - m.MetricGap);
        var height = Math.Max(1, content.Height - m.MetricGap);
        var width = Math.Min(content.Width, (m.HeaderHeight * 3) + m.MetricGap);
        return new Rectangle(content.Right - width, content.Top + ((content.Height - height) / 2), width, height);
    }
    private Rectangle PanelActionBounds(bool close)
    {
        if (IsEditMode || CurrentLayout is null || CurrentLayout.PanelBounds.IsEmpty) return Rectangle.Empty;
        var m = OverlayRenderMetrics.Create(CurrentLayout.Dpi, CurrentLayout.ScalePercent);
        var content = Rectangle.Inflate(CurrentLayout.PanelBounds.ToRectangle(), -m.PanelPadding, -m.PanelPadding);
        return close ? new(content.Right - m.HeaderHeight, content.Top, m.HeaderHeight, m.HeaderHeight)
            : new(content.Left, content.Bottom - m.HeaderHeight - m.ProgressVerticalGap, content.Width, m.HeaderHeight + m.ProgressVerticalGap);
    }
    private int PanelActionAt(Point p)
    {
        var identify = IdentifyButtonBounds;
        if (!identify.IsEmpty && identify.Contains(p)) return _presentation.IsIdentifying ? 0 : 3;
        return !RevealedPanelBounds.Contains(p) ? 0 : PanelCloseBounds.Contains(p) ? 2 : DetailsButtonBounds.Contains(p) ? 1 : 0;
    }

    internal int SetBoundsCoreCallCount { get; private set; }
    internal bool IsEditGestureActive => _editGesture is not null;
    internal OverlayRenderDecorationState RenderDecorations
    {
        get
        {
            if (!IsEditMode
                || CurrentLayout is null
                || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return new OverlayRenderDecorationState(
                    false,
                    false,
                    false,
                    string.Empty,
                    default,
                    0d);
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var content = Rectangle.Inflate(
                capsule,
                -metrics.HorizontalPadding,
                0);
            var hintRight = Math.Max(
                content.Left,
                content.Right - metrics.EditHandleSize - metrics.MetricGap);
            return new OverlayRenderDecorationState(
                true,
                true,
                true,
                "拖动调整位置",
                new IntRect(
                    content.Left,
                    content.Top,
                    hintRight - content.Left,
                    content.Height),
                metrics.LabelFontPoints);
        }
    }

    internal Rectangle EditResizeHandleBounds
    {
        get
        {
            if (CurrentLayout is null || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return Rectangle.Empty;
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var size = Math.Min(
                metrics.EditHandleSize,
                Math.Min(capsule.Width, capsule.Height));
            return new Rectangle(
                capsule.Right - size,
                capsule.Bottom - size,
                size,
                size);
        }
    }

    protected override bool ShowWithoutActivation => !IsEditMode;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            if (!IsEditMode)
            {
                parameters.ExStyle |= WsExNoActivate;
            }
            return parameters;
        }
    }

    public void SetPresentation(OverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (_presentation is not null && _presentation.ExpandedRows.SequenceEqual(presentation.ExpandedRows)
            && _presentation == presentation with { ExpandedRows = _presentation.ExpandedRows }) return;
        _presentation = presentation;
        Invalidate();
    }

    public void ApplyTheme(OverlayThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (_palette == palette)
        {
            return;
        }

        _palette = palette;
        BackColor = palette.Background;
        ForeColor = palette.Value;
        Invalidate();
    }

    public void ApplyLayout(OverlayLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (CurrentLayout == layout && Bounds == layout.WindowBounds.ToRectangle()) return;
        var translation = SameClientShape(CurrentLayout, layout);
        CurrentLayout = layout;
        SetBounds(
            layout.WindowBounds.X,
            layout.WindowBounds.Y,
            layout.WindowBounds.Width,
            layout.WindowBounds.Height,
            BoundsSpecified.All);
        if (translation)
        {
            // Moving a host preserves the client region and any hover/open animation.
            // Updating this key also avoids rebuilding the region on the next animation tick.
            if (SameClientShape(_regionLayout, layout)) _regionLayout = layout;
            return;
        }

        _feedback.SetExpanded(layout.State == OverlayVisualState.Expanded, Environment.TickCount64, CanAnimate);
        RefreshFeedback();
    }

    private static bool SameClientShape(OverlayLayoutResult? a, OverlayLayoutResult b) =>
        a is not null && a == b with { WindowBounds = new(a.WindowBounds.X, a.WindowBounds.Y, b.WindowBounds.Width, b.WindowBounds.Height) };

    private bool CanAnimate => Visible && !IsEditMode
        && (_motionEnabled ?? (SystemInformation.IsMenuAnimationEnabled && !SystemInformation.HighContrast));

    internal void FinishFeedbackAnimation()
    {
        _feedback.Finish();
        RefreshFeedback();
    }

    private void RefreshFeedback()
    {
        if (_feedbackDisposed) return;
        _feedback.Advance(Environment.TickCount64);
        if (!CanAnimate) _feedback.Finish();
        UpdateWindowRegion();
        Invalidate();
        if (_feedback.IsAnimating && CanAnimate) _animationTimer.Start();
        else _animationTimer.Stop();
    }

    private Rectangle RevealedPanelBounds
    {
        get
        {
            if (CurrentLayout is null || CurrentLayout.PanelBounds.IsEmpty || _feedback.Frame.PanelReveal <= 0) return Rectangle.Empty;
            var bounds = CurrentLayout.PanelBounds.ToRectangle();
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * _feedback.Frame.PanelReveal));
            return CurrentLayout.ExpansionDirection == ExpansionDirection.Down
                ? new(bounds.X, bounds.Y, bounds.Width, height)
                : new(bounds.X, bounds.Bottom - height, bounds.Width, height);
        }
    }

    private void UpdateWindowRegion()
    {
        var layout = CurrentLayout;
        if (layout is null || (_regionLayout == layout && _regionReveal == _feedback.Frame.PanelReveal)) return;
        _regionLayout = layout;
        _regionReveal = _feedback.Frame.PanelReveal;
        var metrics = OverlayRenderMetrics.Create(layout.Dpi, layout.ScalePercent);
        using var combined = new GraphicsPath();
        if (!layout.CapsuleBounds.IsEmpty)
        {
            using var capsule = CreateRoundedRectanglePath(
                layout.CapsuleBounds.ToRectangle(),
                metrics.CapsuleRadius);
            combined.AddPath(capsule, connect: false);
        }
        if (!RevealedPanelBounds.IsEmpty)
        {
            using var panel = CreateRoundedRectanglePath(
                RevealedPanelBounds,
                metrics.PanelRadius);
            combined.AddPath(panel, connect: false);
        }

        Region?.Dispose();
        Region = new Region(combined);
    }

    public void BeginEditMode(int scalePercent)
    {
        if (IsEditMode)
        {
            return;
        }
        if (CurrentLayout?.State != OverlayVisualState.Collapsed
            || !CurrentLayout.PanelBounds.IsEmpty)
        {
            throw new InvalidOperationException("编辑模式只能从收起布局开始。");
        }

        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(scalePercent);
        CancelPointerFeedback();
        IsEditMode = true;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        Activate();
        Focus();
        Invalidate();
    }

    public void EndEditMode()
    {
        if (!IsEditMode)
        {
            return;
        }

        CancelEditGesture();
        IsEditMode = false;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        Invalidate();
    }

    internal void SimulateCapsuleClick(Point screenPoint)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(screenPoint), screenPoint);
        HandleMouseUp(MouseButtons.Left, PointToClient(screenPoint), screenPoint);
    }

    internal void SimulateEditDrag(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateEditResize(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateEditGestureCompleted(Point currentScreen) =>
        HandleMouseUp(MouseButtons.Left, PointToClient(currentScreen), currentScreen);

    internal void SimulateEditCaptureLost()
    {
        Capture = false;
        if (_editGesture is not null)
        {
            OnMouseCaptureChanged(EventArgs.Empty);
        }
    }

    internal bool SimulateEditCommand(Keys keyData) => HandleEditCommand(keyData);

    public bool ContainsScreenPoint(Point screenPoint) =>
        CurrentLayout is not null && Region?.IsVisible(PointToClient(screenPoint)) == true;

    private bool IsCapsulePoint(Point clientPoint) => CurrentLayout is not null
        && CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y)
        && Region?.IsVisible(clientPoint) == true;

    protected override void SetBoundsCore(
        int x,
        int y,
        int width,
        int height,
        BoundsSpecified specified)
    {
        SetBoundsCoreCallCount++;
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate && !IsEditMode)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        if (message.Msg == WmNcHitTest && CurrentLayout is not null)
        {
            var packed = message.LParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            var clientPoint = PointToClient(screenPoint);
            if (Region?.IsVisible(clientPoint) != true)
            {
                message.Result = (IntPtr)HtTransparent;
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        HandleMouseDown(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        HandleMouseMove(eventArgs.Location, PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        HandleMouseUp(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (!IsEditMode)
        {
            _feedback.Move(false, Environment.TickCount64, CanAnimate);
            _panelHover = 0; Invalidate();
            Cursor = Cursors.Default;
            RefreshFeedback();
        }
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        base.OnVisibleChanged(eventArgs);
        if (!Visible && _feedback is not null) CancelPointerFeedback();
    }

    private void CancelPointerFeedback()
    {
        _panelHover = _panelPressed = 0;
        _feedback.CancelPointer();
        _feedback.Finish();
        if (!IsEditMode && Capture) Capture = false;
        Cursor = Cursors.Default;
        RefreshFeedback();
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture && !IsEditMode)
        {
            if (_feedback.PointerDown || _panelPressed != 0) CancelPointerFeedback();
            return;
        }
        if (Capture || _editGesture is null)
        {
            return;
        }

        if (_lastEditPreview is not null)
        {
            CompleteEditGesture(_lastEditPreview, releaseCapture: false);
        }
        else
        {
            CancelEditGesture(releaseCapture: false);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (IsEditMode && HandleEditCommand(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void HandleMouseDown(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (!IsEditMode)
        {
            if (_presentation.IsIdentifying && IdentifyButtonBounds.Contains(clientPoint)) return;
            if (button == MouseButtons.Left && PanelActionAt(clientPoint) is var action && action != 0)
            {
                _panelPressed = _panelHover = action; Capture = true; Invalidate(); return;
            }
            if (button == MouseButtons.Left && _feedback.Down(IsCapsulePoint(clientPoint), Environment.TickCount64, CanAnimate))
            {
                Capture = true;
                RefreshFeedback();
            }
            return;
        }
        if (button != MouseButtons.Left
            || CurrentLayout is null
            || !CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y))
        {
            return;
        }

        _editGesture = EditResizeHandleBounds.Contains(clientPoint)
            ? OverlayEditGestureKind.Resize
            : OverlayEditGestureKind.Move;
        _gestureStartCursorScreen = cursorScreen;
        _gestureStartBounds = Bounds;
        _fixedTopLeft = Location;
        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(
            CurrentLayout.ScalePercent);
        _lastEditPreview = null;
        Capture = true;
    }

    private void HandleMouseMove(Point clientPoint, Point cursorScreen)
    {
        if (!IsEditMode)
        {
            var inside = IsCapsulePoint(clientPoint);
            _feedback.Move(inside, Environment.TickCount64, CanAnimate);
            var action = PanelActionAt(clientPoint); if (action != _panelHover) { _panelHover = action; Invalidate(); }
            Cursor = inside || action != 0 ? Cursors.Hand : Cursors.Default;
            RefreshFeedback();
            return;
        }
        if (_editGesture is null)
        {
            Cursor = EditResizeHandleBounds.Contains(clientPoint) ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }

        var deltaX = cursorScreen.X - _gestureStartCursorScreen.X;
        var deltaY = cursorScreen.Y - _gestureStartCursorScreen.Y;
        var scalePercent = _gestureStartScalePercent;
        if (_editGesture == OverlayEditGestureKind.Move)
        {
            Location = new Point(
                _gestureStartBounds.X + deltaX,
                _gestureStartBounds.Y + deltaY);
        }
        else
        {
            scalePercent = ManualAttachmentCalculator.CalculateScale(
                _gestureStartBounds.Size,
                _gestureStartScalePercent,
                deltaX,
                deltaY);
        }

        _lastEditPreview = new OverlayEditPreviewEventArgs(
            _editGesture.Value,
            cursorScreen,
            _fixedTopLeft,
            scalePercent);
        EditPreviewChanged?.Invoke(this, _lastEditPreview);
    }

    private void HandleMouseUp(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (button != MouseButtons.Left)
        {
            return;
        }

        if (IsEditMode)
        {
            if (_editGesture is null)
            {
                return;
            }

            HandleMouseMove(clientPoint, cursorScreen);
            var completed = _lastEditPreview ?? new OverlayEditPreviewEventArgs(
                _editGesture.Value,
                cursorScreen,
                _fixedTopLeft,
                _gestureStartScalePercent);
            CompleteEditGesture(completed, releaseCapture: true);
            return;
        }

        if (_panelPressed != 0)
        {
            var action = _panelPressed; var invoke = action == PanelActionAt(clientPoint); _panelPressed = 0;
            if (Capture) Capture = false; Invalidate();
            if (invoke)
            {
                if (action == 1) CostDetailsRequested?.Invoke(this, EventArgs.Empty);
                else if (action == 3) IdentifyRequested?.Invoke(this, EventArgs.Empty);
                else CapsuleClicked?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        var clicked = _feedback.Up(IsCapsulePoint(clientPoint), Environment.TickCount64, CanAnimate);
        if (Capture) Capture = false;
        RefreshFeedback();
        if (clicked)
        {
            CapsuleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteEditGesture(
        OverlayEditPreviewEventArgs completed,
        bool releaseCapture)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
        EditGestureCompleted?.Invoke(this, completed);
    }

    private void CancelEditGesture(bool releaseCapture = true)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
    }

    private bool HandleEditCommand(Keys keyData)
    {
        if (!IsEditMode)
        {
            return false;
        }

        switch (keyData & Keys.KeyCode)
        {
            case Keys.Enter:
                EditSaveRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case Keys.Escape:
                EditCancelRequested?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (CurrentLayout is null)
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var metrics = OverlayRenderMetrics.Create(
            CurrentLayout.Dpi,
            CurrentLayout.ScalePercent);
        var decorations = RenderDecorations;
        using var labelFont = new Font(
            "Segoe UI",
            (float)(decorations.ShowDragHint
                ? decorations.DragHintFontPoints
                : metrics.LabelFontPoints),
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var compactValueFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.CompactValueFontPoints,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var panelHeaderFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.PanelHeaderFontPoints,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var highlightedValueFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.HighlightedValueFontPoints,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var backgroundBrush = new SolidBrush(_palette.Background);
        using var borderPen = new Pen(_palette.Border, metrics.StrokeWidth);
        using var dividerPen = new Pen(_palette.Divider, metrics.StrokeWidth);
        using var progressTrackBrush = new SolidBrush(_palette.ProgressTrack);

        DrawCapsule(
            eventArgs.Graphics,
            labelFont,
            compactValueFont,
            backgroundBrush,
            borderPen,
            dividerPen,
            metrics,
            decorations);
        if (!CurrentLayout.PanelBounds.IsEmpty)
        {
            DrawPanel(
                eventArgs.Graphics,
                labelFont,
                compactValueFont,
                panelHeaderFont,
                highlightedValueFont,
                backgroundBrush,
                borderPen,
                dividerPen,
                progressTrackBrush,
                metrics,
                decorations);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _feedbackDisposed = true;
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _feedback.CancelPointer();
            CancelEditGesture();
            Region?.Dispose();
            Region = null;
        }
        base.Dispose(disposing);
    }

    private void DrawCapsule(
        Graphics graphics,
        Font labelFont,
        Font valueFont,
        Brush backgroundBrush,
        Pen borderPen,
        Pen dividerPen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        var layout = CurrentLayout!;
        var bounds = layout.CapsuleBounds.ToRectangle();
        using var path = CreateRoundedRectanglePath(bounds, metrics.CapsuleRadius);
        using var surfaceBrush = new SolidBrush(_palette.ToolbarFill(_feedback.Frame));
        using var surfacePen = new Pen(_palette.ToolbarStroke(_feedback.Frame), metrics.StrokeWidth);
        graphics.FillPath(surfaceBrush, path);
        using var outline = CreateRoundedRectanglePath(Rectangle.Inflate(bounds, -metrics.StrokeWidth, -metrics.StrokeWidth), metrics.CapsuleRadius);
        graphics.DrawPath(decorations.ShowBorder ? borderPen : surfacePen, outline);

        if (decorations.ShowDragHint)
        {
            TextRenderer.DrawText(
                graphics,
                decorations.DragHintText,
                labelFont,
                decorations.DragHintBounds.ToRectangle(),
                _palette.Label,
                TextFlags | TextFormatFlags.HorizontalCenter);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        var padding = metrics.HorizontalPadding;
        var content = Rectangle.Inflate(bounds, -padding, 0);
        content.Offset(0, (int)Math.Round(metrics.StrokeWidth * _feedback.Frame.Press));
        var chevronSize = Math.Max(5, metrics.EditHandleSize);
        var chevronBounds = new Rectangle(content.Right - chevronSize, content.Y, chevronSize, content.Height);
        content.Width = Math.Max(0, content.Width - chevronSize - metrics.MetricGap);
        DrawChevron(graphics, chevronBounds, metrics);
        if (!string.IsNullOrWhiteSpace(_presentation.StatusText))
        {
            var identify = IdentifyButtonBounds;
            var statusBounds = identify.IsEmpty ? content : new Rectangle(
                content.Left, content.Top, Math.Max(0, identify.Left - metrics.MetricGap - content.Left), content.Height);
            TextRenderer.DrawText(
                graphics,
                _presentation.StatusText,
                labelFont,
                statusBounds,
                _palette.Label,
                identify.IsEmpty ? TextFlags | TextFormatFlags.HorizontalCenter : TextFlags);
            DrawIdentifyAction(graphics, labelFont, metrics);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        if (layout.CollapsedDisplay == CollapsedDisplayMode.PrimaryOnly)
        {
            DrawCompactMetric(graphics, _presentation.Primary, content, labelFont, valueFont, metrics);
            DrawIdentifyAction(graphics, labelFont, metrics);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        var pillText = OverlayPresentationBuilder.CompactText(_presentation.Primary) + " · " + OverlayPresentationBuilder.CompactText(_presentation.Secondary);
        // The identify action takes the right slot; the round/context text yields so the
        // two never draw on top of each other in the fixed-width capsule.
        var extra = IdentifyButtonBounds.IsEmpty ? _presentation.ExtraText ?? "" : "";
        var extraWidth = Math.Min(content.Width / 2, TextRenderer.MeasureText(graphics, extra, labelFont, Size.Empty, TextFormatFlags.NoPadding).Width + metrics.MetricGap);
        var pillBounds = new Rectangle(content.X + metrics.HeaderHeight, content.Y, content.Width - extraWidth - metrics.HeaderHeight, content.Height);
        using var iconPen = new Pen(_palette.Label, metrics.StrokeWidth);
        UiIcons.Draw(graphics, UiGlyph.Chart, new Rectangle(content.X, content.Y + (content.Height - metrics.DividerHeight) / 2, metrics.DividerHeight, metrics.DividerHeight), _palette.Label, _palette.Accent);
        TextRenderer.DrawText(graphics, pillText, labelFont, pillBounds, _palette.Value, TextFlags);
        TextRenderer.DrawText(graphics, extra, labelFont, new Rectangle(content.Right - extraWidth, content.Y, extraWidth, content.Height), _palette.Label, TextFlags | TextFormatFlags.Right);
        DrawIdentifyAction(graphics, labelFont, metrics);
        DrawEditHandle(graphics, dividerPen, metrics, decorations);
    }
    private void DrawIdentifyAction(Graphics graphics, Font font, OverlayRenderMetrics metrics)
    {
        var bounds = IdentifyButtonBounds;
        if (bounds.IsEmpty) return;
        var hovered = _panelHover == 3; var pressed = _panelPressed == 3 && hovered;
        if (hovered)
        {
            using var brush = new SolidBrush(pressed ? _palette.ToolbarPressed : _palette.ToolbarHover);
            using var path = UiDrawing.Round(bounds, metrics.CapsuleRadius);
            graphics.FillPath(brush, path);
        }
        if (pressed) bounds.Offset(0, metrics.StrokeWidth);
        TextRenderer.DrawText(graphics, _presentation.IsIdentifying ? "识别中…" : "自动识别", font, bounds, _palette.Accent, TextFlags | TextFormatFlags.HorizontalCenter);
    }

    private void DrawChevron(Graphics graphics, Rectangle bounds, OverlayRenderMetrics metrics)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
        graphics.RotateTransform((float)(_feedback.Frame.Open * 180));
        var halfWidth = Math.Max(2f, bounds.Width * .29f);
        using var pen = new Pen(_palette.Label, metrics.StrokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(pen, new[] { new PointF(-halfWidth, -halfWidth / 2), new PointF(0, halfWidth / 2), new PointF(halfWidth, -halfWidth / 2) });
        graphics.Restore(state);
    }

    private void DrawEditHandle(
        Graphics graphics,
        Pen pen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        if (!decorations.ShowResizeHandle || EditResizeHandleBounds.IsEmpty)
        {
            return;
        }

        var handle = EditResizeHandleBounds;
        var inset = metrics.StrokeWidth;
        var middle = Math.Max(inset, handle.Width / 2);
        graphics.DrawLine(
            pen,
            handle.Right - middle,
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - middle);
        graphics.DrawLine(
            pen,
            handle.Right - Math.Max(inset, handle.Width / 3),
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - Math.Max(inset, handle.Height / 3));
    }

    private void DrawPanel(
        Graphics graphics,
        Font labelFont,
        Font valueFont,
        Font headerFont,
        Font highlightedValueFont,
        Brush backgroundBrush,
        Pen borderPen,
        Pen dividerPen,
        Brush progressTrackBrush,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        var layout = CurrentLayout!;
        var bounds = layout.PanelBounds.ToRectangle();
        using var path = CreateRoundedRectanglePath(bounds, metrics.PanelRadius);
        graphics.FillPath(backgroundBrush, path);
        using var outline = CreateRoundedRectanglePath(Rectangle.Inflate(bounds, -metrics.StrokeWidth, -metrics.StrokeWidth), metrics.PanelRadius);
        graphics.DrawPath(borderPen, outline);

        var padding = metrics.PanelPadding;
        var content = Rectangle.Inflate(bounds, -padding, -padding);
        var rowHeight = layout.ExpandedRowHeight;
        var header = new Rectangle(content.Left, content.Top, content.Width - metrics.HeaderHeight - metrics.MetricGap, metrics.HeaderHeight);
        var total = _presentation.Total ?? _presentation.Primary;
        TextRenderer.DrawText(graphics, "Token 用量", labelFont, header, _palette.Label, TextFlags);
        var number = new Rectangle(content.Left, header.Bottom, content.Width, metrics.HeaderHeight + metrics.HighlightTopGap);
        TextRenderer.DrawText(graphics, total.Value, highlightedValueFont, number, _palette.Value, TextFlags);
        var rowsTop = number.Bottom + metrics.HighlightTopGap;
        graphics.DrawLine(dividerPen, content.Left, rowsTop, content.Right, rowsTop);
        rowsTop += metrics.HighlightTopGap;
        for (var index = 0; index < _presentation.ExpandedRows.Count; index++)
        {
            var rowBounds = new Rectangle(content.Left, rowsTop + index * rowHeight, content.Width, rowHeight);
            var metric = _presentation.ExpandedRows[index];
            if (metric.Field == DisplayField.Cost && metric.ExpandedLabel == "会话费用估算")
            {
                using var shade = new SolidBrush(_palette.InputSurface);
                using var box = UiDrawing.Round(Rectangle.Inflate(rowBounds, 4, -1), 5); graphics.FillPath(shade, box);
            }
            DrawExpandedRow(graphics, _presentation.ExpandedRows[index], rowBounds, labelFont, valueFont);
            if (metric.Field == DisplayField.ContextPercent && _presentation.ShowContextProgress)
            {
                var track = new Rectangle(rowBounds.Left, rowBounds.Bottom - metrics.ProgressTrackHeight, rowBounds.Width, metrics.ProgressTrackHeight);
                using var trackPath = UiDrawing.Round(track, metrics.ProgressTrackHeight / 2f); graphics.FillPath(progressTrackBrush, trackPath);
                var fill = track with { Width = (int)(track.Width * Math.Clamp(_presentation.ContextPercent / 100d, 0, 1)) };
                if (fill.Width > 0) { using var color = new SolidBrush(_palette.ContextColor(_presentation.ContextPercent)); using var fillPath = UiDrawing.Round(fill, metrics.ProgressTrackHeight / 2f); graphics.FillPath(color, fillPath); }
            }
        }
        DrawPanelAction(graphics, PanelCloseBounds, 2, labelFont, metrics);
        DrawPanelAction(graphics, DetailsButtonBounds, 1, labelFont, metrics);
    }
    private void DrawPanelAction(Graphics graphics, Rectangle bounds, int action, Font font, OverlayRenderMetrics m)
    {
        if (bounds.IsEmpty) return;
        var pressed = _panelPressed == action && _panelHover == action;
        if (_panelHover == action)
        {
            using var b = new SolidBrush(pressed ? _palette.ToolbarPressed : _palette.ToolbarHover);
            using var p = UiDrawing.Round(bounds, m.CapsuleRadius); graphics.FillPath(b, p);
        }
        if (pressed) bounds.Offset(0, m.StrokeWidth);
        if (action == 2) UiIcons.Draw(graphics, UiGlyph.Close, Rectangle.Inflate(bounds, -m.HighlightTopGap, -m.HighlightTopGap), _palette.Label, _palette.Label);
        else TextRenderer.DrawText(graphics, "查看费用明细  →", font, bounds, _palette.Accent, TextFlags | TextFormatFlags.HorizontalCenter);
    }

    private static void DrawDatabaseIcon(Graphics graphics, Rectangle bounds, Pen pen)
    {
        var height = Math.Max(2, bounds.Height / 3);
        graphics.DrawEllipse(pen, bounds.X, bounds.Y, bounds.Width, height);
        graphics.DrawLine(pen, bounds.X, bounds.Y + height / 2, bounds.X, bounds.Bottom - height / 2);
        graphics.DrawLine(pen, bounds.Right, bounds.Y + height / 2, bounds.Right, bounds.Bottom - height / 2);
        graphics.DrawArc(pen, bounds.X, bounds.Bottom - height, bounds.Width, height, 0, 180);
    }

    private void DrawCompactMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont,
        OverlayRenderMetrics metrics)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var labelWidth = TextRenderer.MeasureText(
            graphics,
            metric.CompactLabel,
            labelFont,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var gap = metrics.CompactMetricGap;
        labelWidth = Math.Min(labelWidth, bounds.Width);
        var labelBounds = new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height);
        var valueBounds = Rectangle.FromLTRB(
            Math.Min(bounds.Right, labelBounds.Right + gap),
            bounds.Top,
            bounds.Right,
            bounds.Bottom);
        TextRenderer.DrawText(graphics, metric.CompactLabel, labelFont, labelBounds, _palette.Label, TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            valueBounds,
            ValueColorFor(metric),
            TextFlags | TextFormatFlags.Right);
    }

    private void DrawHighlightedMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        var labelHeight = Math.Max(1, bounds.Height / 2);
        TextRenderer.DrawText(
            graphics,
            metric.ExpandedLabel,
            labelFont,
            new Rectangle(bounds.Left, bounds.Top, bounds.Width, labelHeight),
            _palette.Label,
            TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Left, bounds.Top + labelHeight, bounds.Width, bounds.Height - labelHeight),
            ValueColorFor(metric),
            TextFlags);
    }

    private void DrawExpandedRow(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        var labelWidth = Math.Max(0, bounds.Width * 56 / 100);
        TextRenderer.DrawText(
            graphics,
            metric.ExpandedLabel,
            labelFont,
            new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height),
            _palette.Label,
            TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Left + labelWidth, bounds.Top, bounds.Width - labelWidth, bounds.Height),
            ValueColorFor(metric),
            TextFlags | TextFormatFlags.Right);
    }

    private Color ValueColorFor(OverlayMetric metric) =>
        metric.Field is DisplayField.Context or DisplayField.ContextPercent
            ? _palette.ContextColor(_presentation.ContextPercent)
            : metric.Field is DisplayField.Cost
            ? _palette.Accent
            : _palette.Value;

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return path;
        }

        var clampedRadius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (clampedRadius == 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var diameter = clampedRadius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

}
