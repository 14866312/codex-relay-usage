namespace CodexTokenOverlay;

internal sealed class CostDetailsForm : Form
{
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 100, Padding = new(12), AutoEllipsis = true };
    private readonly DataGridView _groups = Grid();
    private readonly DataGridView _unpriced = Grid();
    private readonly TextBox _notes = new() { Dock = DockStyle.Bottom, Height = 80, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private string? _thread; private SessionCostResult? _last; private bool _initialized;
    public CostDetailsForm()
    {
        Text = "会话费用明细 · USD"; Font = new Font("Microsoft YaHei UI", 9); AutoScaleMode = AutoScaleMode.Dpi;
        _summary.Name = "CostSummary"; _notes.Name = "CostNotes"; _groups.Name = "CostGroups"; _unpriced.Name = "UnpricedCalls";
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new(1180, 650); MinimumSize = new(850, 500);
        var tabs = new TabControl { Dock = DockStyle.Fill }; var groups = new TabPage("分模型 / 分档费用"); var unpriced = new TabPage("未计价调用");
        groups.Controls.Add(_groups); unpriced.Controls.Add(_unpriced); tabs.TabPages.Add(groups); tabs.TabPages.Add(unpriced);
        foreach (var name in new[] { "完整模型名", "价格方案", "档位", "调用数", "普通输入", "缓存读取", "缓存写入", "输出", "费用合计" }) _groups.Columns.Add(name, name);
        foreach (var name in new[] { "模型", "请求 ID", "档位", "未计价原因" }) _unpriced.Columns.Add(name, name);
        _groups.Columns[0].FillWeight = 200; _groups.Columns[2].FillWeight = 120; _groups.Columns[3].FillWeight = 50;
        _groups.Columns[3].MinimumWidth = 70;
        for (var i = 4; i < 9; i++)
        {
            _groups.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            _groups.Columns[i].MinimumWidth = 115;
            _groups.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        _unpriced.Columns[3].FillWeight = 200;
        Controls.Add(tabs); Controls.Add(_summary); Controls.Add(_notes);
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None };
    public void SetResult(string? threadId, SessionCostResult? result)
    {
        if (_initialized && _thread == threadId && ReferenceEquals(_last, result)) return;
        _initialized = true; _thread = threadId; _last = result;
        _groups.Rows.Clear(); _unpriced.Rows.Clear();
        if (result is null)
        { _summary.Text = "会话：" + (threadId ?? "未识别") + "\r\n正在等待当前会话的费用数据"; _notes.Text = "按当前配置估算（USD） · Codex 本地日志"; return; }
        var detail = result.Amounts is { } a ? "普通输入 " + CostFormatting.Money(a.Input) + " · 缓存读取 " + CostFormatting.Money(a.CacheRead)
            + " · 缓存写入 " + CostFormatting.Money(a.CacheWrite) + " · 输出 " + CostFormatting.Money(a.Output) : "尚无可计价金额";
        _summary.Text = "会话：" + result.ThreadId + "\r\n" + result.StateText + " · " + CostFormatting.Summary(result, false) + " · 已计价 " + result.PricedCalls + "/" + result.RecordedCalls + " 次\r\n" + detail;
        foreach (var g in result.Groups) _groups.Rows.Add(g.Model, g.Profile, g.Tier, g.Count, CostFormatting.Money(g.Amounts.Input),
            CostFormatting.Money(g.Amounts.CacheRead), CostFormatting.Money(g.Amounts.CacheWrite), CostFormatting.Money(g.Amounts.Output), CostFormatting.Money(g.Amounts.Total));
        foreach (var c in result.Unpriced) _unpriced.Rows.Add(c.Call.Model ?? "未知模型", c.Call.Key.ResponseId, c.Tier, c.Reason ?? "未提供");
        _notes.Text = "按当前配置重算的美元估算 · Codex 本地日志 × 用户价格 · 缓存写入使用统一单价\r\n"
            + string.Join("；", result.Issues) + "\r\n用量由中转站提供并写入本地日志，估算金额不等同于中转站账单。";
    }
}
