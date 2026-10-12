namespace CodexTokenOverlay;

internal sealed class CostDetailsForm : ModernDialog
{
    private enum DetailsTab { Priced, Unpriced, Related }
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted" };
    private readonly Label _amount = new() { Text = "—", Dock = DockStyle.Top, Height = 72, Tag = "accent", Name = "CostAmount", Font = new Font("Segoe UI Semibold", 24) };
    private readonly DataGridView _groups = Grid();
    private readonly DataGridView _unpriced = Grid();
    private readonly DataGridView _related = Grid();
    private readonly Label _relatedSummary = new() { Dock = DockStyle.Top, Height = 30, AutoEllipsis = true, Tag = "muted", Name = "RelatedSummary" };
    private readonly Label _relatedNotes = new() { Dock = DockStyle.Top, Height = 42, AutoEllipsis = true, Tag = "muted", Name = "RelatedNotes" };
    private readonly Panel _relatedPanel = new() { Dock = DockStyle.Fill };
    private readonly ModernButton _pricedTab = new() { Text = "已计价明细", Width = 200, Kind = ButtonKind.Primary, Name = "PricedTab" };
    private readonly ModernButton _unpricedTab = new() { Text = "未计价原因", Width = 200, Kind = ButtonKind.Ghost, Name = "UnpricedTab" };
    private readonly ModernButton _relatedTab = new() { Text = "关联用量", Width = 200, Kind = ButtonKind.Ghost, Name = "RelatedTab" };
    private readonly Panel _tables = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Tag = "muted", Visible = false };
    private DetailsTab _tab;
    private string? _thread; private SessionCostResult? _last; private SessionGroupSnapshot? _group; private bool _initialized;
    public CostDetailsForm() : base("会话费用明细")
    {
        _summary.Name = "CostSummary"; _groups.Name = "CostGroups"; _unpriced.Name = "UnpricedCalls"; _related.Name = "RelatedUsage";
        ClientSize = new(1180, 680); MinimumSize = new(850, 500);
        foreach (var name in new[] { "模型", "价格方案", "档位", "调用数", "输入", "缓存读取", "缓存写入", "输出", "费用合计" }) _groups.Columns.Add(name, name);
        foreach (var name in new[] { "模型", "档位", "未计价原因" }) _unpriced.Columns.Add(name, name);
        foreach (var name in new[] { "类型", "会话", "归属", "Token", "费用", "覆盖" }) _related.Columns.Add(name, name);
        _groups.Columns[0].FillWeight = 200; _groups.Columns[2].FillWeight = 100; _groups.Columns[3].FillWeight = 85;
        _groups.Columns[0].MinimumWidth = 160; _groups.Columns[1].MinimumWidth = 110;
        _groups.Columns[2].MinimumWidth = 100; _groups.Columns[3].MinimumWidth = 90;
        for (var i = 4; i < 9; i++)
        {
            _groups.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells; _groups.Columns[i].MinimumWidth = 110;
            _groups.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        _unpriced.Columns[2].FillWeight = 240;
        _related.Columns[0].FillWeight = 90; _related.Columns[1].FillWeight = 150; _related.Columns[2].FillWeight = 230;
        _related.Columns[3].FillWeight = 110; _related.Columns[4].FillWeight = 110; _related.Columns[5].FillWeight = 200;
        _related.Columns[0].MinimumWidth = 80; _related.Columns[1].MinimumWidth = 120; _related.Columns[2].MinimumWidth = 150;
        _related.Columns[3].MinimumWidth = 100; _related.Columns[4].MinimumWidth = 90; _related.Columns[5].MinimumWidth = 140;
        _related.Columns[3].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _related.Columns[4].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _relatedPanel.Controls.Add(_related);
        _relatedPanel.Controls.Add(_relatedNotes);
        _relatedPanel.Controls.Add(_relatedSummary);
        var overview = new Panel { Dock = DockStyle.Top, Height = 152 }; overview.Controls.Add(_summary); overview.Controls.Add(_amount);
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new(0, 6, 0, 8) };
        tabs.Controls.Add(_pricedTab); tabs.Controls.Add(_unpricedTab); tabs.Controls.Add(_relatedTab);
        _pricedTab.Click += (_, _) => SelectTab(DetailsTab.Priced);
        _unpricedTab.Click += (_, _) => SelectTab(DetailsTab.Unpriced);
        _relatedTab.Click += (_, _) => SelectTab(DetailsTab.Related);
        _tables.Controls.Add(_groups); _tables.Controls.Add(_unpriced); _tables.Controls.Add(_relatedPanel); _tables.Controls.Add(_empty);
        Body.Controls.Add(_tables); Body.Controls.Add(tabs); Body.Controls.Add(overview);
        SelectTab(DetailsTab.Priced); ApplyTheme(Palette);
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None };
    private void SelectTab(DetailsTab tab)
    {
        _tab = tab;
        _pricedTab.Kind = tab == DetailsTab.Priced ? ButtonKind.Primary : ButtonKind.Ghost;
        _unpricedTab.Kind = tab == DetailsTab.Unpriced ? ButtonKind.Primary : ButtonKind.Ghost;
        _relatedTab.Kind = tab == DetailsTab.Related ? ButtonKind.Primary : ButtonKind.Ghost;
        _pricedTab.Invalidate(); _unpricedTab.Invalidate(); _relatedTab.Invalidate();
        _groups.Visible = tab == DetailsTab.Priced; _unpriced.Visible = tab == DetailsTab.Unpriced;
        _relatedPanel.Visible = tab == DetailsTab.Related;
        RefreshEmpty();
    }
    private void RefreshEmpty()
    {
        var active = _tab switch { DetailsTab.Priced => (Control)_groups, DetailsTab.Unpriced => _unpriced, _ => _related };
        var noRows = active is DataGridView grid ? grid.Rows.Count == 0 : false;
        _empty.Text = _tab switch
        {
            DetailsTab.Related => _group is null || _group.RootThreadId is null ? "等待当前会话的关联用量"
                : "当前会话没有关联成员\n只统计当前会话的调用",
            DetailsTab.Unpriced => _last is null ? "等待当前会话的费用数据" : "没有未计价调用",
            _ => _last is null ? "等待当前会话的费用数据" : "暂无已计价调用\n请在“模型价格”中配置当前模型"
        };
        _empty.Visible = noRows; if (noRows) _empty.BringToFront();
    }
    public void SetResult(string? threadId, SessionCostResult? result)
    {
        if (_initialized && _thread == threadId && ReferenceEquals(_last, result)) return;
        _initialized = true; _thread = threadId; _last = result; _groups.Rows.Clear(); _unpriced.Rows.Clear();
        if (result is null)
        {
            _pricedTab.Text = "已计价明细"; _unpricedTab.Text = "未计价原因";
            _amount.Text = "—"; _summary.Text = "等待当前会话的费用数据"; RefreshEmpty(); return;
        }
        _amount.Text = result.Amounts is { } amount ? CostFormatting.Money(amount.Total) : "—";
        _summary.Text = result.Status switch
        {
            SessionCostStatus.Partial => "部分记录 · 已计价 " + result.PricedCalls + "/" + result.RecordedCalls + " 次",
            SessionCostStatus.Unavailable => "无法逐次计费",
            _ => "按当前配置估算"
        };
        foreach (var g in result.Groups) _groups.Rows.Add(g.Model, g.Profile, g.Tier, g.Count, CostFormatting.Money(g.Amounts.Input),
            CostFormatting.Money(g.Amounts.CacheRead), CostFormatting.Money(g.Amounts.CacheWrite), CostFormatting.Money(g.Amounts.Output), CostFormatting.Money(g.Amounts.Total));
        foreach (var c in result.Unpriced) _unpriced.Rows.Add(c.Call.Model ?? "未知模型", c.Tier, c.Reason ?? "未提供");
        _pricedTab.Text = "已计价明细  " + result.PricedCalls; _unpricedTab.Text = "未计价原因  " + result.Unpriced.Count;
        RefreshEmpty();
    }
    // Related usage is published separately from the main-session cost so a group read
    // never delays or replaces the numbers the strip already shows.
    public void SetGroup(SessionGroupSnapshot? snapshot)
    {
        if (ReferenceEquals(_group, snapshot)) return;
        _group = snapshot; _related.Rows.Clear();
        foreach (var member in snapshot?.Members ?? [])
            _related.Rows.Add(member.KindText, OverlayPresentationBuilder.ShortThreadId(member.ThreadId),
                member.Attribution ?? "—", RelatedUsageFormatting.Tokens(member.TotalTokens),
                member.Cost is { } cost ? CostFormatting.Money(cost) : "—", member.CoverageText);
        _relatedSummary.Text = RelatedUsageFormatting.Summary(snapshot) + " · 费用 " + RelatedUsageFormatting.CostSummary(snapshot);
        _relatedNotes.Text = snapshot is null || snapshot.Notes.Count == 0 ? "" : string.Join("；", snapshot.Notes);
        RefreshEmpty();
    }
}
