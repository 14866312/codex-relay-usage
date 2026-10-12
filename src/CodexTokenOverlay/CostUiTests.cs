using System.Drawing.Imaging;

namespace CodexTokenOverlay;

internal static class CostUiTests
{
    private static readonly string[] RelayTestModels = ["relay/test"];
    private static readonly string[] RelatedGroupNotes = ["1 个成员关系未确认，未计入已知合计"];

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
            using var editor = new ModelPriceForm(PricingSettings.Empty, RelayTestModels, pricePath);
            using var editorHost = ShowOffscreen(editor);
            Check("new model rates start blank", editor.TryBuildSettings(out var blank, out _) && blank.ForModel("relay/test")!.Low == new TokenPrices());
            Check("default tier threshold 272000 editable", Find<TextBox>(editor, "Threshold").Text == "272000");
            // A single-line TextBox is a native edit control: its height follows the font
            // and stays at 16 px at 96 DPI no matter how tall its container is. The visible
            // input surface is the FieldFrame, so measure that for "usable bounds" instead
            // of asserting a height the native edit never reports on a 100% display.
            var lowInput = Find<TextBox>(editor, "Low0");
            var lowSurface = lowInput.Parent;
            Check("price editor low input field has usable bounds",
                lowInput.Width >= 100 && lowSurface is { Height: >= 20 });
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
            Check("details summary stays user-facing and compact", Find<Label>(details, "CostSummary").Text == "按当前配置估算");
            var bad = result with { Status = SessionCostStatus.Partial, Amounts = null, PricedCalls = 0, Groups = Array.Empty<CostGroup>(),
                Unpriced = new[] { new EvaluatedCall(new(new("session", "unpriced"), "turn", "relay/unknown", SessionCostTests.SampleUsage, 1), null, "—", null, "模型未配置价格") } };
            details.SetResult(result.ThreadId, bad);
            Check("details lists missing reason and model", unpriced.Rows.Count == 1 && unpriced.Rows[0].Cells[2].Value?.ToString() == "模型未配置价格");
            Check("unpriced tab hides internal request ids", unpriced.Columns.Cast<DataGridViewColumn>().All(c => c.Name != "请求 ID"));
            details.SetResult("new-thread", null);
            Check("details immediately clears old amount on switch", groups.Rows.Count == 0 && unpriced.Rows.Count == 0 && !Find<Label>(details, "CostSummary").Text.Contains("0.067"));
            Check("details clears stale tab counts on switch", Find<Button>(details, "PricedTab").Text == "已计价明细" && Find<Button>(details, "UnpricedTab").Text == "未计价原因" && Find<Label>(details, "CostAmount").Text == "—");
            details.SetResult(result.ThreadId, result);
            var amountLabel = Find<Label>(details, "CostAmount");
            Check("large amount has room for current DPI font", amountLabel.Height >= TextRenderer.MeasureText(amountLabel.Text, amountLabel.Font).Height);
            Check("priced tab has room for count and text", Find<Button>(details, "PricedTab").Width >= TextRenderer.MeasureText(Find<Button>(details, "PricedTab").Text, details.Font).Width + 26);
            var relatedGroup = new SessionGroupSnapshot(result.ThreadId, 1, 1, 1, 1, new[]
            {
                new RelatedMemberUsage(result.ThreadId, null, RelatedMemberKind.Root, "当前会话", null,
                    RelatedCoverage.Complete, RelatedCoverage.Complete, 100, 80, 0, 20, 0, 1, 1, .5m, null, "本地日志")
                    { CountedInTotals = true },
                new RelatedMemberUsage("sub-thread-example", result.ThreadId, RelatedMemberKind.Subagent, "父 root…", "data-explorer",
                    RelatedCoverage.Partial, RelatedCoverage.Unavailable, 40, 30, 0, 10, 0, 1, 0, null,
                    "线程累计与调用合计不一致，只计入自身调用", "本地日志") { CountedInTotals = true },
                new RelatedMemberUsage("side-thread-example", result.ThreadId, RelatedMemberKind.SideChat, "会话内侧聊", null,
                    RelatedCoverage.Complete, RelatedCoverage.Unavailable, 30, 20, 0, 10, 0, 0, 0, null, null, "运行时观察")
                    { CountedInTotals = true }
            }, RelatedGroupNotes);
            details.SetGroup(relatedGroup);
            Find<Button>(details, "RelatedTab").PerformClick();
            var related = Find<DataGridView>(details, "RelatedUsage");
            Check("related tab lists every member kind with its coverage", related.Rows.Count == 3 &&
                related.Rows[0].Cells[0].Value?.ToString() == "主会话" && related.Rows[1].Cells[0].Value?.ToString() == "子代理" &&
                related.Rows[1].Cells[5].Value?.ToString()?.Contains("不一致") == true &&
                related.Rows[2].Cells[5].Value?.ToString()?.Contains("费用 不可用") == true);
            Check("related summary reports the known total", Find<Label>(details, "RelatedSummary").Text.Contains("已知合计 170"));
            Check("related notes explain the exclusions", Find<Label>(details, "RelatedNotes").Text.Contains("未确认"));
            details.SetGroup(null);
            Check("related tab clears when the group is gone", related.Rows.Count == 0);
            Find<Button>(details, "PricedTab").PerformClick();
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
                details.SetGroup(relatedGroup); Find<Button>(details, "RelatedTab").PerformClick();
                SavePreview(details, Path.Combine(previewDirectory, "cost-related-dark.png"));
                details.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
                SavePreview(details, Path.Combine(previewDirectory, "cost-related.png"));
                details.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
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
            using var binding = new SessionPickerForm(new[]
            {
                new SessionEntry("example-ui-session", "synthetic", new DateTime(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc), 0, "D:/Projects/demo", "设计与更新用量界面")
            }, "设计与更新用量界面");
            using var bindingHost = ShowOffscreen(binding);
            binding.PerformLayout();
            var bindingCaption = Find<Label>(binding, "BindingCaption");
            var bindingField = Find<FieldFrame>(binding, "SessionFilter");
            var bindingList = Find<ListView>(binding, "SessionList");
            var bindingTextHeight = TextRenderer.MeasureText(bindingCaption.Text, bindingCaption.Font,
                new Size(bindingCaption.ClientSize.Width - bindingCaption.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak).Height;
            Check("binding banner grows to fit its wrapped text",
                bindingCaption.Height >= bindingTextHeight + bindingCaption.Padding.Vertical);
            Check("binding banner text stays clear of the search field",
                bindingCaption.Top + bindingCaption.Padding.Top + bindingTextHeight <= bindingField.Top);
            Check("binding list starts below the search field", bindingList.Top >= bindingField.Bottom);
            if (previewDirectory is not null)
                SavePreview(binding, Path.Combine(previewDirectory, "session-picker-binding.png"));
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
    internal static void SavePreview(Form form, string path)
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
    private static OffscreenHost ShowOffscreen(Form form)
    {
        var host = new OffscreenHost { StartPosition = FormStartPosition.Manual, Location = new(-32_000, -32_000),
            ShowInTaskbar = false, ClientSize = form.Size };
        form.TopLevel = false; form.Location = Point.Empty; host.Controls.Add(form);
        host.Show(); form.Show(); form.PerformLayout(); Application.DoEvents(); return host;
    }
}
