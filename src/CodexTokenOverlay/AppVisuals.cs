using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal static class AppBrand
{
    public const string Name = "Codex 用量助手";
    public const string EnglishName = "Codex Usage Assistant";
    // Keep the binary/configuration identity so an upgrade retains prices and login shortcuts.
    public static Icon Icon { get; } = LoadIcon();
    private static Icon LoadIcon()
    {
        using var stream = typeof(AppBrand).Assembly.GetManifestResourceStream("CodexTokenOverlay.Assets.app.ico");
        return stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream, 64, 64);
    }
    public static Icon TrayIcon(OverlayThemePalette palette)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap)) UiIcons.Draw(g, UiGlyph.Chart, new(2, 2, 28, 28), palette.Value, palette.Accent);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

internal enum UiGlyph { Chart, Price, Cost, Palette, Position, Rows, Fields, Data, Hide, Startup, Exit, Check, Close, Add, Delete, Search, Edit, Folder, Link }
internal static class UiIcons
{
    public static Bitmap Image(UiGlyph glyph, OverlayThemePalette palette, int size = 20)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        Draw(g, glyph, new(1, 1, size - 2, size - 2), glyph == UiGlyph.Exit ? palette.Danger : palette.Accent, palette.Accent);
        return bitmap;
    }
    public static void Draw(Graphics g, UiGlyph glyph, Rectangle bounds, Color color, Color accent)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.X, bounds.Y); g.ScaleTransform(bounds.Width / 20f, bounds.Height / 20f);
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(accent);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, x1, y1, x2, y2);
        switch (glyph)
        {
            case UiGlyph.Chart:
                using (var b = new SolidBrush(color))
                {
                    using var a = UiDrawing.Round(new RectangleF(2, 12, 3.4f, 6), 1.5f); g.FillPath(b, a);
                    using var c = UiDrawing.Round(new RectangleF(8, 8, 3.4f, 10), 1.5f); g.FillPath(b, c);
                    using var d = UiDrawing.Round(new RectangleF(14, 4, 3.4f, 14), 1.5f); g.FillPath(brush, d);
                }
                g.FillEllipse(brush, 14, .5f, 3.4f, 3.4f); break;
            case UiGlyph.Close: Line(5, 5, 15, 15); Line(15, 5, 5, 15); break;
            case UiGlyph.Check: g.DrawLines(pen, [new(4, 10), new(8, 14), new(16, 5)]); break;
            case UiGlyph.Add: Line(10, 4, 10, 16); Line(4, 10, 16, 10); break;
            case UiGlyph.Search: g.DrawEllipse(pen, 3, 3, 10, 10); Line(12, 12, 17, 17); break;
            case UiGlyph.Price: case UiGlyph.Cost:
                using (var p = UiDrawing.Round(new RectangleF(3, 2, 14, 16), 3)) g.DrawPath(pen, p);
                Line(10, 5, 10, 15); g.DrawArc(pen, 7, 6, 6, 4, 90, 270); g.DrawArc(pen, 7, 10, 6, 4, 270, 270); break;
            case UiGlyph.Data: case UiGlyph.Folder:
                using (var p = UiDrawing.Round(new RectangleF(2, 5, 16, 12), 2)) g.DrawPath(pen, p);
                Line(2, 5, 2, 3); Line(2, 3, 8, 3); Line(8, 3, 10, 5); break;
            case UiGlyph.Position: g.DrawRectangle(pen, 4, 4, 12, 12); Line(1, 10, 6, 10); Line(14, 10, 19, 10); Line(10, 1, 10, 6); Line(10, 14, 10, 19); break;
            case UiGlyph.Rows: case UiGlyph.Fields:
                for (var i = 0; i < 3; i++) { g.DrawRectangle(pen, 3, 3 + i * 5, 2, 2); Line(9, 4 + i * 5, 17, 4 + i * 5); } break;
            case UiGlyph.Startup: g.DrawArc(pen, 3, 3, 14, 14, -50, 280); Line(10, 1, 10, 9); break;
            case UiGlyph.Hide: g.DrawEllipse(pen, 2, 5, 16, 10); g.DrawEllipse(pen, 7, 7, 6, 6); Line(3, 17, 17, 3); break;
            case UiGlyph.Exit: Line(10, 3, 4, 3); Line(4, 3, 4, 17); Line(4, 17, 10, 17); Line(9, 10, 18, 10); Line(14, 6, 18, 10); Line(14, 14, 18, 10); break;
            case UiGlyph.Delete: Line(3, 5, 17, 5); Line(7, 2, 13, 2); g.DrawRectangle(pen, 5, 5, 10, 12); Line(8, 8, 8, 14); Line(12, 8, 12, 14); break;
            case UiGlyph.Edit: g.DrawLines(pen, [new(3, 17), new(4, 12), new(14, 2), new(18, 6), new(8, 16), new(3, 17)]); Line(12, 4, 16, 8); break;
            case UiGlyph.Link: g.DrawArc(pen, 2, 8, 9, 9, 45, 270); g.DrawArc(pen, 9, 2, 9, 9, 225, 270); Line(7, 13, 13, 7); break;
            default: g.DrawEllipse(pen, 3, 3, 14, 14); g.FillEllipse(brush, 8, 8, 4, 4); break;
        }
        g.Restore(state);
    }
}

internal static class UiDrawing
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return p;
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }
}
