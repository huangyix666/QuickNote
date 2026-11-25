using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    private const int PageSize = 100;
    private static readonly Color Ink = Color.FromArgb(29, 57, 48);
    private static readonly Color Accent = Color.FromArgb(38, 87, 71);
    private static readonly Color Paper = Color.FromArgb(249, 250, 247);
    private readonly Store store;
    private readonly ListView list = new ListView();
    private readonly TextBox query = Field("搜索标题或账号");
    private readonly TextBox title = Field("标题");
    private readonly TextBox username = Field("账号");
    private readonly TextBox password = Field("密码");
    private readonly TextBox body = Field("便签正文");
    private readonly Label heading = Caption("", 16, true);
    private readonly Label page = Caption("", 9, false);
    private readonly Label status = Caption("", 9, false);
    private readonly Button previous = ActionButton("上一页");
    private readonly Button next = ActionButton("下一页");
    private readonly Button remove = ActionButton("删除");
    private readonly Button reveal = ActionButton("显示");
    private readonly Timer searchDelay = new Timer { Interval = 150 };
    private readonly Timer clipboardExpiry = new Timer { Interval = 30000 };
    private Note current = new Note();
    private int offset;
    private bool loading, syncingList, dirty, lockRequested;
    private uint copiedSequence;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    internal MainForm(Store store)
    {
        this.store = store;
        Text = "密笺 · 资料库";
        Icon = SystemIcons.Shield;
        Font = new Font("Microsoft YaHei UI", 10);
        ForeColor = Ink;
        BackColor = Paper;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(750, 570);
        ClientSize = new Size(986, 662);

        var layout = Grid();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        var bar = Grid(2);
        bar.BackColor = Ink;
        bar.Padding = new Padding(18, 6, 16, 6);
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var brand = Caption("密笺  /  资料库", 15, true);
        brand.ForeColor = Color.White;
        bar.Controls.Add(brand, 0, 0);
        var lockButton = ActionButton("锁定");
        bar.Controls.Add(lockButton, 1, 0);
        layout.Controls.Add(bar, 0, 0);

        var workspace = Grid(2);
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        layout.Controls.Add(workspace, 0, 1);
        workspace.Controls.Add(BuildLibrary(), 0, 0);
        workspace.Controls.Add(BuildEditor(), 1, 0);
        lockButton.Click += delegate { LockVault(); };
        query.TextChanged += delegate { searchDelay.Stop(); searchDelay.Start(); };
        searchDelay.Tick += delegate { searchDelay.Stop(); RefreshList(0); };
        clipboardExpiry.Tick += delegate { ClearOwnedClipboard(); };
        previous.Click += delegate { RefreshList(Math.Max(0, offset - PageSize)); };
        next.Click += delegate { RefreshList(offset + PageSize); };
        list.SelectedIndexChanged += delegate { SelectRecord(); };
        foreach (TextBox field in new[] { title, username, password, body })
            field.TextChanged += delegate {
                if (!loading) { dirty = true; status.Text = "有未保存的修改"; }
            };
        FormClosing += OnClosing;
        Shown += delegate {
            Display(new Note());
            RefreshList(0);
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            else title.Focus();
        };
    }

    private static TableLayoutPanel Grid(int columns = 1)
    {
        return new TableLayoutPanel {
            ColumnCount = columns, Margin = Padding.Empty, Dock = DockStyle.Fill
        };
    }

    private static Label Caption(string text, int size, bool bold)
    {
        return new Label {
            Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, AutoEllipsis = true
        };
    }

    private static TextBox Field(string name)
    {
        return new TextBox {
            AccessibleName = name, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            MaxLength = 0, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 2)
        };
    }

    private static Button ActionButton(string text, bool primary = false)
    {
        var result = new Button {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(9, 2, 9, 2), Anchor = AnchorStyles.None, FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Color.White : Ink,
            BackColor = primary ? Accent : Color.FromArgb(236, 241, 235), UseVisualStyleBackColor = false
        };
        result.FlatAppearance.BorderSize = 0;
        return result;
    }

    private static void Row(TableLayoutPanel grid, Control control, float height)
    {
        int index = grid.RowStyles.Count;
        grid.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(control, 0, index);
    }

    private Control BuildLibrary()
    {
        var pane = Grid();
        pane.BackColor = Color.FromArgb(240, 244, 239);
        pane.Padding = new Padding(16, 12, 16, 12);
        var top = Grid(2);
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(Caption("我的记录", 13, true), 0, 0);
        var add = ActionButton("新建", true);
        add.Click += delegate { NewNote(); };
        top.Controls.Add(add, 1, 0);
        Row(pane, top, 48);
        Row(pane, Caption("搜索标题或账号", 9, false), 23);
        Row(pane, query, 38);
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.AccessibleName = "记录列表";
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.Columns.Add("标题", 128);
        list.Columns.Add("账号", 104);
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Margin = new Padding(0, 5, 0, 2);
        list.Resize += delegate {
            int usable = Math.Max(80, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            list.Columns[0].Width = (int)(usable * .55);
            list.Columns[1].Width = usable - list.Columns[0].Width;
        };
        Row(pane, list, 0);
        var pages = Grid(3);
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        page.TextAlign = ContentAlignment.MiddleCenter;
        pages.Controls.Add(previous, 0, 0);
        pages.Controls.Add(page, 1, 0);
        pages.Controls.Add(next, 2, 0);
        Row(pane, pages, 46);
        return pane;
    }

    private Control BuildEditor()
    {
        var pane = Grid();
        pane.Padding = new Padding(22, 12, 22, 12);
        Row(pane, heading, 48);
        Row(pane, Caption("标题", 10, true), 23);
        Row(pane, title, 36);
        Row(pane, Caption("账号", 10, true), 23);
        Row(pane, FieldRow(username), 38);
        Row(pane, Caption("密码", 10, true), 23);
        password.UseSystemPasswordChar = true;
        Row(pane, FieldRow(password), 38);
        Row(pane, Caption("便签正文", 10, true), 28);
        body.Multiline = true;
        body.AcceptsReturn = true;
        body.ScrollBars = ScrollBars.Vertical;
        body.Dock = DockStyle.Fill;
        Row(pane, body, 0);
        var footer = Grid(3);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var save = ActionButton("保存", true);
        save.Click += delegate { if (SaveNote()) RefreshList(0); };
        remove.Click += delegate { DeleteNote(); };
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(remove, 1, 0);
        footer.Controls.Add(save, 2, 0);
        Row(pane, footer, 46);
        return pane;
    }

    private Control FieldRow(TextBox field)
    {
        bool secret = field == password;
        var row = Grid(secret ? 3 : 2);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(field, 0, 0);
        if (secret)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(reveal, 1, 0);
            reveal.Click += delegate {
                password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
                reveal.Text = password.UseSystemPasswordChar ? "显示" : "隐藏";
            };
        }
        var copy = ActionButton("复制");
        copy.AccessibleName = secret ? "复制密码" : "复制账号";
        copy.Click += delegate { CopyField(field); };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(copy, row.ColumnCount - 1, 0);
        return row;
    }

    private void RefreshList(int start)
    {
        try
        {
            var summaries = store.Search(query.Text.Trim(), start, PageSize + 1);
            if (summaries.Count == 0 && start > 0) { RefreshList(0); return; }
            offset = start;
            syncingList = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                for (int i = 0; i < Math.Min(summaries.Count, PageSize); i++)
                {
                    Note n = summaries[i];
                    var item = new ListViewItem(n.Title) { Tag = n.Id };
                    item.SubItems.Add(n.Username ?? "");
                    list.Items.Add(item);
                    if (current.Id == n.Id) item.Selected = true;
                }
            }
            finally { list.EndUpdate(); syncingList = false; }
            previous.Enabled = offset > 0;
            next.Enabled = summaries.Count > PageSize;
            page.Text = summaries.Count == 0 ? "无记录" : "第 " + (1 + offset / PageSize) + " 页";
        }
        catch (Exception ex) { Report("读取列表失败", ex); }
    }

    private void SelectRecord()
    {
        if (syncingList || list.SelectedItems.Count == 0) return;
        long id = (long)list.SelectedItems[0].Tag;
        if (current.Id == id) return;
        bool wasDirty = dirty;
        if (!MayLeave()) { RestoreSelection(); return; }
        bool savedPrevious = wasDirty && !dirty;
        try
        {
            Note record = store.Get(id);
            if (record == null) throw new InvalidOperationException("这条记录已经删除。");
            Display(record);
            if (savedPrevious) RefreshList(offset);
        }
        catch (Exception ex) { RestoreSelection(); Report("打开失败", ex); }
    }

    private void RestoreSelection()
    {
        syncingList = true;
        try {
            foreach (ListViewItem item in list.Items)
                item.Selected = (long)item.Tag == current.Id;
        }
        finally { syncingList = false; }
    }

    private void Display(Note note)
    {
        loading = true;
        current = note;
        try
        {
            password.UseSystemPasswordChar = true;
            reveal.Text = "显示";
            title.Text = note.Title ?? "";
            username.Text = note.Username ?? "";
            password.Text = note.Password ?? "";
            body.Text = note.Body ?? "";
            foreach (TextBox field in new[] { title, username, password, body }) field.ClearUndo();
            heading.Text = note.Id == 0 ? "新建记录" : "记录详情";
            remove.Enabled = note.Id != 0;
            dirty = false;
            status.Text = "";
        }
        finally { loading = false; }
    }

    private bool MayLeave()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "保存对当前记录的修改吗？", "未保存的修改",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        return answer == DialogResult.No || (answer == DialogResult.Yes && SaveNote());
    }

    private bool SaveNote()
    {
        if (String.IsNullOrWhiteSpace(title.Text))
        {
            status.Text = "请先填写标题";
            title.Focus();
            return false;
        }
        var updated = new Note {
            Id = current.Id, Revision = current.Revision, Title = title.Text.Trim(),
            Username = username.Text, Password = password.Text, Body = body.Text
        };
        try
        {
            store.Save(updated);
            Display(updated);
            status.Text = "已保存";
            return true;
        }
        catch (Exception ex) { Report("保存失败，修改仍在编辑区", ex); return false; }
    }

    private void NewNote()
    {
        if (!MayLeave()) return;
        Display(new Note());
        RefreshList(0);
        title.Focus();
    }

    private void DeleteNote()
    {
        if (current.Id == 0) return;
        if (MessageBox.Show(this, "永久删除当前记录及其未保存的修改？", "删除记录",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            store.Delete(current.Id, current.Revision);
            Display(new Note());
            RefreshList(offset);
            status.Text = "记录已删除";
        }
        catch (Exception ex) { Report("删除失败", ex); }
    }

    private void CopyField(TextBox field)
    {
        if (field.TextLength == 0) { status.Text = "字段为空"; return; }
        try
        {
            Clipboard.SetText(field.Text);
            copiedSequence = GetClipboardSequenceNumber();
            clipboardExpiry.Stop();
            clipboardExpiry.Start();
            status.Text = "已复制，30 秒后清除";
        }
        catch (ExternalException) { status.Text = "剪贴板正被其他程序使用"; }
    }

    private void ClearOwnedClipboard()
    {
        clipboardExpiry.Stop();
        if (copiedSequence == 0 || GetClipboardSequenceNumber() != copiedSequence) return;
        try { Clipboard.Clear(); copiedSequence = 0; }
        catch (ExternalException) { clipboardExpiry.Start(); }
    }

    private void LockVault()
    {
        lockRequested = true;
        Close();
    }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (!MayLeave()) { e.Cancel = true; lockRequested = false; DialogResult = DialogResult.None; return; }
        ClearOwnedClipboard();
        if (lockRequested) DialogResult = DialogResult.Retry;
    }

    private void Report(string caption, Exception ex)
    {
        status.Text = caption;
        MessageBox.Show(this, ex.Message, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        switch (keys)
        {
            case Keys.Control | Keys.N: NewNote(); return true;
            case Keys.Control | Keys.F: query.Focus(); query.SelectAll(); return true;
            case Keys.Control | Keys.S: if (SaveNote()) RefreshList(0); return true;
            case Keys.Control | Keys.L: LockVault(); return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { searchDelay.Dispose(); clipboardExpiry.Dispose(); }
        base.Dispose(disposing);
    }
}
