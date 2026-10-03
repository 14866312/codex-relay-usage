using System.Globalization;

namespace CodexTokenOverlay;

internal sealed class ModelPriceForm : Form
{
    private readonly List<ModelPriceProfile> _profiles;
    private readonly string? _path;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(ModelPriceProfile.Name) };
    private readonly ComboBox _model = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _aliases = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _mode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _multiplier = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _twoTiers = new() { Text = "按每次调用的总输入自动分档", AutoSize = true };
    private readonly TextBox _threshold = new() { Dock = DockStyle.Fill };
    private readonly TextBox[] _low = Enumerable.Range(0, 4).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private readonly TextBox[] _high = Enumerable.Range(0, 4).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private string? _currentId;
    private bool _loading;
    public PricingSettings? SavedSettings { get; private set; }

    public ModelPriceForm(PricingSettings settings, IEnumerable<string> knownModels, string? path = null)
    {
        _profiles = settings.Profiles.ToList(); _path = path;
        _name.Name = "ProfileName"; _aliases.Name = "ModelAliases"; _mode.Name = "PriceMode";
        _multiplier.Name = "Multiplier"; _threshold.Name = "Threshold"; _twoTiers.Name = "TwoTiers";
        for (var i = 0; i < 4; i++) { _low[i].Name = "Low" + i; _high[i].Name = "High" + i; }
        Text = "模型价格 · USD / 1M tokens"; Font = new Font("Microsoft YaHei UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new(1_080, 720); MinimumSize = new(940, 660);
        _model.Items.AddRange(knownModels.Concat(_profiles.SelectMany(p => p.Models)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Cast<object>().ToArray());
        if (_model.Items.Count > 0) _model.SelectedIndex = 0;
        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 250, Padding = new(10), ColumnCount = 1, RowCount = 5 };
        left.RowStyles.Add(new(SizeType.AutoSize)); left.RowStyles.Add(new(SizeType.Percent, 100));
        left.RowStyles.Add(new(SizeType.Absolute, 32)); left.RowStyles.Add(new(SizeType.Absolute, 38)); left.RowStyles.Add(new(SizeType.Absolute, 38));
        left.Controls.Add(new Label { Text = "价格方案\n可绑定多个模型", AutoSize = true }, 0, 0); left.Controls.Add(_list, 0, 1);
        left.Controls.Add(_model, 0, 2);
        var add = new Button { Text = "添加模型 / 方案", Dock = DockStyle.Fill }; add.Click += (_, _) => AddProfile(); left.Controls.Add(add, 0, 3);
        var remove = new Button { Text = "删除所选方案", Dock = DockStyle.Fill }; remove.Click += (_, _) => RemoveProfile(); left.Controls.Add(remove, 0, 4);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(12), ColumnCount = 2, RowCount = 9 };
        editor.ColumnStyles.Add(new(SizeType.Absolute, 150)); editor.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) editor.RowStyles.Add(new(SizeType.AutoSize));
        void Row(string label, Control value, int row) { editor.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new(0, 5, 0, 0) }, 0, row); editor.Controls.Add(value, 1, row); }
        Row("方案名称", _name, 0); _aliases.Height = 88; Row("完整模型名\n每行一个别名", _aliases, 1);
        _mode.Items.AddRange(["基础单价 × 倍率", "直接填写折后单价"]); Row("价格录入方式", _mode, 2);
        Row("计费倍率", _multiplier, 3); Row("双档价格", _twoTiers, 4); Row("分档阈值（tokens）", _threshold, 5);
        var rates = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 5 };
        rates.ColumnStyles.Add(new(SizeType.Percent, 32)); rates.ColumnStyles.Add(new(SizeType.Percent, 34)); rates.ColumnStyles.Add(new(SizeType.Percent, 34));
        rates.Controls.Add(new Label { Text = "USD / 1M tokens", AutoSize = true }, 0, 0);
        rates.Controls.Add(new Label { Text = "低档 / 统一价格", AutoSize = true }, 1, 0); rates.Controls.Add(new Label { Text = "高档（超过阈值）", AutoSize = true }, 2, 0);
        var names = new[] { "普通输入", "缓存读取", "缓存写入", "输出" };
        for (var i = 0; i < 4; i++) { rates.Controls.Add(new Label { Text = names[i], AutoSize = true, Padding = new(0, 5, 0, 0) }, 0, i + 1); rates.Controls.Add(_low[i], 1, i + 1); rates.Controls.Add(_high[i], 2, i + 1); }
        editor.Controls.Add(rates, 0, 6); editor.SetColumnSpan(rates, 2);
        var note = new Label { AutoSize = true, MaximumSize = new(740, 0), Margin = new(0, 12, 0, 0), Text =
            "单价可填 0；留空表示未配置。请用小数点填写数字。\n阈值包含缓存输入，整次调用按选中档位计费。缓存写入使用统一单价，不区分时长。\n保存后，所有已识别的历史调用将按当前价格重新估算；缺失记录不会补成零。" };
        editor.Controls.Add(note, 0, 7); editor.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new(8) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "保存并重算", Name = "SavePrices", Width = 115 };
        save.Click += (_, _) => Save(); buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        Controls.Add(editor); Controls.Add(left); Controls.Add(buttons); CancelButton = cancel;
        _list.SelectedIndexChanged += (_, _) => ChangeSelection();
        _mode.SelectedIndexChanged += (_, _) => { if (!_loading && _mode.SelectedIndex == 1) _multiplier.Text = "1"; _multiplier.Enabled = _mode.SelectedIndex == 0; };
        _twoTiers.CheckedChanged += (_, _) => UpdateTierEnabled();
        RefreshList(_profiles.FirstOrDefault()?.Id);
        if (_profiles.Count == 0) AddProfile();
    }
    private void UpdateTierEnabled() { _threshold.Enabled = _twoTiers.Checked; foreach (var box in _high) box.Enabled = _twoTiers.Checked; }
    private void RefreshList(string? selectedId)
    {
        _loading = true; _list.Items.Clear(); _list.Items.AddRange(_profiles.Cast<object>().ToArray());
        _list.SelectedIndex = _profiles.FindIndex(p => p.Id == selectedId); _loading = false;
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
    { if (FlushCurrent(out var error)) return true; MessageBox.Show(this, error, "价格设置", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
    private void ChangeSelection()
    {
        if (_loading) return; var next = (_list.SelectedItem as ModelPriceProfile)?.Id; if (next == _currentId) return;
        if (!FlushWithMessage()) { _loading = true; _list.SelectedIndex = _profiles.FindIndex(p => p.Id == _currentId); _loading = false; return; }
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
        _profiles.Add(p); RefreshList(p.Id);
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
        { MessageBox.Show(this, error, "价格设置", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        SavedSettings = settings; DialogResult = DialogResult.OK; Close();
    }
}
