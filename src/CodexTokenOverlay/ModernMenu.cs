using System.Drawing.Drawing2D;

namespace CodexTokenOverlay;

internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly OverlayThemePalette _palette;
    public ModernMenuRenderer(OverlayThemePalette palette) { _palette = palette; RoundedEdges = false; }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_palette.Background); e.Graphics.FillRectangle(b, e.ToolStrip.ClientRectangle);
    }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = UiDrawing.Round(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), 9);
        using var pen = new Pen(_palette.Border); e.Graphics.DrawPath(pen, p);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = UiDrawing.Round(new RectangleF(5, 1, e.Item.Width - 10, e.Item.Height - 2), 6);
        using var b = new SolidBrush(_palette.ToolbarHover); e.Graphics.FillPath(b, p);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? _palette.Label : Equals(e.Item.Tag, UiGlyph.Exit) ? _palette.Danger : _palette.Value;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_palette.Divider); e.Graphics.DrawLine(pen, 13, e.Item.Height / 2, e.Item.Width - 13, e.Item.Height / 2);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = _palette.Label; base.OnRenderArrow(e); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        UiIcons.Draw(e.Graphics, UiGlyph.Check, e.ImageRectangle, _palette.Accent, _palette.Accent);
    }
    internal static void Apply(ContextMenuStrip menu, OverlayThemePalette palette)
    {
        menu.Renderer = new ModernMenuRenderer(palette);
        ApplyItems(menu, palette);
    }
    private static void ApplyItems(ToolStrip strip, OverlayThemePalette palette)
    {
        strip.BackColor = palette.Background; strip.ForeColor = palette.Value; strip.Padding = new(4, 6, 4, 6);
        strip.ImageScalingSize = new(18, 18);
        if (strip is ToolStripDropDown drop)
        {
            drop.Renderer = new ModernMenuRenderer(palette); drop.SizeChanged -= RoundMenu; drop.SizeChanged += RoundMenu; RoundMenu(drop, EventArgs.Empty);
        }
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = palette.Value;
            item.Padding = item is ToolStripSeparator ? new(0, 3, 0, 3) : new(4, 7, 10, 7);
            if (item.Tag is UiGlyph glyph) { var old = item.Image; item.Image = UiIcons.Image(glyph, palette, 18); old?.Dispose(); }
            if (item is ToolStripMenuItem parent && parent.HasDropDownItems) ApplyItems(parent.DropDown, palette);
        }
    }
    private static void RoundMenu(object? sender, EventArgs e)
    {
        if (sender is not ToolStripDropDown menu || menu.Width < 2 || menu.Height < 2) return;
        using var p = UiDrawing.Round(new RectangleF(0, 0, menu.Width, menu.Height), 10); var old = menu.Region; menu.Region = new(p); old?.Dispose();
    }
    internal static void DisposeImages(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items) { item.Image?.Dispose(); item.Image = null; if (item is ToolStripMenuItem parent && parent.HasDropDownItems) DisposeImages(parent.DropDown); }
    }
}
