using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class GuiChecks
{
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

    private static void Click(Form form, string text)
    {
        var button = Children(form).OfType<Button>().Single(b => b.Text == text);
        if (!button.Visible || !button.Enabled) throw new Exception(text + " unavailable");
        button.PerformClick();
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
        var clock = Stopwatch.StartNew();
        int step = 0;
        string secret = "temporary-test-passphrase";
        using (var timer = new Timer { Interval = 350 })
        {
            timer.Tick += delegate {
                if (clock.Elapsed.TotalSeconds > 35) throw new TimeoutException("UI step " + step);
                var form = Application.OpenForms.Cast<Form>().LastOrDefault();
                if (form == null || !form.Visible) return;
                bool login = form.GetType().Name == "UnlockForm";
                if (step == 0 && login)
                {
                    Field(form, "主密码").Text = Field(form, "确认主密码").Text = secret;
                    step++; Click(form, "创建资料库");
                }
                else if (step == 1 && !login)
                {
                    Field(form, "标题").Text = "测试账号";
                    Field(form, "账号").Text = "ui-lab";
                    Field(form, "密码").Text = "dummy-only";
                    Field(form, "便签正文").Text = "测试备注";
                    Click(form, "保存");
                    var list = Children(form).OfType<ListView>().Single();
                    if (list.Items.Count != 1) throw new Exception("Save did not publish a row");
                    step++; Click(form, "新建");
                }
                else if (step == 2 && !login)
                {
                    Field(form, "标题").Text = "无账号便签";
                    Field(form, "便签正文").Text = "测试多行\r\n第二行";
                    Click(form, "保存");
                    Field(form, "搜索标题或账号").Text = "ui-lab";
                    step++;
                }
                else if (step == 3 && !login)
                {
                    var list = Children(form).OfType<ListView>().Single();
                    if (list.Items.Count != 1 || list.Items[0].Text != "测试账号") return;
                    list.Items[0].Selected = true;
                    if (Field(form, "便签正文").Text != "测试备注") throw new Exception("Wrong detail");
                    if (!Field(form, "密码").UseSystemPasswordChar) throw new Exception("Password visible");
                    step++; Click(form, "锁定");
                }
                else if (step == 4 && login)
                {
                    Field(form, "主密码").Text = secret;
                    step++; Click(form, "解锁");
                }
                else if (step == 5 && !login)
                {
                    if (Children(form).OfType<ListView>().Single().Items.Count != 2)
                        throw new Exception("Missing records after unlock");
                    step++; form.Close();
                }
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try
            {
                Application.Idle += delegate { if (!timer.Enabled) timer.Start(); };
                var entry = Assembly.LoadFrom(Path.GetFullPath(args[0])).EntryPoint;
                entry.Invoke(null, new object[] { new[] { path } });
                if (step != 6) throw new Exception("Incomplete UI sequence at " + step);
                Console.WriteLine("PASS app entry, setup, credential, plain note, search, masked detail, lock and reopen");
            }
            finally { timer.Stop(); Directory.Delete(folder, true); }
        }
        return 0;
    }
}
