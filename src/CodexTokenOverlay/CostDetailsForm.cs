namespace CodexTokenOverlay;

internal sealed class CostDetailsForm : ModernDialog
{
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted" };
    private readonly Label _amount = new() { Text = "—", Dock = DockStyle.Top, Height = 72, Tag = "accent", Name = "CostAmount", Font = new Font("Segoe UI Semibold", 24) };
    private readonly DataGridView _groups = Grid();
    private readonly DataGridView _unpriced = Grid();
    private readonly TextBox _notes = new() { Dock = DockStyle.Bottom, Height = 70, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Tag = "notes" };
    private readonly ModernButton _pricedTab = new() { Text = "已计价明细", Width = 200, Kind = ButtonKind.Primary, Name = "PricedTab" };
    private readonly ModernButton _unpricedTab = new() { Text = "未计价原因", Width = 200, Kind = ButtonKind.Ghost, Name = "UnpricedTab" };
    private readonly Panel _tables = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Tag = "muted", Visible = false };
    private bool _showUnpriced;
    private string? _thread; private SessionCostResult? _last; private bool _initialized;
    public CostDetailsForm() : base("会话费用明细")
    {
        _summary.Name = "CostSummary"; _notes.Name = "CostNotes"; _groups.Name = "CostGroups"; _unpriced.Name = "UnpricedCalls";
        ClientSize = new(1180, 680); MinimumSize = new(850, 500);
        foreach (var name in new[] { "完整模型名", "价格方案", "档位", "调用数", "普通输入", "缓存读取", "缓存写入", "输出", "费用合计" }) _groups.Columns.Add(name, name);
        foreach (var name in new[] { "模型", "请求 ID", "档位", "未计价原因" }) _unpriced.Columns.Add(name, name);
        _groups.Columns[0].FillWeight = 200; _groups.Columns[2].FillWeight = 100; _groups.Columns[3].FillWeight = 85;
        _groups.Columns[0].MinimumWidth = 160; _groups.Columns[1].MinimumWidth = 110;
        _groups.Columns[2].MinimumWidth = 100; _groups.Columns[3].MinimumWidth = 90;
        for (var i = 4; i < 9; i++)
        {
            _groups.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells; _groups.Columns[i].MinimumWidth = 110;
            _groups.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        _unpriced.Columns[3].FillWeight = 240;
        var overview = new Panel { Dock = DockStyle.Top, Height = 152 }; overview.Controls.Add(_summary); overview.Controls.Add(_amount);
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new(0, 6, 0, 8) };
        tabs.Controls.Add(_pricedTab); tabs.Controls.Add(_unpricedTab);
        _pricedTab.Click += (_, _) => SelectTab(false); _unpricedTab.Click += (_, _) => SelectTab(true);
        _tables.Controls.Add(_groups); _tables.Controls.Add(_unpriced); _tables.Controls.Add(_empty);
        Body.Controls.Add(_tables); Body.Controls.Add(tabs); Body.Controls.Add(overview); Body.Controls.Add(_notes);
        SelectTab(false); ApplyTheme(Palette);
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None };
    private void SelectTab(bool unpriced)
    {
        _showUnpriced = unpriced; _pricedTab.Kind = unpriced ? ButtonKind.Ghost : ButtonKind.Primary; _unpricedTab.Kind = unpriced ? ButtonKind.Primary : ButtonKind.Ghost;
        _pricedTab.Invalidate(); _unpricedTab.Invalidate(); _groups.Visible = !unpriced; _unpriced.Visible = unpriced;
        RefreshEmpty();
    }
    private void RefreshEmpty()
    {
        var noRows = (_showUnpriced ? _unpriced : _groups).Rows.Count == 0;
        _empty.Text = _last is null ? "等待当前会话的费用数据" : _showUnpriced ? "没有未计价调用" : "暂无已计价调用\n请在“模型价格”中配置当前模型";
        _empty.Visible = noRows; if (noRows) _empty.BringToFront();
    }
    public void SetResult(string? threadId, SessionCostResult? result)
    {
        if (_initialized && _thread == threadId && ReferenceEquals(_last, result)) return;
        _initialized = true; _thread = threadId; _last = result; _groups.Rows.Clear(); _unpriced.Rows.Clear();
        if (result is null)
        {
            _pricedTab.Text = "已计价明细"; _unpricedTab.Text = "未计价原因";
            _amount.Text = "—"; _summary.Text = "等待当前会话的费用数据"; _notes.Text = "按当前配置估算（USD） · Codex 本地日志"; RefreshEmpty(); return;
        }
        var detail = result.Amounts is { } a ? "普通输入 " + CostFormatting.Money(a.Input) + "    缓存读取 " + CostFormatting.Money(a.CacheRead)
            + "    缓存写入 " + CostFormatting.Money(a.CacheWrite) + "    输出 " + CostFormatting.Money(a.Output) : "尚无可计价金额";
        _amount.Text = result.Amounts is { } amount ? CostFormatting.Money(amount.Total) : "—";
        _summary.Text = "按当前配置估算 · USD   /   " + result.StateText + "   /   已计价 " + result.PricedCalls + "/" + result.RecordedCalls + " 次\r\n" + detail;
        foreach (var g in result.Groups) _groups.Rows.Add(g.Model, g.Profile, g.Tier, g.Count, CostFormatting.Money(g.Amounts.Input),
            CostFormatting.Money(g.Amounts.CacheRead), CostFormatting.Money(g.Amounts.CacheWrite), CostFormatting.Money(g.Amounts.Output), CostFormatting.Money(g.Amounts.Total));
        foreach (var c in result.Unpriced) _unpriced.Rows.Add(c.Call.Model ?? "未知模型", c.Call.Key.ResponseId, c.Tier, c.Reason ?? "未提供");
        _pricedTab.Text = "已计价明细  " + result.PricedCalls; _unpricedTab.Text = "未计价原因  " + result.Unpriced.Count;
        _notes.Text = "按当前配置估算（USD） · 缓存写入使用统一单价"
            + (result.Issues.Count == 0 ? "" : "\r\n" + string.Join("；", result.Issues)) + "\r\n用量来自 Codex 本地日志，费用供参考。";
        RefreshEmpty();
    }
}
