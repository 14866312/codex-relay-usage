using System.Text.Json;

namespace CodexTokenOverlay;

internal static class TitleBarPlacementTests
{
    internal static CodexWindowInfo Host(uint dpi, int widthDip = 1200,
        int topDip = 250, bool measuredCaption = false)
    {
        int Pixels(int dip) => (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);
        var bounds = new IntRect(Pixels(100), Pixels(topDip), Pixels(widthDip), Pixels(800));
        IntRect? caption = measuredCaption
            ? new(bounds.Right - Pixels(138), bounds.Top, Pixels(138), Pixels(32))
            : null;
        return new(new IntPtr(123), bounds, bounds, caption,
            new(0, 0, Pixels(2400), Pixels(1500)), dpi,
            new(Pixels(36), Pixels(22), Pixels(4), Pixels(4), Pixels(4)));
    }

    internal static void Run(Action<string, bool> check, string temporaryDirectory)
    {
        var defaults = OverlaySettings.CreateDefault();
        check("new installation uses automatic menu-bar position",
            !defaults.ManualPlacementEnabled && defaults.AnchorMode == AnchorMode.TitleBarTopRight);

        var oldJson = JsonSerializer.Serialize(new
        {
            SettingsVersion = 2, ThemeMode = "dark", SessionRoot = temporaryDirectory,
            PinnedThreadId = "synthetic-pin", AnchorMode = 3, VisibleFields = 261,
            CollapsedPrimaryField = 128, CollapsedSecondaryField = 512,
            ManualPlacementEnabled = true, OverlayScalePercent = 110,
            MainAttachment = new { ReferencePoint = 6, OffsetXDip = 0, OffsetYDip = -24 }
        });
        var migration = OverlaySettings.ParseJson(oldJson);
        check("old saved bottom position migrates to the menu bar", migration.MustPersist
            && !migration.Settings.ManualPlacementEnabled
            && migration.Settings.AnchorMode == AnchorMode.TitleBarTopRight);
        check("position migration preserves non-position preferences",
            migration.Settings.ThemeMode == "dark" && migration.Settings.SessionRoot == temporaryDirectory
            && migration.Settings.PinnedThreadId == "synthetic-pin"
            && migration.Settings.VisibleFields == (DisplayField)261
            && migration.Settings.CollapsedPrimaryField == DisplayField.Reasoning
            && migration.Settings.CollapsedSecondaryField == DisplayField.CacheHitRate
            && migration.Settings.OverlayScalePercent == 110);
        var oldPath = Path.Combine(temporaryDirectory, "old-placement-settings.json");
        File.WriteAllText(oldPath, oldJson);
        var upgraded = OverlaySettings.Load(oldPath);
        var reread = OverlaySettings.ParseJson(File.ReadAllText(oldPath));
        check("migration is saved once and is idempotent", !reread.MustPersist
            && reread.Settings.SettingsVersion == 3 && !upgraded.ManualPlacementEnabled);
        var legacy = OverlaySettings.ParseJson(JsonSerializer.Serialize(new
        {
            ThemeMode = "light", SessionRoot = temporaryDirectory,
            PinnedThreadId = "legacy-pin", ManualPlacementEnabled = true
        }));
        check("unversioned settings retain theme and session preferences during migration",
            legacy.MustPersist && !legacy.Settings.ManualPlacementEnabled
            && legacy.Settings.ThemeMode == "light"
            && legacy.Settings.SessionRoot == temporaryDirectory
            && legacy.Settings.PinnedThreadId == "legacy-pin");

        upgraded.ManualPlacementEnabled = true;
        upgraded.MainAttachment = new(AttachmentReferencePoint.TopLeft, 650, 20);
        var savedManual = OverlaySettings.ParseJson(upgraded.Serialize());
        check("new-version custom position survives restart", !savedManual.MustPersist
            && savedManual.Settings.ManualPlacementEnabled
            && savedManual.Settings.MainAttachment == upgraded.MainAttachment);
        upgraded.ResetToTitleBar();
        check("tray reset returns to the menu bar while preserving log and display choices",
            !upgraded.ManualPlacementEnabled && upgraded.AnchorMode == AnchorMode.TitleBarTopRight
            && upgraded.OverlayScalePercent == 100
            && upgraded.SessionRoot == temporaryDirectory
            && upgraded.PinnedThreadId == "synthetic-pin"
            && upgraded.CollapsedPrimaryField == DisplayField.Reasoning);

        foreach (var dpi in new uint[] { 96, 120, 144, 192 })
        foreach (var scale in new[] { 60, 100, 130 })
        foreach (var measured in new[] { false, true })
        {
            var host = Host(dpi, measuredCaption: measured);
            var collapsed = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.TitleBarTopRight, false, 5, true, ScalePercent: scale));
            var expanded = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.TitleBarTopRight, true, 5, true, ScalePercent: scale));
            var buttonsLeft = host.CaptionButtonBounds?.Left ?? host.WindowBounds.Right - (int)Math.Round(138 * dpi / 96d);
            var menuRight = host.WindowBounds.Left + (int)Math.Round(320 * dpi / 96d);
            check($"menu/control clearance at {dpi} DPI {scale}% measured={measured}",
                collapsed.State == OverlayVisualState.Collapsed
                && collapsed.WindowBounds.Left > menuRight && collapsed.WindowBounds.Right < buttonsLeft
                && collapsed.WindowBounds.Top >= host.WindowBounds.Top
                && collapsed.WindowBounds.Bottom <= host.WindowBounds.Top + (int)Math.Round(40 * dpi / 96d));
            check($"downward popup with stable capsule at {dpi} DPI {scale}% measured={measured}",
                expanded.State == OverlayVisualState.Expanded
                && expanded.ExpansionDirection == ExpansionDirection.Down
                && expanded.WindowBounds.Top + expanded.CapsuleBounds.Top == collapsed.WindowBounds.Top
                && expanded.WindowBounds.Left + expanded.CapsuleBounds.Left == collapsed.WindowBounds.Left
                && expanded.PanelBounds.Top > expanded.CapsuleBounds.Bottom
                && expanded.WindowBounds.Bottom <= host.WorkingArea.Bottom);
        }

        var medium = OverlayLayoutCalculator.Calculate(new(Host(96, widthDip: 850), AnchorMode.TitleBarTopRight, false, 5, true));
        check("narrow menu bar shrinks complete strip before dropping fields",
            medium.State == OverlayVisualState.Collapsed && medium.CollapsedDisplay == CollapsedDisplayMode.TwoFields
            && medium.ScalePercent < 100);
        var narrow = OverlayLayoutCalculator.Calculate(new(Host(96, widthDip: 640), AnchorMode.TitleBarTopRight, false, 5, true));
        check("very narrow menu bar uses primary field within blank area",
            narrow.State == OverlayVisualState.Collapsed && narrow.CollapsedDisplay == CollapsedDisplayMode.PrimaryOnly
            && narrow.WindowBounds.Left > narrow.WindowBounds.Right - 120);
        var tiny = OverlayLayoutCalculator.Calculate(new(Host(96, widthDip: 520), AnchorMode.TitleBarTopRight, false, 5, true));
        check("no usable menu space hides instead of covering controls", tiny.State == OverlayVisualState.HiddenForSpace);

        var movedHost = Host(144, topDip: 600);
        var topLayout = OverlayLayoutCalculator.Calculate(new(movedHost, AnchorMode.TitleBarTopRight, false, 5, true));
        var targets = new AttachmentTargetBounds(movedHost.Handle.ToInt64(), movedHost.WindowBounds, movedHost.WorkingArea, movedHost.Dpi);
        var original = new ManualPlacementSnapshot(false, ManualAttachmentRules.DefaultMainAttachment, 130);
        var editor = new ManualAttachmentCoordinator();
        var editing = editor.BeginEdit(original, targets, topLayout);
        var topCenter = new Point(topLayout.WindowBounds.Left + topLayout.CapsuleBounds.Width / 2,
            topLayout.WindowBounds.Top + topLayout.CapsuleBounds.Height / 2);
        check("manual editing starts at visible automatic position and size",
            editing.ResolvedCenter == topCenter && editing.Draft.ScalePercent == topLayout.ScalePercent);
        check("cancel editing restores automatic position and original scale", editor.Cancel().Draft == original);
        var manualTop = OverlayLayoutCalculator.Calculate(new(movedHost, AnchorMode.TitleBarTopRight, true, 5, true, topCenter));
        check("manually placed menu strip expands down even with space above",
            manualTop.State == OverlayVisualState.Expanded && manualTop.ExpansionDirection == ExpansionDirection.Down);
        var shiftedHost = movedHost with
        {
            WindowBounds = movedHost.WindowBounds with { X = movedHost.WindowBounds.X + 80, Y = movedHost.WindowBounds.Y - 120 },
            ExtendedFrameBounds = movedHost.ExtendedFrameBounds with { X = movedHost.ExtendedFrameBounds.X + 80, Y = movedHost.ExtendedFrameBounds.Y - 120 }
        };
        var shifted = OverlayLayoutCalculator.Calculate(new(shiftedHost, AnchorMode.TitleBarTopRight, false, 5, true));
        check("automatic menu strip follows host movement", shifted.WindowBounds.Left - topLayout.WindowBounds.Left == 80
            && shifted.WindowBounds.Top - topLayout.WindowBounds.Top == -120);
    }
}
