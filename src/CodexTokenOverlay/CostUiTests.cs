using System.Drawing.Imaging;

namespace CodexTokenOverlay;

internal static class CostUiTests
{
    internal static DiagnosticsRunner.SelfTestResult Run(string? previewDirectory = null)
    {
        var results = new List<DiagnosticsRunner.TestResult>();
        void Check(string name, bool ok) => results.Add(new(name, ok, ok ? null : "assertion failed"));
        T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
        var directory = Path.Combine(Path.GetTempPath(), "CodexRelayUsage-price-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pricePath = Path.Combine(directory, "prices.json");
            using var editor = new ModelPriceForm(PricingSettings.Empty, new[] { "relay/test" }, pricePath);
            using var editorHost = ShowOffscreen(editor);
            Check("new model rates start blank", editor.TryBuildSettings(out var blank, out _) && blank.ForModel("relay/test")!.Low == new TokenPrices());
            Check("default tier threshold 272000 editable", Find<TextBox>(editor, "Threshold").Text == "272000");
            Check("price editor low input field has usable bounds", Find<TextBox>(editor, "Low0").Width >= 100 && Find<TextBox>(editor, "Low0").Height >= 20);
            var originalBounds = editor.Bounds; var originalMinimum = editor.MinimumSize;
            var laptop = new Rectangle(-1024, 0, 1024, 700);
            editor.FitToWorkingArea(laptop); editor.PerformLayout();
            Check("small display constrains price window including negative monitor origin", laptop.Contains(editor.Bounds));
            var saveButton = Find<Button>(editor, "SavePrices");
            var saveBounds = editor.RectangleToClient(saveButton.RectangleToScreen(saveButton.ClientRectangle));
            Check("small display keeps price save action visible and usable", editor.ClientRectangle.Contains(saveBounds) && saveButton.Enabled && saveBounds.Height >= 30);
            editor.MinimumSize = originalMinimum; editor.Bounds = originalBounds; editor.PerformLayout();
            var aliases = Find<TextBox>(editor, "ModelAliases"); aliases.Text = "relay/test" + Environment.NewLine + "relay/alias";
            Find<TextBox>(editor, "Low0").Text = "2"; Find<TextBox>(editor, "Low1").Text = "0.1";
            Find<TextBox>(editor, "Low2").Text = "0"; Find<TextBox>(editor, "Low3").Text = "10";
            Find<TextBox>(editor, "Multiplier").Text = "0.35";
            Find<CheckBox>(editor, "TwoTiers").Checked = false;
            Check("price form retains exact aliases and decimal prices", editor.TryBuildSettings(out var configured, out _) &&
                configured.ForModel("relay/alias")!.Low == new TokenPrices(2m, .1m, 0m, 10m) && configured.Profiles.Single().Multiplier == .35m);
            var highBox = Find<TextBox>(editor, "High0");
            Check("disabled tiers cannot edit high prices", !highBox.Enabled && !Find<TextBox>(editor, "Threshold").Enabled);
            Find<CheckBox>(editor, "TwoTiers").Checked = true; highBox.Text = "4";
            Check("tier toggle restores independent high prices", highBox.Enabled && editor.TryBuildSettings(out var tiers, out _) && tiers.Profiles.Single().High.Input == 4m);
            Find<TextBox>(editor, "Low0").Text = "-1";
            Check("UI rejects negative price", !editor.TryBuildSettings(out _, out _));
            Find<TextBox>(editor, "Low0").Text = "abc";
            Check("UI rejects nonnumeric price", !editor.TryBuildSettings(out _, out _));
            Find<TextBox>(editor, "Low0").Text = "2";
            Find<TextBox>(editor, "Low2").Text = "";
            Check("UI distinguishes zero and blank", editor.TryBuildSettings(out var missing, out _) && missing.Profiles.Single().Low.CacheWrite is null);
            var mode = Find<ComboBox>(editor, "PriceMode"); mode.SelectedIndex = 1;
            Check("direct price mode fixes multiplier to one", !Find<TextBox>(editor, "Multiplier").Enabled &&
                editor.TryBuildSettings(out var direct, out _) && direct.Profiles.Single().Mode == PriceEntryMode.EffectivePrices && direct.Profiles.Single().Multiplier == 1m);
            mode.SelectedIndex = 0; Find<TextBox>(editor, "Multiplier").Text = ".35";
            Check("base mode restores multiplier editing", Find<TextBox>(editor, "Multiplier").Enabled);
            var search = Find<TextBox>(editor, "ProfileSearch");
            Find<TextBox>(editor, "ProfileName").Text = "中转模型方案"; search.Text = "alias";
            Check("search commits and retains valid unsaved price draft", editor.TryBuildSettings(out var searched, out _) && searched.Profiles.Single().Name == "中转模型方案" && searched.Profiles.Single().Low.Input == 2m);
            search.Text = "no matching scheme";
            Check("empty search result keeps current draft editable", aliases.Text.Contains("relay/alias") && editor.TryBuildSettings(out var hiddenDraft, out _) && hiddenDraft.Profiles.Single().Low.Output == 10m);
            Find<TextBox>(editor, "Low0").Text = "invalid"; search.Text = "another search";
            Check("invalid draft prevents search from discarding edits", Find<TextBox>(editor, "Low0").Text == "invalid" && Find<Label>(editor, "PriceError").Text.Length > 0 && !editor.TryBuildSettings(out _, out _));
            Find<TextBox>(editor, "Low0").Text = "2"; search.Text = "";
            Check("corrected search clears inline error", Find<Label>(editor, "PriceError").Text.Length == 0 && editor.TryBuildSettings(out _, out _));
            if (previewDirectory is not null)
            {
                SavePreview(editor, Path.Combine(previewDirectory, "model-prices.png"));
                editor.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
                SavePreview(editor, Path.Combine(previewDirectory, "model-prices-dark.png"));
                editor.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            }
            // Exercise the actual save button without touching the user's configuration.
            Find<Button>(editor, "SavePrices").PerformClick();
            Check("save button atomically persists draft", editor.SavedSettings is not null && PricingStore.Load(pricePath).Settings.ForModel("relay/alias") is not null);
            using var details = new CostDetailsForm(); using var detailsHost = ShowOffscreen(details);
            var result = SessionCostTests.SampleResult(); details.SetResult(result.ThreadId, result);
            var groups = Find<DataGridView>(details, "CostGroups"); var unpriced = Find<DataGridView>(details, "UnpricedCalls");
            Check("details window displays model group and exact amount", groups.Rows.Count == 1 &&
                groups.Rows[0].Cells[8].Value?.ToString() == "$0.067");
            Check("details summary names current pricing basis", Find<TextBox>(details, "CostNotes").Text.Contains("按当前配置"));
            var bad = result with { Status = SessionCostStatus.Partial, Amounts = null, PricedCalls = 0, Groups = Array.Empty<CostGroup>(),
                Unpriced = new[] { new EvaluatedCall(new(new("session", "unpriced"), "turn", "relay/unknown", SessionCostTests.SampleUsage, 1), null, "—", null, "模型未配置价格") } };
            details.SetResult(result.ThreadId, bad);
            Check("details lists missing reason and model", unpriced.Rows.Count == 1 && unpriced.Rows[0].Cells[3].Value?.ToString() == "模型未配置价格");
            details.SetResult("new-thread", null);
            Check("details immediately clears old amount on switch", groups.Rows.Count == 0 && unpriced.Rows.Count == 0 && !Find<Label>(details, "CostSummary").Text.Contains("0.067"));
            Check("details clears stale tab counts on switch", Find<Button>(details, "PricedTab").Text == "已计价明细" && Find<Button>(details, "UnpricedTab").Text == "未计价原因" && Find<Label>(details, "CostAmount").Text == "—");
            details.SetResult(result.ThreadId, result);
            var amountLabel = Find<Label>(details, "CostAmount");
            Check("large amount has room for current DPI font", amountLabel.Height >= TextRenderer.MeasureText(amountLabel.Text, amountLabel.Font).Height);
            Check("priced tab has room for count and text", Find<Button>(details, "PricedTab").Width >= TextRenderer.MeasureText(Find<Button>(details, "PricedTab").Text, details.Font).Width + 26);
            var detailsBounds = details.Bounds; var detailsMinimum = details.MinimumSize;
            details.FitToWorkingArea(new(0, 0, 1024, 700)); details.PerformLayout();
            Check("small display preserves usable cost table and amount", new Rectangle(0, 0, 1024, 700).Contains(details.Bounds) && groups.Height >= 150 && amountLabel.Visible);
            details.MinimumSize = detailsMinimum; details.Bounds = detailsBounds; details.PerformLayout();
            if (previewDirectory is not null)
            {
                SavePreview(details, Path.Combine(previewDirectory, "cost-details.png"));
                details.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
                SavePreview(details, Path.Combine(previewDirectory, "cost-details-dark.png"));
                details.SetResult(result.ThreadId, bad); Find<Button>(details, "UnpricedTab").PerformClick();
                SavePreview(details, Path.Combine(previewDirectory, "cost-unpriced-dark.png"));
                details.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
                details.SetResult("waiting", null);
                SavePreview(details, Path.Combine(previewDirectory, "cost-waiting.png"));
                using var picker = new SessionPickerForm(new[]
                {
                    new SessionEntry("example-ui-session", "synthetic", new DateTime(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc), 0, "D:/Projects/demo", "设计与更新用量界面"),
                    new SessionEntry("example-cost-session", "synthetic", new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc), 0, "D:/Projects/demo", "核对会话费用")
                });
                using var pickerHost = ShowOffscreen(picker);
                SavePreview(picker, Path.Combine(previewDirectory, "session-picker.png"));
                picker.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
                SavePreview(picker, Path.Combine(previewDirectory, "session-picker-dark.png"));
            }
            foreach (var scale in new[] { 1.25f, 1.5f })
            {
                using var scaled = new ModelPriceForm(new(1, new[] { SessionCostTests.Profile() }), Array.Empty<string>(), pricePath);
                using var scaledHost = ShowOffscreen(scaled);
                scaled.Scale(new SizeF(scale, scale)); scaled.PerformLayout();
                var input = Find<TextBox>(scaled, "Low0"); var output = Find<TextBox>(scaled, "Low3");
                Check("price UI " + scale + " scale keeps fields separated", input.Width >= 100 &&
                    input.Parent!.RectangleToScreen(input.Bounds).Bottom <= output.Parent!.RectangleToScreen(output.Bounds).Top);
            }
        }
        catch (Exception e) { results.Add(new("unhandled price UI probe exception", false, e.ToString())); }
        finally { Directory.Delete(directory, true); }
        return new(results.Count(r => r.Passed), results.Count(r => !r.Passed), results);
    }
    private static void SavePreview(Form form, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(path, ImageFormat.Png);
    }
    private sealed class OffscreenHost : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
    private static Form ShowOffscreen(Form form)
    {
        var host = new OffscreenHost { StartPosition = FormStartPosition.Manual, Location = new(-32_000, -32_000),
            ShowInTaskbar = false, ClientSize = form.Size };
        form.TopLevel = false; form.Location = Point.Empty; host.Controls.Add(form);
        host.Show(); form.Show(); form.PerformLayout(); Application.DoEvents(); return host;
    }
}
