using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    private const int PageSize = 8;
    private readonly Store store;
    private readonly ListView list = new ListView();
    private readonly TextBox query = VaultLayout.Field("搜索标题或账号");
    private readonly Label page = VaultLayout.Caption("", 9);
    private readonly Label status = VaultLayout.Caption("", 9);
    private readonly Button previous = UiStyle.IconButton(UiStyle.ToolbarIcon.Previous, "上一页", size: 24);
    private readonly Button next = UiStyle.IconButton(UiStyle.ToolbarIcon.Next, "下一页", size: 24);
    private readonly ContextMenuStrip recordMenu = new ContextMenuStrip();
    private readonly ToolTip toolbarTips = new ToolTip();
    private readonly Timer searchDelay = new Timer { Interval = 150 };
    private readonly Timer clipboardExpiry = new Timer { Interval = 30000 };
    private TableLayoutPanel libraryPane;
    private TableLayoutPanel settingsPane;
    private Form dialog;
    private int offset;
    private bool closingSession;
    private uint copiedSequence;
    private Label copyStatus;
    private string copyMessage;
    private bool settingsVisible;

    internal event EventHandler LockRequested;
    internal event EventHandler<StorageMovedEventArgs> StorageMoved;
    internal bool HasOpenDialog { get { return dialog != null; } }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);

    internal MainForm(Store store)
    {
        this.store = store;
        VaultLayout.Configure(this, "资料库", new Size(290, 284));
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(260, 140);

        libraryPane = VaultLayout.Grid();
        libraryPane.Padding = new Padding(8, 4, 8, 4);
        Controls.Add(libraryPane);

        var toolbar = VaultLayout.Actions(4);
        query.Margin = new Padding(0, 2, 6, 2);
        query.HandleCreated += delegate {
            const int setCueBanner = 0x1501;
            SendMessage(query.Handle, setCueBanner, new IntPtr(1), "标题或账号");
        };
        toolbar.Controls.Add(query, 0, 0);
        var pin = UiStyle.IconButton(UiStyle.ToolbarIcon.Pin, "置顶窗口");
        var add = UiStyle.IconButton(UiStyle.ToolbarIcon.New, "新建");
        var lockButton = UiStyle.IconButton(UiStyle.ToolbarIcon.Lock, "锁定");
        var settingsButton = UiStyle.IconButton(UiStyle.ToolbarIcon.Settings, "设置");
        toolbarTips.SetToolTip(pin, pin.AccessibleName);
        toolbarTips.SetToolTip(add, "新建");
        toolbarTips.SetToolTip(lockButton, "锁定");
        toolbarTips.SetToolTip(settingsButton, "设置");
        toolbarTips.SetToolTip(previous, "上一页");
        toolbarTips.SetToolTip(next, "下一页");
        toolbar.Controls.Add(pin, 1, 0);
        toolbar.Controls.Add(add, 2, 0);
        toolbar.Controls.Add(lockButton, 3, 0);
        toolbar.Controls.Add(settingsButton, 4, 0);
        VaultLayout.Row(libraryPane, toolbar, 34);

        pin.Click += delegate {
            TopMost = !TopMost;
            UiStyle.SetIconActive(pin, TopMost);
            pin.AccessibleName = TopMost ? "取消置顶" : "置顶窗口";
            toolbarTips.SetToolTip(pin, pin.AccessibleName);
        };
        settingsButton.Click += delegate { OpenSettings(); };

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
        list.Margin = new Padding(0, 2, 0, 2);
        list.Resize += delegate {
            int usable = Math.Max(80, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            list.Columns[0].Width = (int)(usable * .55);
            list.Columns[1].Width = usable - list.Columns[0].Width;
        };
        VaultLayout.Row(libraryPane, list, 0);

        var pages = VaultLayout.Grid(3);
        pages.RowCount = 1;
        pages.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Visible = false;
        var footerCenter = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        footerCenter.Controls.Add(page);
        footerCenter.Controls.Add(status);
        status.TextChanged += delegate {
            bool hasStatus = status.Text.Length > 0;
            status.Visible = hasStatus;
            page.Visible = !hasStatus;
        };
        pages.Controls.Add(footerCenter, 0, 0);
        pages.Controls.Add(previous, 1, 0);
        pages.Controls.Add(next, 2, 0);
        VaultLayout.Row(libraryPane, pages, 28);

        recordMenu.Items.Add("复制账号", null, delegate { CopySelected(false); });
        recordMenu.Items.Add("复制密码", null, delegate { CopySelected(true); });
        recordMenu.Items.Add(new ToolStripSeparator());
        recordMenu.Items.Add("查看详情", null, delegate { OpenSelected(); });
        recordMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
            e.Cancel = SelectedId == 0;
        };
        list.ContextMenuStrip = recordMenu;
        list.MouseDown += delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Right) return;
            var item = list.GetItemAt(e.X, e.Y);
            if (item == null) list.SelectedItems.Clear();
            else { item.Selected = true; item.Focused = true; }
        };
        list.ItemActivate += delegate { OpenSelected(); };
        add.Click += delegate { Edit(new Note()); };
        lockButton.Click += delegate { RaiseLockRequested(); };
        query.TextChanged += delegate { searchDelay.Stop(); searchDelay.Start(); };
        searchDelay.Tick += delegate { searchDelay.Stop(); RefreshList(0, SelectedId); };
        clipboardExpiry.Tick += delegate { ClearOwnedClipboard(); };
        previous.Click += delegate { RefreshList(Math.Max(0, offset - PageSize)); };
        next.Click += delegate { RefreshList(offset + PageSize); };
        Shown += delegate {
            RefreshList(0);
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            query.Focus();
        };
    }

    private long SelectedId
    {
        get { return list.SelectedItems.Count == 0 ? 0 : (long)list.SelectedItems[0].Tag; }
    }

    private void RefreshList(int start, long selectedId = 0)
    {
        try
        {
            var summaries = store.Search(query.Text.Trim(), start, PageSize + 1);
            if (summaries.Count == 0 && start > 0) { RefreshList(0); return; }
            offset = start;
            ListViewItem selected = null;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                for (int i = 0; i < Math.Min(summaries.Count, PageSize); i++)
                {
                    Note note = summaries[i];
                    var item = new ListViewItem(note.Title) { Tag = note.Id };
                    item.SubItems.Add(note.Username ?? "");
                    list.Items.Add(item);
                    if (selectedId == note.Id)
                    {
                        item.Selected = item.Focused = true;
                        selected = item;
                    }
                }
            }
            finally { list.EndUpdate(); }
            if (selected != null) selected.EnsureVisible();
            previous.Enabled = offset > 0;
            next.Enabled = summaries.Count > PageSize;
            page.Text = summaries.Count == 0 ? "无记录" : "第 " + (1 + offset / PageSize) + " 页";
            status.Text = "";
        }
        catch (Exception ex) { Report("读取列表失败", ex); }
    }

    private void OpenSelected()
    {
        long id = SelectedId;
        if (id == 0 || HasOpenDialog) return;
        Note note;
        try { note = GetRecord(id); }
        catch (Exception ex) { Report("打开失败", ex); return; }
        Edit(note);
    }

    private Note GetRecord(long id)
    {
        var note = store.Get(id);
        if (note == null) throw new InvalidOperationException("这条记录已经删除。");
        return note;
    }

    private void Edit(Note note)
    {
        if (HasOpenDialog) return;
        using (var editor = new NoteEditor(store, note, CopyValue))
        {
            ShowOwnedDialog(editor);
            if (editor.Deleted)
            {
                RefreshList(offset);
                status.Text = "记录已删除";
            }
            else if (editor.Saved)
            {
                RefreshList(0, note.Id);
                status.Text = "已保存";
            }
        }
    }

    private void ChangePassword()
    {
        if (searchDelay.Enabled)
        {
            searchDelay.Stop();
            RefreshList(0, SelectedId);
        }
        using (var change = new UnlockForm(null, store))
            if (ShowOwnedDialog(change) == DialogResult.OK) status.Text = "主密码已更改";
    }

    private void OpenSettings()
    {
        if (HasOpenDialog || settingsVisible) return;
        settingsVisible = true;
        Text = "密笺 · 设置";
        libraryPane.Visible = false;
        settingsPane = BuildSettingsPane();
        Controls.Add(settingsPane);
        settingsPane.BringToFront();
        settingsPane.Focus();
    }

    private TableLayoutPanel BuildSettingsPane()
    {
        var pane = VaultLayout.Grid();
        pane.Padding = new Padding(12, 6, 12, 8);
        var header = VaultLayout.Grid();
        var back = UiStyle.IconButton(UiStyle.ToolbarIcon.Previous, "返回", size: 26);
        back.Anchor = AnchorStyles.Left;
        back.Margin = Padding.Empty;
        toolbarTips.SetToolTip(back, "返回");
        back.Click += delegate { CloseSettings(); };
        header.Controls.Add(back, 0, 0);
        VaultLayout.Row(pane, header, 38);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var content = VaultLayout.Grid();
        content.Dock = DockStyle.Top;
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content);
        VaultLayout.Row(pane, scroll, 0);

        var autoStart = UiStyle.ActionButton("不可用");
        autoStart.AccessibleName = "开机自启动";
        try { UiStyle.SetToggleState(autoStart, StartupSettings.Enabled); }
        catch { autoStart.Enabled = false; }
        autoStart.Click += delegate {
            try
            {
                bool enabled = !StartupSettings.Enabled;
                StartupSettings.Enabled = enabled;
                UiStyle.SetToggleState(autoStart, enabled);
            }
            catch (Exception ex) { Report("设置失败", ex); }
        };
        VaultLayout.Row(content, SettingsRow("开机自启动", autoStart), 46);
        VaultLayout.Row(content, new Panel { Margin = Padding.Empty }, 5);

        var change = UiStyle.ActionButton("更改");
        change.AccessibleName = "更改主密码";
        change.Click += delegate { ChangePassword(); };
        VaultLayout.Row(content, SettingsRow("主密码", change), 46);
        VaultLayout.Row(content, new Panel { Margin = Padding.Empty }, 5);

        var path = VaultLayout.Caption(store.FilePath, 9);
        path.AccessibleName = "当前存储位置";
        path.ForeColor = Color.FromArgb(59, 78, 70);
        path.Margin = new Padding(8, 0, 4, 0);
        toolbarTips.SetToolTip(path, store.FilePath);
        var move = UiStyle.ActionButton("更改");
        move.AccessibleName = "更换存储位置";
        move.Click += delegate {
            using (var folder = new FolderBrowserDialog {
                Description = "选择新的资料库存储文件夹",
                SelectedPath = Path.GetDirectoryName(store.FilePath)
            })
            {
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                string target = Path.Combine(folder.SelectedPath, Path.GetFileName(store.FilePath));
                if (String.Equals(target, store.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    Report("更换存储位置", new IOException("新位置必须与当前资料库不同。"));
                    return;
                }
                if (MessageBox.Show(this, "资料库会先复制并校验到新位置。旧资料库会保留，待新位置重新打开后再询问是否删除。\n\n" + target,
                    "确认更换存储位置", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                try
                {
                    if (MoveStorage(target) && StorageMoved != null)
                        StorageMoved(this, new StorageMovedEventArgs(target));
                }
                catch (Exception ex) { Report("迁移失败", ex); }
            }
        };
        VaultLayout.Row(content, SettingsRow("存储位置", move, path), 46);
        return pane;
    }

    private static TableLayoutPanel SettingsRow(string title, Control action, Label detail = null)
    {
        var row = VaultLayout.Grid(detail == null ? 2 : 3);
        row.BackColor = Color.White;
        row.Padding = new Padding(10, 0, 8, 0);
        var label = VaultLayout.Caption(title, 10, true);
        if (detail != null)
        {
            label.AutoSize = true;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(detail, 1, 0);
        }
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(action, row.ColumnCount - 1, 0);
        return row;
    }

    private void CloseSettings()
    {
        if (!settingsVisible) return;
        settingsVisible = false;
        if (settingsPane != null)
        {
            Controls.Remove(settingsPane);
            settingsPane.Dispose();
            settingsPane = null;
        }
        Text = "密笺 · 资料库";
        libraryPane.Visible = true;
        libraryPane.BringToFront();
        RefreshList(offset, SelectedId);
    }

    private bool MoveStorage(string targetPath)
    {
        using (var prompt = new SecretPromptForm("验证主密码"))
        {
            if (prompt.ShowDialog(dialog ?? this) != DialogResult.OK) return false;
            Store.CloneVerified(store.FilePath, targetPath, prompt.Password);
            return true;
        }
    }

    private DialogResult ShowOwnedDialog(Form form)
    {
        Form owner = dialog ?? this;
        Form previous = dialog;
        dialog = form;
        try { return form.ShowDialog(owner); }
        finally { dialog = previous; }
    }

    private void CopySelected(bool secret)
    {
        long id = SelectedId;
        if (id == 0) return;
        try
        {
            string value = secret ? GetRecord(id).Password : list.SelectedItems[0].SubItems[1].Text;
            CopyValue(value, secret, status);
        }
        catch (Exception ex) { Report("复制失败", ex); }
    }

    private void CopyValue(string value, bool secret, Label target)
    {
        if (String.IsNullOrEmpty(value)) { target.Text = secret ? "密码为空" : "账号为空"; return; }
        try
        {
            Clipboard.SetText(value);
            copiedSequence = GetClipboardSequenceNumber();
            clipboardExpiry.Stop();
            ClearCopyStatus();
            copyStatus = target;
            copyMessage = secret ? "密码已复制" : "账号已复制";
            target.Text = copyMessage;
            clipboardExpiry.Start();
        }
        catch (ExternalException) { target.Text = "剪贴板正被其他程序使用"; }
    }

    private void ClearCopyStatus()
    {
        if (copyStatus != null && !copyStatus.IsDisposed && copyStatus.Text == copyMessage)
            copyStatus.Text = "";
        copyStatus = null;
        copyMessage = null;
    }

    private void ClearOwnedClipboard()
    {
        clipboardExpiry.Stop();
        if (copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence)
        {
            try { Clipboard.Clear(); }
            catch (ExternalException) { clipboardExpiry.Start(); return; }
        }
        copiedSequence = 0;
        ClearCopyStatus();
    }

    internal void RestoreWindow()
    {
        WindowVisibility.EnsureShown(this);
        Form target = dialog ?? this;
        if (dialog != null) WindowVisibility.EnsureShown(dialog);
        target.Activate();
    }

    internal void CloseSession()
    {
        closingSession = true;
        try { Close(); }
        finally { closingSession = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !closingSession)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
        if (!e.Cancel) ClearOwnedClipboard();
    }

    private void RaiseLockRequested()
    {
        if (LockRequested != null) LockRequested(this, EventArgs.Empty);
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
            case Keys.Control | Keys.N: Edit(new Note()); return true;
            case Keys.Control | Keys.F: query.Focus(); query.SelectAll(); return true;
            case Keys.Control | Keys.L: RaiseLockRequested(); return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            searchDelay.Dispose();
            clipboardExpiry.Dispose();
            recordMenu.Dispose();
            toolbarTips.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class StorageMovedEventArgs : EventArgs
{
    internal readonly string NewPath;
    internal StorageMovedEventArgs(string newPath) { NewPath = newPath; }
}

internal sealed class SecretPromptForm : Form
{
    private readonly TextBox password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    internal string Password { get { return password.Text; } }

    internal SecretPromptForm(string title)
    {
        Text = title;
        Icon = UiStyle.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 150);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3, ColumnCount = 1 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.Controls.Add(new Label { Text = "请输入当前主密码以验证迁移操作。", Dock = DockStyle.Fill }, 0, 0);
        grid.Controls.Add(password, 0, 1);
        var ok = UiStyle.ActionButton("继续", true);
        ok.DialogResult = DialogResult.OK;
        var cancel = UiStyle.ActionButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, 2);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += delegate { password.Focus(); };
    }
}

internal sealed class NoteEditor : Form
{
    private readonly Store store;
    private readonly Note note;
    private readonly Action<string, bool, Label> copyValue;
    private readonly TextBox title = VaultLayout.Field("标题");
    private readonly TextBox username = VaultLayout.Field("账号");
    private readonly TextBox password = VaultLayout.Field("密码");
    private readonly TextBox body = VaultLayout.Field("便签正文");
    private readonly Label status = VaultLayout.Caption("", 9);
    private bool dirty;
    internal bool Saved { get; private set; }
    internal bool Deleted { get; private set; }

    internal NoteEditor(Store store, Note note, Action<string, bool, Label> copyValue)
    {
        this.store = store;
        this.note = note;
        this.copyValue = copyValue;
        string caption = note.Id == 0 ? "新建记录" : "记录详情";
        VaultLayout.Configure(this, caption, new Size(500, 498));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(395, 455);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        var pane = VaultLayout.Grid();
        pane.Padding = new Padding(20, 12, 20, 12);
        Controls.Add(pane);
        VaultLayout.Row(pane, VaultLayout.Caption(caption, 15, true), 43);
        VaultLayout.Row(pane, VaultLayout.Caption("标题", 10, true), 23);
        VaultLayout.Row(pane, title, 34);
        VaultLayout.Row(pane, VaultLayout.Caption("账号", 10, true), 23);
        VaultLayout.Row(pane, FieldRow(username, false), 37);
        VaultLayout.Row(pane, VaultLayout.Caption("密码", 10, true), 23);
        password.UseSystemPasswordChar = true;
        VaultLayout.Row(pane, FieldRow(password, true), 37);
        VaultLayout.Row(pane, VaultLayout.Caption("便签正文", 10, true), 27);
        body.Multiline = true;
        body.AcceptsReturn = true;
        body.ScrollBars = ScrollBars.Vertical;
        body.Dock = DockStyle.Fill;
        VaultLayout.Row(pane, body, 0);

        var footer = VaultLayout.Actions(2);
        var save = UiStyle.ActionButton("保存", true);
        save.Click += delegate { if (SaveNote()) Close(); };
        var remove = UiStyle.ActionButton("删除");
        remove.Enabled = note.Id != 0;
        remove.Click += delegate { DeleteNote(); };
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(remove, 1, 0);
        footer.Controls.Add(save, 2, 0);
        VaultLayout.Row(pane, footer, 45);

        title.Text = note.Title ?? "";
        username.Text = note.Username ?? "";
        password.Text = note.Password ?? "";
        body.Text = note.Body ?? "";
        foreach (var field in new[] { title, username, password, body })
        {
            field.ClearUndo();
            field.TextChanged += delegate { dirty = true; status.Text = "有未保存的修改"; };
        }
        Shown += delegate { title.Focus(); };
        UiStyle.FadeIn(this);
    }

    private Control FieldRow(TextBox field, bool secret)
    {
        var row = VaultLayout.Actions(secret ? 2 : 1);
        row.Controls.Add(field, 0, 0);
        if (secret)
        {
            var reveal = UiStyle.ActionButton("显示");
            reveal.Click += delegate {
                password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
                reveal.Text = password.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            row.Controls.Add(reveal, 1, 0);
        }
        var copy = UiStyle.ActionButton("复制");
        copy.AccessibleName = secret ? "复制密码" : "复制账号";
        copy.Click += delegate { copyValue(field.Text, secret, status); };
        row.Controls.Add(copy, row.ColumnCount - 1, 0);
        return row;
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
            Id = note.Id, Revision = note.Revision, Title = title.Text.Trim(),
            Username = username.Text, Password = password.Text, Body = body.Text
        };
        try
        {
            store.Save(updated);
            note.Id = updated.Id;
            dirty = false;
            Saved = true;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败，修改仍在编辑区", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void DeleteNote()
    {
        if (MessageBox.Show(this, "永久删除当前记录及其未保存的修改？", "删除记录",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            store.Delete(note.Id, note.Revision);
            dirty = false;
            Deleted = true;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (dirty)
        {
            var answer = AskSave();
            e.Cancel = answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveNote());
        }
        base.OnFormClosing(e);
    }

    private DialogResult AskSave()
    {
        using (var prompt = new Form {
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false, ShowInTaskbar = false
        })
        {
            VaultLayout.Configure(prompt, "未保存的修改", new Size(342, 115));
            var pane = VaultLayout.Grid();
            pane.Padding = new Padding(15, 8, 15, 8);
            VaultLayout.Row(pane, VaultLayout.Caption("保存对当前记录的修改吗？", 10), 0);
            var actions = VaultLayout.Actions(3);
            var cancel = UiStyle.ActionButton("取消");
            cancel.DialogResult = DialogResult.Cancel;
            var discard = UiStyle.ActionButton("放弃");
            discard.DialogResult = DialogResult.No;
            var save = UiStyle.ActionButton("保存", true);
            save.DialogResult = DialogResult.Yes;
            actions.Controls.Add(cancel, 1, 0);
            actions.Controls.Add(discard, 2, 0);
            actions.Controls.Add(save, 3, 0);
            VaultLayout.Row(pane, actions, 40);
            prompt.AcceptButton = save;
            prompt.CancelButton = cancel;
            prompt.Controls.Add(pane);
            return prompt.ShowDialog(this);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        if (keys == (Keys.Control | Keys.S)) { if (SaveNote()) Close(); return true; }
        if (keys == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }
}

internal static class VaultLayout
{
    internal static readonly Color Ink = Color.FromArgb(29, 57, 48);
    internal static readonly Color Paper = Color.FromArgb(249, 250, 247);

    internal static void Configure(Form form, string title, Size size)
    {
        form.Text = "密笺 · " + title;
        form.Icon = UiStyle.AppIcon;
        form.Font = new Font("Microsoft YaHei UI", 10);
        form.ForeColor = Ink;
        form.BackColor = Paper;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ClientSize = size;
    }

    internal static TableLayoutPanel Grid(int columns = 1)
    {
        return new TableLayoutPanel {
            ColumnCount = columns, Margin = Padding.Empty, Dock = DockStyle.Fill
        };
    }

    internal static TableLayoutPanel Actions(int actions)
    {
        var row = Grid(actions + 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < actions; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return row;
    }

    internal static Label Caption(string text, int size, bool bold = false)
    {
        return new Label {
            Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, AutoEllipsis = true
        };
    }

    internal static TextBox Field(string name)
    {
        return new TextBox {
            AccessibleName = name, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            MaxLength = 0, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 2)
        };
    }

    internal static void Row(TableLayoutPanel grid, Control control, float height)
    {
        int index = grid.RowStyles.Count;
        grid.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(control, 0, index);
    }
}
