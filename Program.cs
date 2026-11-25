using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            string path = args.Length == 0
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "QuickNoteVault", "vault.qnv")
                : Path.GetFullPath(args[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            while (true)
            {
                Store store;
                using (var unlock = new UnlockForm(path))
                {
                    if (unlock.ShowDialog() != DialogResult.OK)
                        return;
                    store = unlock.OpenedStore;
                }
                using (store)
                using (var window = new MainForm(store))
                {
                    if (window.ShowDialog() != DialogResult.Retry)
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开资料库。\n" + ex.Message, "密笺", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class UnlockForm : Form
{
    internal Store OpenedStore { get; private set; }
    private readonly string vaultPath;
    private readonly bool creating;
    private readonly TextBox password = PasswordBox("主密码");
    private readonly TextBox confirm = PasswordBox("确认主密码");
    private readonly Label status = new Label();
    private readonly Button submit = new Button();
    private bool opening;

    internal UnlockForm(string path)
    {
        vaultPath = path;
        creating = !File.Exists(path);
        Text = creating ? "密笺 · 创建资料库" : "密笺 · 解锁资料库";
        Icon = SystemIcons.Shield;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(249, 250, 247);
        ClientSize = new Size(464, creating ? 466 : 390);
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
            Text = creating ? "创建本地资料库" : "解锁本地资料库",
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true
        }, 34);
        Add(panel, new Label {
            Text = creating ? "请设置至少 12 个字符的主密码。遗忘后无法恢复。" : "输入主密码，查看你的账号和便签。",
            AutoSize = true
        }, 38);
        Add(panel, new Label { Text = "主密码", AutoSize = true }, 25);
        Add(panel, password, 43);
        if (creating)
        {
            Add(panel, new Label { Text = "确认主密码", AutoSize = true }, 25);
            Add(panel, confirm, 43);
        }
        submit.Text = creating ? "创建资料库" : "解锁";
        submit.Dock = DockStyle.Fill;
        submit.FlatStyle = FlatStyle.Flat;
        submit.FlatAppearance.BorderSize = 0;
        submit.BackColor = Color.FromArgb(38, 87, 71);
        submit.ForeColor = Color.White;
        submit.Click += OpenVault;
        Add(panel, submit, 45);
        status.AutoEllipsis = true;
        status.ForeColor = Color.FromArgb(145, 50, 47);
        status.Dock = DockStyle.Fill;
        Add(panel, status, 44);
        var location = new Label { Text = path, AutoEllipsis = true, Dock = DockStyle.Fill };
        Add(panel, location, 31);
        AcceptButton = submit;
        Shown += delegate { password.Focus(); };
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
        if (creating && (secret.Length < 12 || String.IsNullOrWhiteSpace(secret)))
        {
            status.Text = "主密码至少需要 12 个字符。";
            return;
        }
        if (creating && secret != confirm.Text)
        {
            status.Text = "两次输入的主密码不一致。";
            confirm.Focus();
            return;
        }
        opening = true;
        submit.Enabled = password.Enabled = confirm.Enabled = false;
        status.Text = creating ? "正在创建资料库…" : "正在解锁…";
        try
        {
            OpenedStore = await Task.Run(() => new Store(vaultPath, secret, creating));
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            opening = false;
            submit.Enabled = password.Enabled = confirm.Enabled = true;
        }
        if (OpenedStore != null)
        {
            password.Clear();
            confirm.Clear();
            DialogResult = DialogResult.OK;
        }
        else
        {
            password.Focus();
            password.SelectAll();
        }
    }
}
