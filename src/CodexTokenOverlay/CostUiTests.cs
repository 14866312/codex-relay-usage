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
            if (previewDirectory is not null) SavePreview(editor, Path.Combine(previewDirectory, "model-prices.png"));
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
            details.SetResult(result.ThreadId, result);
            if (previewDirectory is not null) SavePreview(details, Path.Combine(previewDirectory, "cost-details.png"));
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
