namespace CodexTokenOverlay;

internal sealed class SessionPickerForm : Form
{
    private readonly IReadOnlyList<SessionEntry> _sessions;
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "搜索会话标题、ID 或工作目录" };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    public string? SelectedThreadId { get; private set; }
    public SessionPickerForm(IReadOnlyList<SessionEntry> sessions)
    {
        _sessions = sessions; Text = "选择会话（选择后锁定）"; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(850, 520); MinimumSize = new Size(620, 340); Font = new Font("Microsoft YaHei UI", 9);
        _list.Columns.Add("标题 / ID", 340); _list.Columns.Add("最后写入", 150); _list.Columns.Add("工作目录", 300);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        var choose = new Button { Text = "选择并锁定", Width = 110 };
        choose.Click += (_, _) => Choose(); _list.DoubleClick += (_, _) => Choose();
        buttons.Controls.Add(cancel); buttons.Controls.Add(choose);
        Controls.Add(_list); Controls.Add(_filter); Controls.Add(buttons);
        AcceptButton = choose; CancelButton = cancel;
        _filter.TextChanged += (_, _) => Populate(); Populate();
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
