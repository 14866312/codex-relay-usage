using System.Globalization;

namespace CodexTokenOverlay;

internal sealed class ModelPriceForm : ModernDialog
{
    private readonly List<ModelPriceProfile> _profiles;
    private readonly string? _path;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(ModelPriceProfile.Name) };
    private readonly ComboBox _model = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _aliases = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _mode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _multiplier = new() { Dock = DockStyle.Fill };
    private readonly ModernToggle _twoTiers = new() { Text = "按每次调用的总输入自动分档" };
    private readonly TextBox _threshold = new() { Dock = DockStyle.Fill };
    private readonly TextBox[] _low = Enumerable.Range(0, 4).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private readonly TextBox[] _high = Enumerable.Range(0, 4).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private readonly TextBox _search = new() { Name = "ProfileSearch", PlaceholderText = "搜索方案或模型…" };
    private readonly Label _error = new() { AutoSize = true, Tag = "danger", Name = "PriceError" };
    private string? _currentId;
    private bool _loading;
    public PricingSettings? SavedSettings { get; private set; }

    public ModelPriceForm(PricingSettings settings, IEnumerable<string> knownModels, string? path = null) : base("模型价格")
    {
        _profiles = settings.Profiles.ToList(); _path = path;
        _name.Name = "ProfileName"; _aliases.Name = "ModelAliases"; _mode.Name = "PriceMode";
        _multiplier.Name = "Multiplier"; _threshold.Name = "Threshold"; _twoTiers.Name = "TwoTiers";
        for (var i = 0; i < 4; i++) { _low[i].Name = "Low" + i; _high[i].Name = "High" + i; }
        ClientSize = new(1080, 780); MinimumSize = new(940, 700);
        _list.BorderStyle = BorderStyle.None; _list.DrawMode = DrawMode.OwnerDrawFixed; _list.ItemHeight = 38;
        _list.DrawItem += DrawProfile; _list.IntegralHeight = false;
        _model.Items.AddRange(knownModels.Concat(_profiles.SelectMany(p => p.Models)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Cast<object>().ToArray());
        if (_model.Items.Count > 0) _model.SelectedIndex = 0;
        _model.AccessibleName = "要添加的完整模型名";
        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 230, Padding = new(0, 0, 20, 0), ColumnCount = 1, RowCount = 5 };
        left.RowStyles.Add(new(SizeType.Absolute, 46)); left.RowStyles.Add(new(SizeType.Percent, 100));
        left.RowStyles.Add(new(SizeType.Absolute, 30)); left.RowStyles.Add(new(SizeType.Absolute, 46)); left.RowStyles.Add(new(SizeType.Absolute, 44));
        left.Controls.Add(new FieldFrame(_search), 0, 0); left.Controls.Add(_list, 0, 1);
        left.Controls.Add(LabelFor("新增方案的完整模型名"), 0, 2); left.Controls.Add(new FieldFrame(_model), 0, 3);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new(SizeType.Percent, 74)); actions.ColumnStyles.Add(new(SizeType.Percent, 26));
        var add = new ModernButton { Text = "新增方案", Glyph = UiGlyph.Add, Kind = ButtonKind.Secondary, Dock = DockStyle.Fill, Name = "AddProfile" };
        var remove = new ModernButton { Glyph = UiGlyph.Delete, Kind = ButtonKind.Ghost, Dock = DockStyle.Fill, AccessibleName = "删除所选方案" };
        add.Click += (_, _) => AddProfile(); remove.Click += (_, _) => RemoveProfile();
        actions.Controls.Add(add, 0, 0); actions.Controls.Add(remove, 1, 0); left.Controls.Add(actions, 0, 4);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(6, 0, 0, 0) };
        var editor = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 8, Margin = Padding.Empty };
        var heights = new[] { 68, 96, 70, 68, 50, 230, 66, 32 };
        foreach (var height in heights) editor.RowStyles.Add(new(SizeType.Absolute, height));
        Control Field(string caption, Control value, int height = 38)
        {
            var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new(0, 0, 0, 4) };
            field.RowStyles.Add(new(SizeType.Absolute, 24)); field.RowStyles.Add(new(SizeType.Percent, 100));
            field.Controls.Add(LabelFor(caption), 0, 0); field.Controls.Add(new FieldFrame(value, height), 0, 1); return field;
        }
        editor.Controls.Add(Field("方案名称", _name), 0, 0);
        editor.Controls.Add(Field("完整模型名 / 别名（每行一个，保留中转前缀）", _aliases, 62), 0, 1);
        _mode.Items.AddRange(["基础单价 × 倍率", "直接填写折后单价"]);
        editor.Controls.Add(Field("计费方式", _mode), 0, 2);
        _multiplier.PlaceholderText = "例如 0.35"; editor.Controls.Add(Field("倍率（折后单价模式固定为 1）", _multiplier), 0, 3);
        var tiers = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        tiers.ColumnStyles.Add(new(SizeType.Percent, 58)); tiers.ColumnStyles.Add(new(SizeType.Absolute, 52)); tiers.ColumnStyles.Add(new(SizeType.Percent, 42));
        _twoTiers.Text = "启用双档价格"; _twoTiers.Dock = DockStyle.Fill;
        tiers.Controls.Add(_twoTiers, 0, 0); tiers.Controls.Add(LabelFor("阈值"), 1, 0); tiers.Controls.Add(new FieldFrame(_threshold), 2, 0); editor.Controls.Add(tiers, 0, 4);
        var rates = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, Margin = new(0, 8, 0, 4) };
        rates.ColumnStyles.Add(new(SizeType.Percent, 28)); rates.ColumnStyles.Add(new(SizeType.Percent, 36)); rates.ColumnStyles.Add(new(SizeType.Percent, 36));
        rates.RowStyles.Add(new(SizeType.Absolute, 32)); for (var i = 0; i < 4; i++) rates.RowStyles.Add(new(SizeType.Percent, 25));
        rates.Controls.Add(LabelFor("USD / 1M tokens"), 0, 0); rates.Controls.Add(LabelFor("低档 / 统一价格"), 1, 0); rates.Controls.Add(LabelFor("高档（超过阈值）"), 2, 0);
        var names = new[] { "普通输入", "缓存读取", "缓存写入", "输出" };
        for (var i = 0; i < 4; i++)
        {
            _low[i].PlaceholderText = "未配置"; _high[i].PlaceholderText = "未配置";
            rates.Controls.Add(LabelFor(names[i]), 0, i + 1); rates.Controls.Add(new FieldFrame(_low[i]), 1, i + 1); rates.Controls.Add(new FieldFrame(_high[i]), 2, i + 1);
        }
        editor.Controls.Add(rates, 0, 5);
        editor.Controls.Add(new Label { Dock = DockStyle.Fill, Tag = "muted", Text = "单价可填 0；留空表示未配置。保存后按当前价格重新计算本会话费用。", AutoEllipsis = true, Padding = new(0, 6, 0, 0) }, 0, 6);
        editor.Controls.Add(_error, 0, 7); scroll.Controls.Add(editor);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 10, 0, 0) };
        var save = new ModernButton { Text = "保存并重算", Name = "SavePrices", Kind = ButtonKind.Primary, Width = 126 };
        var cancel = new ModernButton { Text = "取消", DialogResult = DialogResult.Cancel, Kind = ButtonKind.Secondary, Width = 82 };
        save.Click += (_, _) => Save(); buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        Body.Controls.Add(scroll); Body.Controls.Add(left); Body.Controls.Add(buttons); CancelButton = cancel; AcceptButton = save;
        _list.SelectedIndexChanged += (_, _) => ChangeSelection();
        _mode.SelectedIndexChanged += (_, _) => { if (!_loading && _mode.SelectedIndex == 1) _multiplier.Text = "1"; _multiplier.Enabled = _mode.SelectedIndex == 0; };
        _twoTiers.CheckedChanged += (_, _) => UpdateTierEnabled();
        _search.TextChanged += (_, _) => FilterProfiles();
        RefreshList(_profiles.FirstOrDefault()?.Id); if (_profiles.Count == 0) AddProfile(); ApplyTheme(Palette);
    }
    private static Label LabelFor(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "muted", AutoEllipsis = true, Margin = new(0, 0, 6, 0) };
    private void DrawProfile(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return; var selected = e.State.HasFlag(DrawItemState.Selected);
        using var b = new SolidBrush(selected ? Palette.ToolbarOpen : Palette.Background); e.Graphics.FillRectangle(b, e.Bounds);
        if (selected) { using var bar = new SolidBrush(Palette.Accent); e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top + 7, 3, e.Bounds.Height - 14); }
        var profile = (ModelPriceProfile)_list.Items[e.Index];
        TextRenderer.DrawText(e.Graphics, profile.Name, e.Font, Rectangle.Inflate(e.Bounds, -12, 0), selected ? Palette.Value : Palette.Label, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    private void FilterProfiles()
    {
        // Searching must never throw away an unsaved valid draft or switch to another profile.
        if (_loading) return; if (!FlushCurrent(out var error)) { _error.Text = error; return; }
        _error.Text = ""; RefreshList(_currentId);
    }
    private void UpdateTierEnabled() { _threshold.Enabled = _twoTiers.Checked; foreach (var box in _high) box.Enabled = _twoTiers.Checked; }
    private void RefreshList(string? selectedId)
    {
        _loading = true; _list.BeginUpdate(); _list.Items.Clear();
        var query = _search.Text.Trim();
        var shown = _profiles.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Models.Any(m => m.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _list.Items.AddRange(shown.Cast<object>().ToArray());
        _list.SelectedIndex = Array.FindIndex(shown, p => p.Id == selectedId); _list.EndUpdate(); _loading = false;
        LoadProfile(_profiles.FirstOrDefault(p => p.Id == selectedId));
    }
    private void LoadProfile(ModelPriceProfile? profile)
    {
        _loading = true; _currentId = profile?.Id;
        _name.Text = profile?.Name ?? ""; _aliases.Text = string.Join(Environment.NewLine, profile?.Models ?? Array.Empty<string>());
        _mode.SelectedIndex = profile?.Mode == PriceEntryMode.EffectivePrices ? 1 : 0;
        _multiplier.Text = Format(profile?.Mode == PriceEntryMode.EffectivePrices ? 1m : profile?.Multiplier ?? 1m);
        _twoTiers.Checked = profile?.TwoTiers ?? true; _threshold.Text = (profile?.Threshold ?? 272_000).ToString(CultureInfo.InvariantCulture);
        var low = profile?.Low ?? new(); var high = profile?.High ?? new();
        var lows = new[] { low.Input, low.CacheRead, low.CacheWrite, low.Output }; var highs = new[] { high.Input, high.CacheRead, high.CacheWrite, high.Output };
        for (var i = 0; i < 4; i++) { _low[i].Text = Format(lows[i]); _high[i].Text = Format(highs[i]); }
        _loading = false; _multiplier.Enabled = _mode.SelectedIndex == 0; UpdateTierEnabled();
    }
    private static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private bool FlushCurrent(out string? error)
    {
        error = null; if (_currentId is null) return true;
        try
        {
            decimal? Parse(TextBox box, bool required = false)
            {
                var text = box.Text.Trim(); if (text.Length == 0 && !required) return null;
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0)
                    throw new FormatException("单价和倍率必须是非负数字；留空单价表示未配置。");
                return number;
            }
            if (!long.TryParse(_threshold.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) || threshold < 0) throw new FormatException("阈值必须是非负整数。");
            TokenPrices Read(TextBox[] boxes) => new(Parse(boxes[0]), Parse(boxes[1]), Parse(boxes[2]), Parse(boxes[3]));
            var models = _aliases.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            var p = new ModelPriceProfile(_currentId, _name.Text.Trim(), models, (PriceEntryMode)_mode.SelectedIndex,
                _mode.SelectedIndex == 1 ? 1m : Parse(_multiplier, true)!.Value, _twoTiers.Checked, threshold, Read(_low), Read(_high));
            var index = _profiles.FindIndex(p => p.Id == _currentId);
            var draft = _profiles.ToArray(); draft[index] = p;
            error = new PricingSettings(PricingSettings.CurrentVersion, draft).ValidationIssue();
            if (error is not null) return false;
            _profiles[index] = p; return true;
        }
        catch (FormatException e) { error = e.Message; return false; }
    }
    private bool FlushWithMessage()
    { if (FlushCurrent(out var error)) { _error.Text = ""; return true; } _error.Text = error; return false; }
    private void ChangeSelection()
    {
        if (_loading || _list.SelectedItem is not ModelPriceProfile selected) return; var next = selected.Id; if (next == _currentId) return;
        if (!FlushWithMessage()) { _loading = true; _list.SelectedIndex = Enumerable.Range(0, _list.Items.Count).FirstOrDefault(i => ((ModelPriceProfile)_list.Items[i]).Id == _currentId, -1); _loading = false; return; }
        RefreshList(next);
    }
    private void AddProfile()
    {
        if (!FlushWithMessage()) return;
        var model = _model.Text.Trim();
        var existing = _profiles.FirstOrDefault(p => p.Models.Contains(model, StringComparer.Ordinal));
        if (existing is not null) { RefreshList(existing.Id); return; }
        var p = new ModelPriceProfile(Guid.NewGuid().ToString("N"), model.Length > 0 ? model : "新价格方案",
            model.Length > 0 ? new[] { model } : Array.Empty<string>(), PriceEntryMode.BaseWithMultiplier, 1m, true, 272_000, new(), new());
        _profiles.Add(p); _loading = true; _search.Text = ""; _loading = false; RefreshList(p.Id);
    }
    private void RemoveProfile()
    { if (_currentId is null) return; _profiles.RemoveAll(p => p.Id == _currentId); RefreshList(_profiles.FirstOrDefault()?.Id); }
    internal bool TryBuildSettings(out PricingSettings settings, out string? error)
    {
        if (!FlushCurrent(out error)) { settings = PricingSettings.Empty; return false; }
        settings = new(PricingSettings.CurrentVersion, _profiles.ToArray()); error = settings.ValidationIssue(); return error is null;
    }
    private void Save()
    {
        if (!TryBuildSettings(out var settings, out var error) || !PricingStore.TrySave(settings, out error, _path))
        { _error.Text = error; return; }
        SavedSettings = settings; DialogResult = DialogResult.OK; Close();
    }
}
