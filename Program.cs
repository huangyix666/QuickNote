using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            bool background = args.Length != 0 && args[0] == "--startup";
            int pathArg = background ? 1 : 0;
            string path = args.Length <= pathArg
                ? VaultLocationSettings.CurrentPath
                : Path.GetFullPath(args[pathArg]);
            using (var instance = new VaultInstance(path))
            {
                if (!instance.TakeOwnership(background)) return;
                using (var session = new VaultSession(path))
                {
                    instance.Listen(delegate { session.OpenVault(); });
                    if (background) session.ShowLockedTray();
                    else session.OpenVault();
                    Application.Run(session);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开资料库。\n" + ex.Message, "密笺", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class WindowVisibility
{
    private const int Show = 5;
    private const int Restore = 9;

    internal static void EnsureShown(Form form)
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;

        IntPtr handle = form.Handle;
        if (IsIconic(handle)) ShowWindow(handle, Restore);
        if (!IsWindowVisible(handle))
        {
            // A hidden startup can override the first native show without updating Form.Visible.
            ShowWindow(handle, Show);
            if (!IsWindowVisible(handle)) ShowWindow(handle, Show);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}

internal sealed class VaultInstance : IDisposable
{
    private const int ActivateMessage = 0x8001; // WM_APP + 1
    private static readonly IntPtr MessageWindow = new IntPtr(-3); // HWND_MESSAGE
    private readonly string name;
    private readonly Mutex mutex;
    private bool owned;
    private ActivationWindow receiver;

    internal VaultInstance(string path)
    {
        using (var user = WindowsIdentity.GetCurrent())
        using (var sha = SHA256.Create())
        {
            string identity = user.User.Value + "|" + Path.GetFullPath(path).ToUpperInvariant();
            name = "QuickNoteVault-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
        }
        mutex = new Mutex(false, @"Local\" + name);
    }

    internal bool TakeOwnership(bool background)
    {
        if (TryAcquire()) return true;
        if (background) return false;

        // Another launch can acquire the mutex before its receiver is created.
        var wait = Stopwatch.StartNew();
        do
        {
            IntPtr target = FindWindowEx(MessageWindow, IntPtr.Zero, null, name);
            uint processId;
            if (target != IntPtr.Zero && GetWindowThreadProcessId(target, out processId) != 0)
            {
                AllowSetForegroundWindow(processId);
                if (PostMessage(target, ActivateMessage, IntPtr.Zero, IntPtr.Zero)) return false;
            }
            if (TryAcquire()) return true;
            Thread.Sleep(25);
        }
        while (wait.ElapsedMilliseconds < 3000);
        throw new InvalidOperationException("密笺已在运行，暂时无法唤出窗口。");
    }

    private bool TryAcquire()
    {
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        return owned;
    }

    internal void Listen(Action activate)
    {
        receiver = new ActivationWindow(name, activate);
    }

    public void Dispose()
    {
        if (receiver != null) receiver.DestroyHandle();
        if (owned) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    private sealed class ActivationWindow : NativeWindow
    {
        private readonly Action activate;

        internal ActivationWindow(string name, Action activate)
        {
            this.activate = activate;
            CreateHandle(new CreateParams { Parent = MessageWindow, Caption = name });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ActivateMessage) activate();
            else base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal static class StartupSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickNoteVault";

    private static string Command
    {
        get { return "\"" + Application.ExecutablePath + "\" --startup"; }
    }

    internal static bool Enabled
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && Matches(key);
        }
        set
        {
            if (value)
            {
                if (Command.Length > 260) throw new InvalidOperationException("程序所在路径过长，无法添加开机自启动。");
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null && Matches(key)) key.DeleteValue(ValueName, false);
            }
        }
    }

    private static bool Matches(RegistryKey key)
    {
        return String.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class VaultLocationSettings
{
    private const string RunKey = @"Software\QuickNoteVault";
    private const string ValueName = "VaultPath";

    internal static string CurrentPath
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                string saved = key == null ? null : key.GetValue(ValueName) as string;
                if (!String.IsNullOrWhiteSpace(saved)) return Path.GetFullPath(saved);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "QuickNoteVault", "vault.qnv");
        }
    }

    internal static void Set(string path)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(ValueName, Path.GetFullPath(path), RegistryValueKind.String);
    }
}

internal sealed class VaultSession : ApplicationContext
{
    private string path;
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    private MainForm window;
    private UnlockForm unlockWindow;
    private Store store;
    private bool locking;
    private string oldPathAfterMove;

    internal VaultSession(string path)
    {
        this.path = path;
        var open = trayMenu.Items.Add("打开密笺", null, delegate { OpenVault(); });
        var lockItem = trayMenu.Items.Add("锁定资料库", null, delegate { LockVault(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        var exit = trayMenu.Items.Add("退出密笺", null, delegate { ExitVault(); });
        trayMenu.Opening += delegate {
            open.Enabled = !locking;
            lockItem.Enabled = !locking && window != null && !window.HasOpenDialog;
            exit.Enabled = !locking && (window == null || !window.HasOpenDialog)
                && (unlockWindow == null || !unlockWindow.IsOpening);
        };
        tray.Icon = UiStyle.AppIcon;
        tray.Text = "密笺";
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += OpenFromTray;
        tray.MouseDoubleClick += OpenFromTray;
    }

    private void OpenFromTray(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) OpenVault();
    }

    internal void ShowLockedTray()
    {
        tray.Text = "密笺（已锁定）";
        tray.Visible = true;
    }

    internal void OpenVault()
    {
        if (locking) return;
        if (window != null) { window.RestoreWindow(); return; }
        if (unlockWindow == null)
        {
            unlockWindow = new UnlockForm(path);
            unlockWindow.FormClosed += UnlockClosed;
        }
        WindowVisibility.EnsureShown(unlockWindow);
        unlockWindow.Activate();
    }

    private void UnlockClosed(object sender, FormClosedEventArgs e)
    {
        var unlock = (UnlockForm)sender;
        unlockWindow = null;
        store = unlock.OpenedStore;
        if (store == null)
        {
            if (!tray.Visible) ExitThread();
            return;
        }
        window = new MainForm(store);
        window.LockRequested += delegate { LockVault(); };
        window.StorageMoved += StorageMoved;
        window.FormClosed += delegate {
            window = null;
            store.Dispose();
            store = null;
            if (!String.IsNullOrEmpty(oldPathAfterMove))
            {
                string oldPath = oldPathAfterMove;
                oldPathAfterMove = null;
                if (MessageBox.Show("新位置已经验证成功。是否删除旧资料库？\n\n" + oldPath,
                    "清理旧位置", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    DeleteVaultFiles(oldPath);
            }
            if (locking) ShowLockedTray();
            else ExitThread();
            locking = false;
        };
        tray.Text = "密笺";
        tray.Visible = true;
        window.RestoreWindow();
    }

    private void StorageMoved(object sender, StorageMovedEventArgs e)
    {
        string previous = path;
        try { VaultLocationSettings.Set(e.NewPath); }
        catch (Exception ex)
        {
            MessageBox.Show("新资料库已经复制成功，但无法保存默认位置。\n" + ex.Message,
                "更换存储位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        path = e.NewPath;
        oldPathAfterMove = previous;
        locking = true;
        var currentWindow = sender as MainForm;
        if (currentWindow != null)
            currentWindow.BeginInvoke(new Action(delegate { if (window != null) window.CloseSession(); }));
    }

    private static void DeleteVaultFiles(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    private void ExitVault()
    {
        if (locking) return;
        if (window != null)
        {
            if (!window.HasOpenDialog) window.CloseSession();
            return;
        }
        if (unlockWindow != null)
        {
            if (unlockWindow.IsOpening) return;
            unlockWindow.Close();
        }
        ExitThread();
    }

    private void LockVault()
    {
        if (window == null || window.HasOpenDialog || locking) return;
        locking = true;
        try { window.CloseSession(); }
        finally { locking = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            if (unlockWindow != null) unlockWindow.Dispose();
            if (window != null) window.Dispose();
            if (store != null) store.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class UnlockForm : Form
{
    internal Store OpenedStore { get; private set; }
    internal bool IsOpening { get { return opening; } }
    private readonly string vaultPath;
    private readonly Store changingStore;
    private readonly bool creating;
    private readonly TextBox password;
    private readonly TextBox newPassword = PasswordBox("新主密码");
    private readonly TextBox confirm;
    private readonly Label status = new Label();
    private readonly Button submit;
    private bool opening;

    internal UnlockForm(string path, Store changingStore = null)
    {
        vaultPath = path;
        this.changingStore = changingStore;
        bool changing = changingStore != null;
        creating = !changing && !File.Exists(path);
        string action = changing ? "更改主密码" : creating ? "创建资料库" : "解锁";
        string title = changing ? action : creating ? "创建资料库" : "解锁资料库";
        Text = "密笺 · " + title;
        password = PasswordBox(changing ? "当前主密码" : "主密码");
        confirm = PasswordBox(changing ? "确认新主密码" : "确认主密码");
        submit = UiStyle.ActionButton(action, true);
        Icon = UiStyle.AppIcon;
        StartPosition = changing ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = !changing;
        ShowInTaskbar = !changing;
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(249, 250, 247);
        ClientSize = new Size(438, changing ? 460 : creating ? 393 : 325);
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(32, 24, 32, 20), ColumnCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        Add(panel, new Label {
            Text = "密笺", Font = new Font(Font.FontFamily, 21, FontStyle.Bold),
            ForeColor = Color.FromArgb(27, 65, 55), AutoSize = true
        }, 50);
        Add(panel, new Label {
            Text = title,
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true
        }, 34);
        Add(panel, new Label {
            Text = changing ? "先输入当前主密码，再设置新主密码。"
                : creating ? "请输入并确认主密码。遗忘后无法恢复。" : "输入主密码，查看你的账号和便签。",
            AutoSize = true
        }, 38);
        Add(panel, new Label { Text = password.AccessibleName, AutoSize = true }, 25);
        Add(panel, password, 43);
        if (changing)
        {
            Add(panel, new Label { Text = newPassword.AccessibleName, AutoSize = true }, 25);
            Add(panel, newPassword, 43);
        }
        if (creating || changing)
        {
            Add(panel, new Label { Text = confirm.AccessibleName, AutoSize = true }, 25);
            Add(panel, confirm, 43);
        }
        submit.Dock = DockStyle.Fill;
        submit.Click += OpenVault;
        Add(panel, submit, 45);
        status.AutoEllipsis = true;
        status.ForeColor = Color.FromArgb(145, 50, 47);
        status.Dock = DockStyle.Fill;
        Add(panel, status, 44);
        AcceptButton = submit;
        Shown += delegate { password.Focus(); };
        if (changing) UiStyle.FadeIn(this);
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (opening) e.Cancel = true; };
    }

    private static void Add(TableLayoutPanel panel, Control control, int height)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.Controls.Add(control, 0, row);
    }

    private static TextBox PasswordBox(string name)
    {
        return new TextBox { AccessibleName = name, Dock = DockStyle.Top, UseSystemPasswordChar = true };
    }

    private async void OpenVault(object sender, EventArgs e)
    {
        if (opening) return;
        string secret = password.Text;
        bool changing = changingStore != null;
        string replacement = changing ? newPassword.Text : secret;
        if (secret.Length == 0 || replacement.Length == 0)
        {
            status.Text = secret.Length == 0 ? "请输入主密码。" : "请输入新主密码。";
            return;
        }
        if ((creating || changing) && replacement != confirm.Text)
        {
            status.Text = "两次输入的主密码不一致。";
            confirm.Focus();
            return;
        }
        opening = true;
        submit.Enabled = password.Enabled = confirm.Enabled = newPassword.Enabled = false;
        status.Text = changing ? "正在更改主密码…" : creating ? "正在创建资料库…" : "正在解锁…";
        bool success = false;
        try
        {
            if (changing) await Task.Run(() => changingStore.ChangePassword(secret, replacement));
            else OpenedStore = await Task.Run(() => new Store(vaultPath, secret, creating));
            success = true;
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            opening = false;
            submit.Enabled = password.Enabled = confirm.Enabled = newPassword.Enabled = true;
        }
        if (success)
        {
            password.Clear();
            newPassword.Clear();
            confirm.Clear();
            if (changing) DialogResult = DialogResult.OK;
            else Close();
        }
        else
        {
            password.Focus();
            password.SelectAll();
        }
    }
}
