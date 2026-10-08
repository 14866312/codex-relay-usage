using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal interface IThemedControl { void ApplyTheme(OverlayThemePalette palette); }
internal enum ButtonKind { Primary, Secondary, Ghost, Danger }

internal sealed class ModernButton : Button, IThemedControl
{
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Light);
    private bool _hover, _pressed;
    [DefaultValue(ButtonKind.Primary)] public ButtonKind Kind { get; set; }
    [DefaultValue(null)] public UiGlyph? Glyph { get; set; }
    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        Height = 36; Cursor = Cursors.Hand; Margin = new(4);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public void ApplyTheme(OverlayThemePalette palette) { _palette = palette; BackColor = palette.Background; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _pressed = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); _pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? _palette.Background);
        var scale = DeviceDpi / 96f; var r = new RectangleF(1, 1, Width - 3, Height - 3);
        var fill = Kind == ButtonKind.Primary ? _palette.Primary : Kind == ButtonKind.Ghost ? _palette.Background : _palette.InputSurface;
        if (_hover) fill = Kind == ButtonKind.Primary ? _palette.PrimaryHover : _palette.ToolbarHover;
        if (_pressed && (_hover || !Capture)) fill = Kind == ButtonKind.Primary ? _palette.PrimaryPressed : _palette.ToolbarPressed;
        if (!Enabled) fill = _palette.ProgressTrack;
        var text = !Enabled ? _palette.Label : Kind == ButtonKind.Primary ? Color.White : Kind == ButtonKind.Danger ? _palette.Danger : _palette.Value;
        using var p = UiDrawing.Round(r, 7 * scale); using var b = new SolidBrush(fill); g.FillPath(b, p);
        if (Kind is ButtonKind.Secondary or ButtonKind.Danger) { using var pen = new Pen(_palette.Border); g.DrawPath(pen, p); }
        if (Focused && ShowFocusCues) { using var pen = new Pen(_palette.Accent, 1.6f * scale); g.DrawPath(pen, p); }
        var bounds = Rectangle.Inflate(ClientRectangle, -(int)(9 * scale), 0); if (_pressed && (_hover || !Capture)) bounds.Offset(0, 1);
        if (Glyph is { } glyph)
        {
            var size = (int)(16 * scale); var x = string.IsNullOrEmpty(Text) ? (Width - size) / 2 : bounds.Left;
            UiIcons.Draw(g, glyph, new(x, bounds.Top + (bounds.Height - size) / 2, size, size), text, text);
            if (!string.IsNullOrEmpty(Text)) { bounds.X += size + (int)(6 * scale); bounds.Width -= size + (int)(6 * scale); }
        }
        TextRenderer.DrawText(g, Text, Font, bounds, text, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class ModernToggle : CheckBox, IThemedControl
{
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Light);
    private bool _hover;
    public ModernToggle()
    {
        AutoSize = false; Height = 36; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public void ApplyTheme(OverlayThemePalette palette) { _palette = palette; BackColor = palette.Background; ForeColor = palette.Value; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
        var scale = DeviceDpi / 96f; var track = new RectangleF(0, (Height - 20 * scale) / 2, 36 * scale, 20 * scale);
        var color = !Enabled ? _palette.ProgressTrack : Checked ? (_hover ? _palette.PrimaryHover : _palette.Primary) : _palette.Border;
        using var path = UiDrawing.Round(track, 10 * scale); using var fill = new SolidBrush(color); g.FillPath(fill, path);
        var size = 14 * scale; var x = Checked ? track.Right - size - 3 * scale : track.Left + 3 * scale;
        using var knob = new SolidBrush(Color.White); g.FillEllipse(knob, x, track.Top + 3 * scale, size, size);
        var textBounds = new Rectangle((int)(44 * scale), 0, Math.Max(0, Width - (int)(44 * scale)), Height);
        TextRenderer.DrawText(g, Text, Font, textBounds, Enabled ? _palette.Value : _palette.Label, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1), _palette.Accent, BackColor);
    }
}

// Retains real TextBox/ComboBox controls for editing, accessibility and native IME support.
internal sealed class FieldFrame : Panel, IThemedControl
{
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Light);
    public Control Editor { get; }
    [DefaultValue(false)] public bool Invalid { get; set; }
    public FieldFrame(Control editor, int height = 38)
    {
        Editor = editor; Dock = DockStyle.Fill; Height = height; MinimumSize = new(0, height); Margin = new(0, 3, 0, 7);
        Padding = new(10, 7, 10, 6); DoubleBuffered = true;
        if (editor is TextBox text) text.BorderStyle = BorderStyle.None;
        if (editor is ComboBox combo) { combo.FlatStyle = FlatStyle.Flat; combo.DrawMode = DrawMode.OwnerDrawFixed; combo.DrawItem += DrawComboItem; }
        editor.Dock = DockStyle.Fill; editor.Margin = Padding.Empty; Controls.Add(editor);
        editor.Enter += (_, _) => Invalidate(); editor.Leave += (_, _) => Invalidate(); editor.EnabledChanged += (_, _) => { ApplyTheme(_palette); Invalidate(); };
    }
    public void ApplyTheme(OverlayThemePalette palette)
    {
        _palette = palette; BackColor = palette.Background;
        Editor.BackColor = Editor.Enabled ? palette.InputSurface : palette.Background; Editor.ForeColor = Editor.Enabled ? palette.Value : palette.Label; Invalidate();
    }
    private void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return; var selected = e.State.HasFlag(DrawItemState.Selected);
        using var b = new SolidBrush(selected ? _palette.ToolbarHover : _palette.InputSurface); e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, ((ComboBox)Editor).GetItemText(((ComboBox)Editor).Items[e.Index]), e.Font, e.Bounds, _palette.Value, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = UiDrawing.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), 7 * DeviceDpi / 96f);
        using var b = new SolidBrush(Editor.BackColor); g.FillPath(b, p);
        using var pen = new Pen(Invalid ? _palette.Danger : Editor.Focused ? _palette.Accent : _palette.Border, Editor.Focused || Invalid ? 1.5f : 1); g.DrawPath(pen, p);
    }
}

internal class ModernDialog : Form
{
    private readonly Panel _header;
    private readonly Label _title;
    private readonly ModernButton _close;
    protected OverlayThemePalette Palette { get; private set; } = OverlayThemePalette.For(OverlayThemeKind.Light);
    protected Panel Body { get; }
    public ModernDialog(string title)
    {
        Text = title + " · " + AppBrand.Name; Icon = AppBrand.Icon; AccessibleName = title;
        Font = new Font("Microsoft YaHei UI", 9f); AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; Padding = new(1); DoubleBuffered = true;
        _header = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new(22, 12, 14, 8) };
        _title = new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
        _close = new ModernButton { Text = "", Glyph = UiGlyph.Close, Kind = ButtonKind.Ghost, Dock = DockStyle.Right, Width = 34, AccessibleName = "关闭" };
        _close.Click += (_, _) => Close();
        _header.Controls.Add(_title); _header.Controls.Add(_close);
        _header.MouseDown += DragHeader; _title.MouseDown += DragHeader;
        Body = new Panel { Dock = DockStyle.Fill, Padding = new(22, 0, 22, 20) };
        Controls.Add(Body); Controls.Add(_header); ApplyTheme(Palette);
    }
    public void ApplyTheme(OverlayThemePalette palette)
    {
        Palette = palette; ThemeControls(this, palette); _title.ForeColor = palette.Accent; Invalidate();
        if (IsHandleCreated && TopLevel)
        {
            var dark = palette.IsLight ? 0 : 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            var corner = 2; DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
        }
    }
    internal static void ThemeControls(Control root, OverlayThemePalette palette)
    {
        root.BackColor = palette.Background; root.ForeColor = palette.Value;
        if (root is IThemedControl themed) { themed.ApplyTheme(palette); return; }
        if (root is Label l) l.ForeColor = Equals(l.Tag, "accent") ? palette.Accent : Equals(l.Tag, "muted") ? palette.Label : Equals(l.Tag, "danger") ? palette.Danger : palette.Value;
        if (root is TextBox text) { text.BackColor = Equals(text.Tag, "notes") ? palette.Background : palette.InputSurface; text.ForeColor = Equals(text.Tag, "notes") ? palette.Label : palette.Value; }
        if (root is DataGridView grid) StyleGrid(grid, palette);
        if (root is ListBox or ListView) { root.BackColor = palette.InputSurface; root.ForeColor = palette.Value; }
        foreach (Control c in root.Controls) ThemeControls(c, palette);
    }
    internal static void StyleGrid(DataGridView grid, OverlayThemePalette palette)
    {
        grid.BackgroundColor = palette.Background; grid.GridColor = palette.Divider; grid.EnableHeadersVisualStyles = false;
        grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = palette.InputSurface, ForeColor = palette.Label, Font = grid.Font, Padding = new(8), Alignment = DataGridViewContentAlignment.MiddleLeft };
        grid.DefaultCellStyle = new() { BackColor = palette.Background, ForeColor = palette.Value, SelectionBackColor = palette.ToolbarOpen, SelectionForeColor = palette.Value, Padding = new(8, 3, 8, 3) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = palette.Background;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = (int)(40 * grid.DeviceDpi / 96d);
        grid.RowTemplate.Height = (int)(40 * grid.DeviceDpi / 96d);
    }
    private void DragHeader(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !TopLevel) return; ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyTheme(Palette); }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (TopLevel) FitToWorkingArea(Screen.FromHandle(Handle).WorkingArea);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (TopLevel) FitToWorkingArea(Screen.FromHandle(Handle).WorkingArea);
    }
    internal void FitToWorkingArea(Rectangle workingArea)
    {
        if (workingArea.Width < 2 || workingArea.Height < 2) return;
        var margin = Math.Min(16 * DeviceDpi / 96, Math.Min(workingArea.Width, workingArea.Height) / 4);
        var available = Rectangle.Inflate(workingArea, -margin, -margin);
        // High DPI can make the preferred minimum larger than a laptop screen. Keep
        // the action row visible; the price editor scrolls independently above it.
        MinimumSize = new(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
        var width = Math.Min(Width, available.Width); var height = Math.Min(Height, available.Height);
        var bounds = new Rectangle(Math.Clamp(Left, available.Left, available.Right - width),
            Math.Clamp(Top, available.Top, available.Bottom - height), width, height);
        if (Bounds != bounds) Bounds = bounds;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = UiDrawing.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), 12 * DeviceDpi / 96f);
        using var pen = new Pen(Palette.Border); e.Graphics.DrawPath(pen, p);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e); if (Width < 2 || Height < 2) return;
        using var p = UiDrawing.Round(new RectangleF(0, 0, Width, Height), 12 * DeviceDpi / 96f);
        var old = Region; Region = new Region(p); old?.Dispose();
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 && TopLevel && WindowState == FormWindowState.Normal)
        {
            var packed = m.LParam.ToInt64(); var point = PointToClient(new(unchecked((short)packed), unchecked((short)(packed >> 16))));
            var grip = Math.Max(6, 6 * DeviceDpi / 96);
            var left = point.X < grip; var right = point.X >= ClientSize.Width - grip; var top = point.Y < grip; var bottom = point.Y >= ClientSize.Height - grip;
            var hit = top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 0;
            if (hit != 0) { m.Result = new(hit); return; }
        }
        base.WndProc(ref m);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
