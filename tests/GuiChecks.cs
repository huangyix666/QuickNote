using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class GuiChecks
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    private enum Step
    {
        StartInTray, Setup, AddCredential, SaveCredential, VerifyCredential, SaveNote, Search, OpenDetails,
        CheckDetails, CancelDiscard, ConfirmDiscard, Discard, Hide, CheckRepeatLaunch, ChangePassword,
        SavePassword, Lock, UnlockFromTray, CheckUnlockRestore, EnterPassword, SeedPages,
        VerifyPages, SavePagedRecord, VerifyPagedSave, ShowExitPrompt, ExitWithPrompt, Done
    }

    private static IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Children(child)) yield return descendant;
        }
    }

    private static TextBox Field(Form form, string name)
    {
        return Children(form).OfType<TextBox>().Single(c => c.AccessibleName == name);
    }

    private static Button ActionButton(Form form, string name, bool iconOnly = false)
    {
        return Children(form).OfType<Button>().Single(candidate =>
            iconOnly ? candidate.AccessibleName == name && candidate.Text == "" : candidate.Text == name);
    }

    private static void Click(Form form, string name, bool iconOnly = false)
    {
        var button = ActionButton(form, name, iconOnly);
        if (!button.Visible || !button.Enabled) throw new Exception(name + " unavailable");
        button.PerformClick();
    }

    private static void CheckCompactLibrary(Form form)
    {
        if (Children(form).Any(control => control.Visible && control.Text == "我的记录"))
            throw new Exception("The former records heading is still visible");
        if (Children(form).OfType<Button>().Any(button => button.Text == "复制账号" || button.Text == "复制密码"))
            throw new Exception("Standalone copy buttons are still present in the library");
        if (form.CurrentAutoScaleDimensions == new SizeF(96, 96) && form.ClientSize.Width != 290)
            throw new Exception("The library does not open at 290 pixels wide at 100% scaling");

        var toolbar = new[] { "新建", "锁定", "设置" }.Select(name => ActionButton(form, name, true)).ToArray();
        // Compare resting colors so the live mouse position cannot influence the result.
        var normal = toolbar[0].GetType().GetField("normal", InstanceMembers);
        if (toolbar.Any(button => !button.Visible || !button.Enabled || button.ForeColor != toolbar[0].ForeColor ||
                                   !Equals(normal.GetValue(button), normal.GetValue(toolbar[0]))))
            throw new Exception("Toolbar icons do not share the same style");
    }

    private static void CheckPager(Form form, bool canGoBack, bool canGoForward)
    {
        var previous = ActionButton(form, "上一页", true);
        var next = ActionButton(form, "下一页", true);
        if (!previous.Visible || !next.Visible || previous.Enabled != canGoBack || next.Enabled != canGoForward)
            throw new Exception("Pagination arrow visibility or enabled state is wrong");
        var footerLabel = Children(previous.Parent).OfType<Label>().Single(label => label.Visible);
        foreach (var control in new Control[] { previous, next, footerLabel })
            if (!control.Parent.ClientRectangle.Contains(control.Bounds))
                throw new Exception("A pagination footer control is clipped");
    }

    private static void AddPageFixtures(Form form)
    {
        // Add fixtures only to the temporary vault already opened by this test.
        var store = form.GetType().GetField("store", InstanceMembers).GetValue(form);
        var noteType = form.GetType().Assembly.GetType("Note", true);
        var title = noteType.GetField("Title", InstanceMembers);
        var save = store.GetType().GetMethod("Save", InstanceMembers);
        for (int i = 1; i <= 17; i++)
        {
            var note = Activator.CreateInstance(noteType, true);
            title.SetValue(note, "分页测试 " + i.ToString("D3"));
            save.Invoke(store, new[] { note });
        }
    }

    private static void CheckPageRecords(ListView list, int newest, int oldest)
    {
        if (list.Items.Count != newest - oldest + 1)
            throw new Exception("Expected records " + newest + " through " + oldest + ", but found " + list.Items.Count + " rows");
        for (int i = 0; i < list.Items.Count; i++)
            if (list.Items[i].Text != "分页测试 " + (newest - i).ToString("D3"))
                throw new Exception("The page does not show the expected records in order");
    }

    private static int CheckPageNavigation(Form form)
    {
        var list = Records(form);
        CheckPageRecords(list, 17, 10);
        CheckPager(form, false, true);
        if (list.GetItemRect(7).Bottom > list.ClientSize.Height)
            throw new Exception("The default window height clips the eighth record");
        int fullPageHeight = form.ClientSize.Height;

        Click(form, "下一页", true);
        CheckPageRecords(list, 9, 2);
        CheckPager(form, true, true);
        if (form.ClientSize.Height != fullPageHeight)
            throw new Exception("The second full page changed the window height");

        Click(form, "下一页", true);
        CheckPageRecords(list, 1, 1);
        CheckPager(form, true, false);
        if (form.ClientSize.Height != fullPageHeight)
            throw new Exception("The last page changed the window height");

        Click(form, "上一页", true);
        CheckPageRecords(list, 9, 2);
        CheckPager(form, true, true);
        if (form.ClientSize.Height != fullPageHeight)
            throw new Exception("Returning to a full page changed the window height");
        return fullPageHeight;
    }

    private static void OpenSelectedDetails(ListView list)
    {
        typeof(ListView).GetMethod("OnItemActivate", InstanceMembers).Invoke(list, new object[] { EventArgs.Empty });
    }

    private static ListView Records(Form form)
    {
        return Children(form).OfType<ListView>().Single(c => c.AccessibleName == "记录列表");
    }

    private static ToolStripItem MenuItem(ContextMenuStrip menu, string text)
    {
        return menu.Items.Cast<ToolStripItem>().Single(item => item.Text == text);
    }

    private static void ClickPasswordSetting(Form form)
    {
        var bounds = form.Bounds;
        Click(form, "设置", true);
        var settings = Application.OpenForms.Cast<Form>().Single(window => window.Text == "密笺 · 设置");
        if (settings != form || settings.Bounds != bounds || Records(form).Visible ||
            Children(settings).OfType<Label>().Any(label => label.Visible && label.Text == "设置"))
            throw new Exception("Settings did not replace the library in the same window");
        var settingsScroll = Children(settings).OfType<Panel>().Single(panel => panel.Visible && panel.AutoScroll);
        settings.ClientSize = new Size(settings.ClientSize.Width, 150);
        settings.PerformLayout();
        var move = Children(settings).OfType<Button>().Single(button => button.AccessibleName == "更换存储位置");
        var change = Children(settings).OfType<Button>().Single(button => button.AccessibleName == "更改主密码");
        var path = Children(settings).OfType<Label>().Single(label => label.AccessibleName == "当前存储位置");
        if (move.Text != "更改" || change.Text != "更改" || path.Parent != move.Parent ||
            path.Right > move.Left || !path.AutoEllipsis)
            throw new Exception("The storage path and its change button are not arranged in one row");
        settingsScroll.ScrollControlIntoView(move);
        if (!settingsScroll.VerticalScroll.Visible || !ActionButton(settings, "返回", true).Visible ||
            !settingsScroll.RectangleToScreen(settingsScroll.ClientRectangle).Contains(move.RectangleToScreen(move.ClientRectangle)))
            throw new Exception("Compact settings cannot scroll to the storage action");
        settings.Bounds = bounds;
        change.PerformClick();
    }

    private static void CheckNoVaultPath(Form form, string path)
    {
        string file = Path.GetFileName(path);
        if (form.Text.Contains(file) || Children(form).Any(control => control.Visible && control.Text.Contains(file)))
            throw new Exception("A vault path is visible in the form");
    }

    private static void CheckCopyMenu(ListView list, ListViewItem item)
    {
        item.EnsureVisible();
        var point = new Point(item.Bounds.Left + 8, item.Bounds.Top + item.Bounds.Height / 2);
        typeof(Control).GetMethod("OnMouseDown", InstanceMembers).Invoke(list,
            new object[] { new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0) });
        if (list.SelectedItems.Count != 1 || list.SelectedItems[0] != item)
            throw new Exception("Right click did not select the clicked record");

        var menu = list.ContextMenuStrip;
        if (menu == null) throw new Exception("Record context menu unavailable");
        menu.Show(list, point);
        try
        {
            var copyAccount = MenuItem(menu, "复制账号");
            var copyPassword = MenuItem(menu, "复制密码");
            if (!menu.Visible || !copyAccount.Enabled || !copyPassword.Enabled)
                throw new Exception("Record context menu copy actions unavailable");
            CheckCopy("record context menu", copyAccount.PerformClick, copyPassword.PerformClick);
        }
        finally { menu.Close(); }
    }

    private static void CheckDetailCopy(Form form)
    {
        var copyAccount = Children(form).OfType<Button>().Single(button => button.AccessibleName == "复制账号");
        var copyPassword = Children(form).OfType<Button>().Single(button => button.AccessibleName == "复制密码");
        if (!copyAccount.Visible || !copyAccount.Enabled || !copyPassword.Visible || !copyPassword.Enabled)
            throw new Exception("Record details copy buttons unavailable");
        CheckCopy("record details", copyAccount.PerformClick, copyPassword.PerformClick);
    }

    // Drive the real tray callbacks without depending on the current Explorer overflow position.
    private static NotifyIcon Tray()
    {
        Type threadContext = typeof(Application).GetNestedType("ThreadContext", BindingFlags.NonPublic);
        var thread = threadContext.GetMethod("FromCurrent", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        var session = threadContext.GetProperty("ApplicationContext", InstanceMembers).GetValue(thread, null);
        var tray = (NotifyIcon)session.GetType().GetField("tray", InstanceMembers).GetValue(session);
        if (!tray.Visible || tray.ContextMenuStrip == null) throw new Exception("Tray unavailable");
        return tray;
    }

    private static void OpenFromTray(bool doubleClick = false)
    {
        var tray = Tray();
        string handler = doubleClick ? "OnMouseDoubleClick" : "OnMouseClick";
        typeof(NotifyIcon).GetMethod(handler, InstanceMembers).Invoke(tray,
            new object[] { new MouseEventArgs(MouseButtons.Left, doubleClick ? 2 : 1, 0, 0, 0) });
    }

    private static void ClickTrayMenu(string name, bool withUnlockPrompt = false)
    {
        var tray = Tray();
        var menu = tray.ContextMenuStrip;
        typeof(NotifyIcon).GetMethod("ShowContextMenu", InstanceMembers).Invoke(tray, null);
        ToolStripItem action;
        try
        {
            action = MenuItem(menu, name);
            if (!menu.Visible || !action.Enabled)
                throw new Exception("Tray menu action unavailable: " + name);
            if (withUnlockPrompt && (!MenuItem(menu, "打开密笺").Enabled || !MenuItem(menu, "退出密笺").Enabled))
                throw new Exception("An open unlock prompt disabled the tray menu");
        }
        finally { menu.Close(); }
        action.PerformClick();
    }

    private static uint AssertClipboard(Action copy, string expected)
    {
        copy();
        uint sequence = GetClipboardSequenceNumber();
        if (Clipboard.GetText() != expected) throw new Exception("Copy did not place the requested field on the clipboard");
        return sequence;
    }

    private static void CheckCopy(string source, Action copyAccount, Action copyPassword)
    {
        const string account = "ui-lab";
        const string password = "dummy-only";
        IDataObject previous;
        try { previous = Clipboard.GetDataObject(); }
        catch (ExternalException)
        {
            Console.WriteLine("SKIP clipboard transfer from " + source + " (Windows clipboard unavailable)");
            return;
        }
        uint sequence = 0;
        try
        {
            sequence = AssertClipboard(copyAccount, account);
            sequence = AssertClipboard(copyPassword, password);
            Console.WriteLine("PASS clipboard transfer from " + source);
        }
        finally
        {
            if (sequence != 0 && sequence == GetClipboardSequenceNumber())
            {
                if (previous == null) Clipboard.Clear();
                else Clipboard.SetDataObject(previous, true);
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            for (Exception cause = ex; cause != null; cause = cause.InnerException)
            {
                Console.Error.WriteLine(cause.GetType().FullName + " " + cause.Message);
                Console.Error.WriteLine(cause.StackTrace);
            }
            return 1;
        }
    }
    private static int Run(string[] args)
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "vault-ui-" + Guid.NewGuid().ToString("N"))).FullName;
        string path = Path.Combine(folder, "smoke.qnv");
        string appExe = Path.GetFullPath(args[0]);
        var clock = Stopwatch.StartNew();
        bool startInTray = args.Length == 2 && args[1] == "--startup";
        Step step = startInTray ? Step.StartInTray : Step.Setup;
        int initialHeight = 0;
        int fullPageHeight = 0;
        long savedRecordId = 0;
        Form minimizedUnlock = null;
        Process repeatedLaunch = null;
        const string secret = "初";
        const string newSecret = "新";
        const string main = "密笺 · 资料库";
        const string detail = "密笺 · 记录详情";
        const string newRecord = "密笺 · 新建记录";
        const string dirtyPrompt = "密笺 · 未保存的修改";
        Exception checkError = null;
        using (var timer = new Timer { Interval = 350 })
        {
            Action checkStep = delegate {
                if (clock.Elapsed.TotalSeconds > 45) throw new TimeoutException("UI step " + step);
                var form = Application.OpenForms.Cast<Form>().LastOrDefault(candidate => candidate.Visible);
                if (step == Step.StartInTray)
                {
                    if (form != null || File.Exists(path)) throw new Exception("Startup opened the vault before a tray request");
                    step = Step.Setup;
                    OpenFromTray();
                    Console.WriteLine("PASS startup starts locked in the tray");
                    return;
                }
                if (step == Step.UnlockFromTray)
                {
                    if (form != null) throw new Exception("Lock left a vault window visible");
                    step = Step.CheckUnlockRestore;
                    OpenFromTray(true);
                }
                else if (step == Step.ShowExitPrompt)
                {
                    if (form != null) throw new Exception("Lock left the main window visible before exit");
                    step = Step.ExitWithPrompt;
                    ClickTrayMenu("打开密笺");
                }
                if (form == null) return;

                if (step == Step.Setup && form.Text == "密笺 · 创建资料库")
                {
                    CheckNoVaultPath(form, path);
                    Field(form, "主密码").Text = Field(form, "确认主密码").Text = secret;
                    step = Step.AddCredential;
                    Click(form, "创建资料库");
                }
                else if (step == Step.AddCredential && form.Text == main)
                {
                    CheckCompactLibrary(form);
                    CheckPager(form, false, false);
                    initialHeight = form.ClientSize.Height;
                    step = Step.SaveCredential;
                    Click(form, "新建", true);
                }
                else if (step == Step.SaveCredential && form.Text == newRecord)
                {
                    Field(form, "标题").Text = "测试账号";
                    Field(form, "账号").Text = "ui-lab";
                    Field(form, "密码").Text = "dummy-only";
                    Field(form, "便签正文").Text = "测试备注";
                    step = Step.VerifyCredential;
                    Click(form, "保存");
                }
                else if (step == Step.VerifyCredential && form.Text == main)
                {
                    var list = Records(form);
                    if (list.Items.Count != 1) throw new Exception("Save did not publish a row");
                    CheckPager(form, false, false);
                    if (form.ClientSize.Height != initialHeight)
                        throw new Exception("Adding a record changed the window height");
                    step = Step.SaveNote;
                    Click(form, "新建", true);
                }
                else if (step == Step.SaveNote && form.Text == newRecord)
                {
                    Field(form, "标题").Text = "无账号便签";
                    Field(form, "便签正文").Text = "测试多行\r\n第二行";
                    step = Step.Search;
                    Click(form, "保存");
                }
                else if (step == Step.Search && form.Text == main)
                {
                    var list = Records(form);
                    if (list.Items.Count != 2) throw new Exception("Plain note did not publish a row");
                    if (form.ClientSize.Height != initialHeight)
                        throw new Exception("Adding a second record changed the window height");
                    CheckPager(form, false, false);
                    list.Items.Cast<ListViewItem>().Single(item => item.Text == "无账号便签").Selected = true;
                    CheckCopyMenu(list, list.Items.Cast<ListViewItem>().Single(item => item.Text == "测试账号"));
                    Field(form, "搜索标题或账号").Text = "ui-lab";
                    step = Step.OpenDetails;
                }
                else if (step == Step.OpenDetails && form.Text == main)
                {
                    var list = Records(form);
                    if (list.Items.Count != 1 || list.Items[0].Text != "测试账号") return;
                    if (form.ClientSize.Height != initialHeight)
                        throw new Exception("Filtering records changed the window height");
                    list.Items[0].Selected = true;
                    step = Step.CheckDetails;
                    OpenSelectedDetails(list);
                }
                else if (step == Step.CheckDetails && form.Text == detail)
                {
                    if (Field(form, "便签正文").Text != "测试备注") throw new Exception("Wrong detail");
                    if (!Field(form, "密码").UseSystemPasswordChar) throw new Exception("Password visible");
                    CheckDetailCopy(form);
                    Field(form, "便签正文").Text = "unsaved edit";
                    step = Step.CancelDiscard;
                    form.Close();
                }
                else if (step == Step.CancelDiscard && form.Text == dirtyPrompt)
                {
                    step = Step.ConfirmDiscard;
                    Click(form, "取消");
                }
                else if (step == Step.ConfirmDiscard && form.Text == detail)
                {
                    if (Field(form, "便签正文").Text != "unsaved edit") throw new Exception("Cancel lost the edit");
                    step = Step.Discard;
                    form.Close();
                }
                else if (step == Step.Discard && form.Text == dirtyPrompt)
                {
                    step = Step.Hide;
                    Click(form, "放弃");
                }
                else if (step == Step.Hide && form.Text == main)
                {
                    form.Close();
                    if (form.Visible || form.IsDisposed) throw new Exception("Close did not hide the vault");
                    ClickTrayMenu("打开密笺");
                    if (!form.Visible || Field(form, "搜索标题或账号").Text != "ui-lab" || Records(form).SelectedItems.Count != 1)
                        throw new Exception("Restore did not preserve the search and selection");
                    form.WindowState = FormWindowState.Minimized;
                    ClickTrayMenu("打开密笺");
                    if (form.WindowState != FormWindowState.Normal)
                        throw new Exception("Tray Open did not restore the minimized library window");
                    form.WindowState = FormWindowState.Minimized;
                    repeatedLaunch = Process.Start(new ProcessStartInfo(appExe, "\"" + path + "\"") {
                        UseShellExecute = false
                    });
                    step = Step.CheckRepeatLaunch;
                }
                else if (step == Step.CheckRepeatLaunch && form.Text == main)
                {
                    if (!repeatedLaunch.HasExited || form.WindowState != FormWindowState.Normal) return;
                    if (repeatedLaunch.ExitCode != 0)
                        throw new Exception("The repeated launch exited with code " + repeatedLaunch.ExitCode);
                    if (!IsWindowVisible(form.Handle))
                        throw new Exception("The original library window remained hidden after a repeated launch");
                    if (Field(form, "搜索标题或账号").Text != "ui-lab" || Records(form).SelectedItems.Count != 1)
                        throw new Exception("A repeated launch did not preserve the original search and selection");
                    step = Step.ChangePassword;
                }
                else if (step == Step.ChangePassword && form.Text == main)
                {
                    step = Step.SavePassword;
                    ClickPasswordSetting(form);
                }
                else if (step == Step.SavePassword && form.Text == "密笺 · 更改主密码")
                {
                    Field(form, "当前主密码").Text = secret;
                    Field(form, "新主密码").Text = Field(form, "确认新主密码").Text = newSecret;
                    step = Step.Lock;
                    Click(form, "更改主密码");
                }
                else if (step == Step.Lock && form.Text == "密笺 · 设置")
                {
                    Click(form, "返回", true);
                }
                else if (step == Step.Lock && form.Text == main)
                {
                    step = Step.UnlockFromTray;
                    Click(form, "锁定", true);
                }
                else if (step == Step.CheckUnlockRestore && form.Text == "密笺 · 解锁资料库")
                {
                    minimizedUnlock = form;
                    form.WindowState = FormWindowState.Minimized;
                    step = Step.EnterPassword;
                    ClickTrayMenu("打开密笺", true);
                }
                else if (step == Step.EnterPassword && form.Text == "密笺 · 解锁资料库")
                {
                    if (form != minimizedUnlock || form.WindowState != FormWindowState.Normal || !IsWindowVisible(form.Handle))
                        throw new Exception("Tray Open did not restore the same minimized unlock prompt");
                    CheckNoVaultPath(form, path);
                    Field(form, "主密码").Text = newSecret;
                    step = Step.SeedPages;
                    Click(form, "解锁");
                }
                else if (step == Step.SeedPages && form.Text == main)
                {
                    if (Records(form).Items.Count != 2) throw new Exception("Missing records after unlock");
                    AddPageFixtures(form);
                    Field(form, "搜索标题或账号").Text = "分页测试";
                    step = Step.VerifyPages;
                }
                else if (step == Step.VerifyPages && form.Text == main)
                {
                    var list = Records(form);
                    if (list.Items.Count == 2) return;
                    fullPageHeight = CheckPageNavigation(form);
                    if (fullPageHeight != initialHeight)
                        throw new Exception("Filling a page changed the initial window height");
                    var item = list.Items[list.Items.Count - 1];
                    item.Selected = true;
                    savedRecordId = (long)item.Tag;
                    if (form.ClientSize.Height != fullPageHeight) throw new Exception("Selection changed the window height");
                    step = Step.SavePagedRecord;
                    OpenSelectedDetails(list);
                }
                else if (step == Step.SavePagedRecord && form.Text == detail)
                {
                    Field(form, "便签正文").Text = "编辑分页中的记录";
                    step = Step.VerifyPagedSave;
                    Click(form, "保存");
                }
                else if (step == Step.VerifyPagedSave && form.Text == main)
                {
                    var list = Records(form);
                    if (list.Items.Count != 8 || list.SelectedItems.Count != 1 || (long)list.SelectedItems[0].Tag != savedRecordId)
                        throw new Exception("Saving the paged record did not restore its selection on an eight-row page");
                    if (form.ClientSize.Height != fullPageHeight) throw new Exception("Saving changed the full-page height");
                    CheckPager(form, false, true);
                    step = Step.ShowExitPrompt;
                    Click(form, "锁定", true);
                }
                else if (step == Step.ExitWithPrompt && form.Text == "密笺 · 解锁资料库")
                {
                    step = Step.Done;
                    ClickTrayMenu("退出密笺", true);
                }
            };
            timer.Tick += delegate {
                try { checkStep(); }
                catch (Exception ex)
                {
                    if (checkError == null) checkError = ex;
                    throw;
                }
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try
            {
                Application.Idle += delegate { if (!timer.Enabled) timer.Start(); };
                var entry = Assembly.LoadFrom(appExe).EntryPoint;
                string[] appArgs = startInTray ? new[] { "--startup", path } : new[] { path };
                try { entry.Invoke(null, new object[] { appArgs }); }
                finally { if (checkError != null) throw new Exception("UI check failed at " + step, checkError); }
                if (step != Step.Done) throw new Exception("Incomplete UI sequence at " + step);
                if (Application.OpenForms.Cast<Form>().Any(form => form.Visible))
                    throw new Exception("Tray Exit left a window visible");
                Console.WriteLine("PASS app entry, compact toolbar, eight rows per page, fixed height, short password, notes, search, context menu, masked detail, discard, tray restore from hidden or minimized, repeated launch restore, settings, password change, lock, reopen, and exit while unlock is visible");
            }
            finally
            {
                timer.Stop();
                if (repeatedLaunch != null)
                {
                    if (!repeatedLaunch.HasExited)
                    {
                        repeatedLaunch.Kill();
                        repeatedLaunch.WaitForExit(5000);
                    }
                    repeatedLaunch.Dispose();
                }
                Directory.Delete(folder, true);
            }
        }
        return 0;
    }
}
