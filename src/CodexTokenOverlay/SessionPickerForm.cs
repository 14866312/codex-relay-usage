namespace CodexTokenOverlay;

internal sealed class SessionPickerForm : ModernDialog
{
    private readonly IReadOnlyList<SessionEntry> _sessions;
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "搜索会话标题、ID 或工作目录" };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    public string? SelectedThreadId { get; private set; }
    public SessionPickerForm(IReadOnlyList<SessionEntry> sessions, string? bindingTitle = null) : base(bindingTitle is null ? "选择会话" : "绑定当前对话")
    {
        _sessions = sessions; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(850, 520); MinimumSize = new Size(620, 340); Font = new Font("Microsoft YaHei UI", 9);
        _list.Columns.Add("标题 / ID", 320); _list.Columns.Add("最后写入", 190); _list.Columns.Add("工作目录", 280);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        _list.BorderStyle = BorderStyle.None;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += (_, e) =>
        {
            using var background = new SolidBrush(Palette.InputSurface); e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", _list.Font, Rectangle.Inflate(e.Bounds, -8, 0), Palette.Label,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var divider = new Pen(Palette.Divider); e.Graphics.DrawLine(divider, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        var cancel = new ModernButton { Text = "取消", DialogResult = DialogResult.Cancel, Kind = ButtonKind.Secondary };
        var choose = new ModernButton { Text = bindingTitle is null ? "选择并锁定" : "绑定并跟随", Width = 156, Kind = ButtonKind.Primary };
        choose.Click += (_, _) => Choose(); _list.DoubleClick += (_, _) => Choose();
        buttons.Controls.Add(cancel); buttons.Controls.Add(choose);
        Body.Controls.Add(_list); Body.Controls.Add(new FieldFrame(_filter) { Dock = DockStyle.Top, Height = 40 }); Body.Controls.Add(buttons);
        if (bindingTitle is not null)
            Body.Controls.Add(new Label { Text = "当前页面：" + bindingTitle + Environment.NewLine + "仅绑定此侧栏条目，切换对话后继续自动跟随。",
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(4), AutoEllipsis = true });
        AcceptButton = choose; CancelButton = cancel;
        _filter.TextChanged += (_, _) => Populate(); Populate();
        ApplyTheme(Palette);
    }
    private void Populate()
    {
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var session in _sessions)
        {
            var search = $"{session.Title} {session.ThreadId} {session.Cwd}";
            if (!search.Contains(_filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(session.Title is null ? session.ThreadId : session.Title + " · " + session.ThreadId) { Tag = session.ThreadId };
            item.SubItems.Add(session.WriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(session.Cwd ?? "未提供"); _list.Items.Add(item);
        }
        _list.EndUpdate();
    }
    private void Choose()
    {
        if (_list.SelectedItems.Count != 1) return;
        SelectedThreadId = _list.SelectedItems[0].Tag as string; DialogResult = DialogResult.OK; Close();
    }
}
