// ============================================================================
// Mi8iOS.Setup.cs —— 安装器 / 卸载器（用户级，无需管理员权限）
// 编译: csc /target:winexe /out:Mi8iOSSetup.exe /win32manifest:app.manifest
//       /res:app.ico,app.ico /res:Mi8iOS.exe,payload.gui
//       /res:mi8-ios.ps1,payload.engine /res:README.md,payload.readme
//       Mi8iOS.Setup.cs
// 参数:
//   (无)                图形安装
//   --target=DIR        安装目录
//   --silent            无界面
//   /uninstall          卸载（可加 --silent）
//   --purge=DIR         内部用：真正删除文件（从临时副本运行）
// ============================================================================
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mi8iOS.Setup
{
    static class C
    {
        public static readonly Color Bg = Color.FromArgb(14, 17, 23);
        public static readonly Color Panel = Color.FromArgb(22, 27, 34);
        public static readonly Color Card = Color.FromArgb(33, 38, 45);
        public static readonly Color Line = Color.FromArgb(48, 54, 61);
        public static readonly Color Text = Color.FromArgb(230, 237, 243);
        public static readonly Color Dim = Color.FromArgb(139, 148, 158);
        public static readonly Color Green = Color.FromArgb(35, 134, 54);
        public static readonly Color Red = Color.FromArgb(218, 54, 51);
        public static readonly Color OnGreen = Color.FromArgb(63, 185, 80);
    }

    static class Util
    {
        public const string AppName = "Mi8 iOS 工具箱";
        public const string AppExe = "Mi8iOS.exe";
        public const string UninstExe = "Uninstall.exe";
        public const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mi8iOS";

        public static string DefaultTarget
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Programs", "Mi8iOS");
            }
        }

        public static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

        public static void WriteRes(string name, string path, bool bom)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("缺少资源: " + name);
                var buf = new byte[s.Length];
                int off = 0, n;
                while ((n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
                if (bom && off >= 3 && !(buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF))
                {
                    var withBom = new byte[off + 3];
                    withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                    Array.Copy(buf, 0, withBom, 3, off);
                    buf = withBom; off += 3;
                }
                File.WriteAllBytes(path, Sub(buf, off));
            }
        }

        static byte[] Sub(byte[] b, int n)
        {
            if (n == b.Length) return b;
            var r = new byte[n];
            Array.Copy(b, r, n);
            return r;
        }

        // 引擎 ps1 必须带 UTF-8 BOM，否则 PowerShell 5.1 按 GBK 解析中文会语法错
        public static void Install(string target, bool shortcuts)
        {
            Directory.CreateDirectory(target);
            WriteRes("payload.gui", Path.Combine(target, AppExe), false);
            WriteRes("payload.readme", Path.Combine(target, "README.md"), false);
            WriteRes("payload.engine", Path.Combine(target, "engine", "mi8-ios.ps1"), true);

            string self = SelfPath;
            string dst = Path.Combine(target, UninstExe);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, dst, true);

            if (shortcuts) MakeShortcuts(target);
            WriteRegistry(target);
        }

        public static void MakeShortcuts(string target)
        {
            string exe = Path.Combine(target, AppExe);
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                          AppName + ".lnk");
            string menuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                                          AppName);
            Directory.CreateDirectory(menuDir);
            string menu = Path.Combine(menuDir, AppName + ".lnk");
            MkLnk(desktop, exe, target);
            MkLnk(menu, exe, target);
        }

        static void MkLnk(string lnk, string target, string workDir)
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic l = sh.CreateShortcut(lnk);
                l.TargetPath = target;
                l.WorkingDirectory = workDir;
                l.IconLocation = target + ",0";
                l.Description = "小米8 (dipper) 跑 iOS 16 一键工具";
                l.Save();
            }
            catch { }
        }

        public static void RemoveShortcuts()
        {
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
            }
            catch { }
            try
            {
                string menuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
                if (Directory.Exists(menuDir)) Directory.Delete(menuDir, true);
            }
            catch { }
        }

        public static void WriteRegistry(string target)
        {
            try
            {
                string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mi8iOS";
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", "1.0.0");
                    k.SetValue("Publisher", "Mi8 iOS 工具箱");
                    k.SetValue("DisplayIcon", Path.Combine(target, AppExe));
                    k.SetValue("InstallLocation", target);
                    k.SetValue("UninstallString", "\"" + Path.Combine(target, UninstExe) + "\" /uninstall");
                    k.SetValue("QuietUninstallString", "\"" + Path.Combine(target, UninstExe) + "\" /uninstall --silent");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 1200, RegistryValueKind.DWord); // KB
                }
            }
            catch { }
        }

        public static void RemoveRegistry()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mi8iOS", false); }
            catch { }
        }

        public static string InstalledDir()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mi8iOS"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallLocation");
                        if (v != null && Directory.Exists("" + v)) return "" + v;
                    }
                }
            }
            catch { }
            return DefaultTarget;
        }

        // 从临时副本运行，等原进程退出后删除安装目录
        public static void Purge(string dir)
        {
            Thread.Sleep(1800);
            RemoveShortcuts();
            RemoveRegistry();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch
            {
                Thread.Sleep(2500);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------- 安装界面
    sealed class SetupForm : Form
    {
        string target;
        TextBox tDir;
        CheckBox cShort, cLaunch;
        Panel pIntro, pDoing, pDone;
        Label stepL;
        ProgressBar bar;

        public SetupForm(string initialTarget)
        {
            target = string.IsNullOrEmpty(initialTarget) ? Util.DefaultTarget : initialTarget;

            Text = "安装 " + Util.AppName;
            ClientSize = new Size(560, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = C.Bg; ForeColor = C.Text;
            Font = new Font("Microsoft YaHei UI", 9F);
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null) { using (s) Icon = new Icon(s); }
            }
            catch { }

            BuildIntro();
            BuildDoing();
            BuildDone();

            Controls.Add(pIntro);
            Controls.Add(pDoing);
            Controls.Add(pDone);
            ShowPage(pIntro);
        }

        static Label L(string text, float size, Color col, Point loc, bool bold)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = col,
                AutoSize = true,
                Location = loc
            };
        }

        static Button Btn(string text, Size size, Color back, Color fore, Point loc, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text, Size = size, Location = loc, FlatStyle = FlatStyle.Flat,
                BackColor = back, ForeColor = fore, Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false, Font = new Font("Microsoft YaHei UI", 9.5F)
            };
            b.FlatAppearance.BorderColor = C.Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 62);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        void BuildIntro()
        {
            pIntro = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg };

            PictureBox pic = null;
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null)
                {
                    using (s)
                    {
                        using (var ico = new Icon(s))
                            pic = new PictureBox { Image = ico.ToBitmap(), Size = new Size(48, 48), Location = new Point(24, 22), SizeMode = PictureBoxSizeMode.StretchImage };
                    }
                }
            }
            catch { }
            if (pic != null) pIntro.Controls.Add(pic);

            pIntro.Controls.Add(L("安装 Mi8 iOS 工具箱", 15F, C.Text, new Point(86, 24), true));
            pIntro.Controls.Add(L("小米8 (dipper) 跑 iOS 16 的一键流水线 · 纯本地运行", 9F, C.Dim, new Point(88, 54), false));
            pIntro.Controls.Add(L("安装内容：图形界面 + PowerShell 引擎 + 使用说明（约 1.2 MB）", 8.5F, C.Dim, new Point(26, 96), false));
            pIntro.Controls.Add(L("仅当前用户安装，不需要管理员权限，不写注册表服务项。", 8.5F, Color.FromArgb(95, 103, 112), new Point(26, 118), false));

            pIntro.Controls.Add(L("安装位置", 9F, C.Text, new Point(26, 152), false));
            tDir = new TextBox
            {
                Text = target, Location = new Point(26, 174), Width = 434, Height = 26,
                BackColor = C.Card, ForeColor = C.Text, BorderStyle = BorderStyle.FixedSingle
            };
            pIntro.Controls.Add(tDir);
            pIntro.Controls.Add(Btn("浏览…", new Size(66, 26), C.Card, C.Text, new Point(468, 173), delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择安装位置";
                    if (d.ShowDialog(this) == DialogResult.OK) tDir.Text = d.SelectedPath;
                }
            }));

            cShort = new CheckBox { Text = "创建桌面和开始菜单快捷方式", ForeColor = C.Dim, AutoSize = true, Location = new Point(26, 216), Checked = true };
            cLaunch = new CheckBox { Text = "安装完成后启动工具", ForeColor = C.Dim, AutoSize = true, Location = new Point(26, 244), Checked = true };
            pIntro.Controls.Add(cShort); pIntro.Controls.Add(cLaunch);

            var go = Btn("开始安装", new Size(150, 42), C.Green, Color.White, new Point(26, 300), OnInstall);
            var cancel = Btn("取消", new Size(90, 42), C.Card, C.Dim, new Point(444, 300), delegate { Close(); });
            pIntro.Controls.Add(go); pIntro.Controls.Add(cancel);
            AcceptButton = go; CancelButton = cancel;

            pIntro.Controls.Add(L("工具会自动挑剩余空间 ≥ 60GB 的盘做工作目录（下载 IPSW、编译都在那）。", 8F,
                                  Color.FromArgb(88, 96, 105), new Point(26, 356), false));
        }

        void BuildDoing()
        {
            pDoing = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg, Visible = false };
            pDoing.Controls.Add(L("正在安装…", 14F, C.Text, new Point(26, 60), true));
            bar = new ProgressBar { Location = new Point(26, 110), Width = 508, Height = 26, Style = ProgressBarStyle.Continuous, Maximum = 5, Value = 0 };
            pDoing.Controls.Add(bar);
            stepL = L("准备中…", 9F, C.Dim, new Point(26, 152), false);
            pDoing.Controls.Add(stepL);
        }

        void BuildDone()
        {
            pDone = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg, Visible = false };
            pDone.Controls.Add(L("✓  安装完成", 15F, C.OnGreen, new Point(26, 60), true));
            pDone.Controls.Add(L("已安装到", 9F, C.Dim, new Point(26, 106), false));
            var dir = L(target, 8.5F, C.Text, new Point(26, 130), false);
            dir.Font = new Font("Consolas", 8.5F);
            pDone.Controls.Add(dir);
            pDone.Controls.Add(L("下一步建议：打开工具 → 点「体检 doctor」→ 「QEMU 演示」先看效果。", 9F, C.Dim, new Point(26, 176), false));
            pDone.Controls.Add(L("真机前请先解锁 Bootloader（小米需等待 168 小时）。", 9F, Color.FromArgb(210, 153, 34), new Point(26, 204), false));

            var open = Btn("启动工具", new Size(150, 42), C.Green, Color.White, new Point(26, 260), delegate
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(target, Util.AppExe),
                        UseShellExecute = true
                    });
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
                Close();
            });
            var done = Btn("完成", new Size(90, 42), C.Card, C.Dim, new Point(444, 260), delegate { Close(); });
            pDone.Controls.Add(open); pDone.Controls.Add(done);
            AcceptButton = done; CancelButton = done;
        }

        void ShowPage(Panel p)
        {
            pIntro.Visible = (p == pIntro);
            pDoing.Visible = (p == pDoing);
            pDone.Visible = (p == pDone);
            if (p == pDone) { pIntro.Hide(); pDoing.Hide(); pDone.Show(); }
            else if (p == pIntro) { pDoing.Hide(); pDone.Hide(); pIntro.Show(); }
            else { pIntro.Hide(); pDone.Hide(); pDoing.Show(); }
        }

        void OnInstall(object sender, EventArgs e)
        {
            target = tDir.Text.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show("请填写安装位置", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool shortcuts = cShort.Checked;
            bool launch = cLaunch.Checked;

            ShowPage(pDoing);
            Enabled = false;

            var th = new Thread(delegate()
            {
                string err = null;
                try
                {
                    Step(1, "写入程序文件…");
                    Util.Install(target, shortcuts);
                    Step(4, "注册卸载信息…");
                    Thread.Sleep(300);
                }
                catch (Exception ex) { err = ex.Message; }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        Enabled = true;
                        if (err != null)
                        {
                            ShowPage(pIntro);
                            MessageBox.Show("安装失败：\n" + err, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        ShowPage(pDone);
                        if (launch)
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = Path.Combine(target, Util.AppExe),
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                            Close();
                        }
                    }));
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void Step(int n, string text)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    bar.Value = Math.Min(bar.Maximum, n);
                    stepL.Text = text;
                }));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ 入口
    static class Entry
    {
        static string Arg(string[] args, string name)
        {
            string p = name + "=";
            foreach (var a in args)
                if (a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a.Substring(p.Length);
            return null;
        }

        static bool Has(string[] args, string name)
        {
            foreach (var a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args == null) args = new string[0];

            string purge = Arg(args, "--purge");
            if (purge != null) { Util.Purge(purge); return 0; }

            bool silent = Has(args, "--silent");
            bool uninstall = Has(args, "/uninstall") || Has(args, "--uninstall");

            if (uninstall)
            {
                string dir = Util.InstalledDir();
                if (silent)
                {
                    // 复制到临时目录再删，避免删除正在运行的自身
                    string tmp = Path.Combine(Path.GetTempPath(), "Mi8iOS-uninst-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                    File.Copy(Util.SelfPath, tmp, true);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = tmp,
                        Arguments = "--purge=\"" + dir + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var r = MessageBox.Show("确定要卸载 " + Util.AppName + " 吗？\n\n" +
                                        "安装目录将被删除：" + dir + "\n\n" +
                                        "注意：不会删除工作目录里的 IPSW、镜像和编译产物（E:\\mi8-ios 等），" +
                                        "如需一并清掉请手动删除。",
                                        "卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;

                string tmp2 = Path.Combine(Path.GetTempPath(), "Mi8iOS-uninst-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                File.Copy(Util.SelfPath, tmp2, true);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tmp2,
                    Arguments = "--purge=\"" + dir + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return 0;
            }

            string target = Arg(args, "--target");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (silent)
            {
                try
                {
                    Util.Install(string.IsNullOrEmpty(target) ? Util.DefaultTarget : target, true);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

            Application.Run(new SetupForm(target));
            return 0;
        }
    }
}
