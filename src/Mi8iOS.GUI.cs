// ============================================================================
// Mi8iOS.GUI.cs —— 小米8 跑 iOS 16 的图形化工具箱
// 编译: csc /target:winexe /out:Mi8iOS.exe /win32manifest:app.manifest
//       /res:app.ico,app.ico Mi8iOS.GUI.cs
// 说明: 纯 .NET Framework 4.x WinForms，零第三方依赖；引擎是 mi8-ios.ps1
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mi8iOS
{
    // ---------------------------------------------------------------- 调色板
    static class C
    {
        public static readonly Color Bg      = Color.FromArgb(14, 17, 23);
        public static readonly Color Panel   = Color.FromArgb(22, 27, 34);
        public static readonly Color Card    = Color.FromArgb(33, 38, 45);
        public static readonly Color Line    = Color.FromArgb(48, 54, 61);
        public static readonly Color Text    = Color.FromArgb(230, 237, 243);
        public static readonly Color Dim     = Color.FromArgb(139, 148, 158);
        public static readonly Color Accent  = Color.FromArgb(47, 129, 247);
        public static readonly Color Green   = Color.FromArgb(35, 134, 54);
        public static readonly Color OnGreen = Color.FromArgb(63, 185, 80);
        public static readonly Color Red     = Color.FromArgb(218, 54, 51);
        public static readonly Color OnRed   = Color.FromArgb(248, 81, 73);
        public static readonly Color Amber   = Color.FromArgb(210, 153, 34);
        public static readonly Color Log     = Color.FromArgb(201, 209, 217);
        public static readonly Color LogBg   = Color.FromArgb(13, 17, 23);
    }

    // ------------------------------------------------------------ 配置(简单INI)
    sealed class AppConfig
    {
        public string Work = "";
        public string BootImg = "";
        public bool Yes = false;

        static string Path_ { get { return Path.Combine(BaseDir, "config.ini"); } }

        public static AppConfig Load()
        {
            var c = new AppConfig();
            try
            {
                if (File.Exists(Path_))
                {
                    foreach (var raw in File.ReadAllLines(Path_, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int i = line.IndexOf('=');
                        if (i < 1) continue;
                        string k = line.Substring(0, i).Trim();
                        string v = line.Substring(i + 1).Trim();
                        if (k == "Work") c.Work = v;
                        else if (k == "BootImg") c.BootImg = v;
                        else if (k == "Yes") c.Yes = (v == "1" || v.ToLower() == "true");
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Mi8 iOS 工具箱 配置");
                sb.AppendLine("Work=" + Work);
                sb.AppendLine("BootImg=" + BootImg);
                sb.AppendLine("Yes=" + (Yes ? "1" : "0"));
                File.WriteAllText(Path_, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { MessageBox.Show("保存配置失败: " + ex.Message); }
        }

        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }
    }

    // ------------------------------------------------------------- 进度解析
    static class Engine
    {
        public const string IpswName = "iPhone10,3,iPhone10,6_16.7.16_20H392_Restore.ipsw";

        // 找引擎脚本：安装版在 engine\，开发版与 exe 同目录
        public static string ScriptPath
        {
            get
            {
                string a = Path.Combine(AppConfig.BaseDir, "engine", "mi8-ios.ps1");
                if (File.Exists(a)) return a;
                string b = Path.Combine(AppConfig.BaseDir, "mi8-ios.ps1");
                if (File.Exists(b)) return b;
                return a;
            }
        }

        // 与 mi8-ios.ps1 的 Resolve-Work 完全一致的规则
        public static string ResolveWork(string configured)
        {
            if (!string.IsNullOrEmpty(configured)) return configured;
            try
            {
                var root = DriveInfo.GetDrives();
                string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd('\\');
                long sysFree = 0;
                foreach (var d in root)
                {
                    if (!d.IsReady || d.DriveType == DriveType.Removable) continue;
                    if (d.Name.TrimEnd('\\') == sys) { sysFree = d.AvailableFreeSpace; }
                }
                if (sysFree >= 60L * 1024 * 1024 * 1024)
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mi8-ios");

                string best = null; long bestFree = 0;
                foreach (var d in root)
                {
                    if (!d.IsReady || d.DriveType == DriveType.Removable) continue;
                    if (d.Name.Length < 2) continue;
                    if (d.AvailableFreeSpace > bestFree) { bestFree = d.AvailableFreeSpace; best = d.Name; }
                }
                if (best != null && bestFree >= 60L * 1024 * 1024 * 1024)
                    return Path.Combine(best.TrimEnd('\\') + "\\mi8-ios");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mi8-ios");
            }
            catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mi8-ios"); }
        }

        public static string BuildArgs(string cmd, AppConfig cfg, string work)
        {
            var sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -Command \"");
            sb.Append("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;");
            sb.Append("$ErrorActionPreference='Continue';");
            sb.Append("& '").Append(Esc(ScriptPath)).Append("' '").Append(Esc(cmd)).Append("'");
            if (!string.IsNullOrEmpty(work)) sb.Append(" -Work '").Append(Esc(work)).Append("'");
            if (!string.IsNullOrEmpty(cfg.BootImg)) sb.Append(" -BootImg '").Append(Esc(cfg.BootImg)).Append("'");
            if (cfg.Yes) sb.Append(" -Yes");
            sb.Append("\"");
            return sb.ToString();
        }

        static string Esc(string s) { return (s ?? "").Replace("'", "''"); }

        public static ProcessStartInfo CreateInfo(string cmd, AppConfig cfg, string work)
        {
            return new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = BuildArgs(cmd, cfg, work),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,   // 关键：wsl.exe 在无 stdin 时会永久挂起
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ScriptPath)
            };
        }

        public static bool Ex(string p) { return !string.IsNullOrEmpty(p) && File.Exists(p); }
    }

    // ------------------------------------------------------------ 进度条目
    sealed class Stage
    {
        public string Name;
        public Func<bool?> Check;
        public Stage(string n, Func<bool?> c) { Name = n; Check = c; }
    }

    sealed class StageRow : Panel
    {
        public string Title = "";
        public bool? State = false;

        public StageRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = 30;
            Margin = new Padding(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = 13f, cy = Height / 2f, r = 5f;
            bool done = (State == true);
            bool unknown = (State == null);

            if (done)
            {
                using (var b = new SolidBrush(C.OnGreen)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                using (var p = new Pen(Color.FromArgb(255, 255, 255), 1.6f))
                {
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx - 2.6f, cy), new PointF(cx - 0.6f, cy + 2.2f), new PointF(cx + 2.9f, cy - 2.4f) });
                }
            }
            else
            {
                using (var b = new SolidBrush(unknown ? Color.FromArgb(60, 66, 75) : C.Card))
                    g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                using (var p = new Pen(C.Line, 1.2f)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }

            using (var f = new Font("Microsoft YaHei UI", 9f))
            using (var br = new SolidBrush(done ? C.Text : (unknown ? Color.FromArgb(100, 108, 116) : C.Dim)))
                g.DrawString(Title, f, br, new PointF(27f, (Height - f.GetHeight(g)) / 2f - 1f));
        }
    }

    // ------------------------------------------------------------ 主窗体
    sealed class MainForm : Form
    {
        AppConfig cfg;
        string work;

        // 顶栏
        Label title, sub, stateL, workL, timerL;
        PictureBox logo;

        // 进度
        List<Stage> stages = new List<Stage>();
        List<StageRow> rows = new List<StageRow>();
        FlowLayoutPanel stageFlow;
        bool hfsQueried, hfsOk;
        string pendingRun;

        // 日志
        RichTextBox log;
        Panel logBox;

        // 运行
        Process proc;
        string running;
        Stopwatch sw = new Stopwatch();
        StreamWriter logWriter;
        string logFile;
        System.Windows.Forms.Timer tick;

        // 控件
        Button btnNext, btnStop, btnClear;
        ProgressBar marquee;

        public MainForm(string[] args)
        {
            cfg = AppConfig.Load();
            work = Engine.ResolveWork(cfg.Work);

            Text = "Mi8 iOS 工具箱 —— 小米8 (dipper) 跑 iOS 16";
            ClientSize = new Size(1180, 760);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C.Bg;
            ForeColor = C.Text;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);

            try { var ico = LoadIcon(); if (ico != null) Icon = ico; } catch { }

            BuildStages();
            BuildUi();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 3000;
            tick.Tick += delegate { RefreshStages(); };
            tick.Start();

            RefreshStages();
            QueryHfsAsync();

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--run" && i + 1 < args.Length) { pendingRun = args[i + 1]; break; }
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AppendLine("已就绪 · 引擎: " + Engine.ScriptPath, C.Dim);
            AppendLine("点击「一键下一步」自动推进，或按需点下面的步骤按钮。", C.Dim);
            AppendLine("", C.Log);
            if (pendingRun != null)
            {
                string c = pendingRun; pendingRun = null;
                Run(c, IsDanger(c));
            }
        }

        static Icon LoadIcon()
        {
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null) { using (s) return new Icon(s); }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------ 阶段定义
        void BuildStages()
        {
            string W = work;
            stages.Add(new Stage("环境 setup", delegate {
                return Engine.Ex(Path.Combine(W, "lib", "setup.sh")); }));
            stages.Add(new Stage("源码 clone", delegate {
                return Engine.Ex(Path.Combine(W, "repo", ".git")); }));
            stages.Add(new Stage("WSL hfsplus", delegate {
                return hfsQueried ? (bool?)hfsOk : null; }));
            stages.Add(new Stage("iOS 16.7.16 固件", delegate {
                return Engine.Ex(Path.Combine(W, "ipsw", Engine.IpswName)); }));
            stages.Add(new Stage("根文件系统镜像", delegate {
                return Engine.Ex(Path.Combine(W, "out", "ios16-root.simg")); }));
            stages.Add(new Stage("XNU 内核", delegate {
                string d = Path.Combine(W, "xnu");
                try { if (Directory.Exists(d)) return Directory.GetFiles(d, "kernel*", SearchOption.AllDirectories).Length > 0; }
                catch { }
                return false; }));
            stages.Add(new Stage("boot.img", delegate {
                return Engine.Ex(Path.Combine(W, "phone", "boot.img")); }));
            stages.Add(new Stage("引导镜像 shim-boot", delegate {
                return Engine.Ex(Path.Combine(W, "repo", "shim", "out", "shim-boot.img")); }));
        }

        void RefreshStages()
        {
            for (int i = 0; i < stages.Count && i < rows.Count; i++)
            {
                bool? v = false;
                try { v = stages[i].Check(); } catch { v = false; }
                if (rows[i].State != v) { rows[i].State = v; rows[i].Invalidate(); }
            }
        }

        void QueryHfsAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = "-e grep -qw hfsplus /proc/filesystems",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    if (p != null)
                    {
                        try { p.StandardInput.Close(); } catch { }
                        ok = p.WaitForExit(5000) && p.ExitCode == 0;
                        p.Close();
                    }
                }
                catch { ok = false; }
                hfsOk = ok; hfsQueried = true;
                try { BeginInvoke(new MethodInvoker(RefreshStages)); } catch { }
            });
        }

        // ------------------------------------------------------ 界面搭建
        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), Padding = new Padding(0) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildBody(), 0, 1);
            root.Controls.Add(BuildActions(), 0, 2);
            root.Controls.Add(BuildStatus(), 0, 3);
        }

        Panel BuildHeader()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Panel, Padding = new Padding(16, 0, 12, 0) };

            logo = new PictureBox { Size = new Size(34, 34), Location = new Point(18, 15), SizeMode = PictureBoxSizeMode.StretchImage };
            try { var ico = LoadIcon(); if (ico != null) logo.Image = ico.ToBitmap(); } catch { }
            p.Controls.Add(logo);

            title = new Label { Text = "Mi8 iOS 工具箱", Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                                ForeColor = C.Text, AutoSize = true, Location = new Point(62, 10) };
            sub = new Label { Text = "小米8 · dipper · 骁龙845   →   iOS 16.7.16 (20H392) · 仅写 userdata，不动 boot/recovery",
                              Font = new Font("Microsoft YaHei UI", 8.5F), ForeColor = C.Dim, AutoSize = true, Location = new Point(64, 40) };
            p.Controls.Add(title); p.Controls.Add(sub);

            // 顶栏按钮用固定宽度：AutoSize 在 Resize 里算不准
            var bDir = MkBtnFixed("打开工作目录", new Size(116, 32), delegate { OpenWork(); });
            var bSet = MkBtnFixed("设置", new Size(64, 32), delegate { OpenSettings(); });
            bSet.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bDir.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bSet.Location = new Point(p.Width - bSet.Width - 16, 16);
            bDir.Location = new Point(bSet.Left - bDir.Width - 8, 16);
            p.Controls.Add(bSet); p.Controls.Add(bDir);
            p.Resize += delegate
            {
                bSet.Left = p.ClientSize.Width - bSet.Width - 16;
                bDir.Left = bSet.Left - bDir.Width - 8;
            };

            var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = C.Line };
            p.Controls.Add(line);
            return p;
        }

        Control BuildBody()
        {
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268F));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // ---- 左：进度
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = C.Bg, Padding = new Padding(14, 14, 10, 8) };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            btnNext = MkBtn("▶   一键下一步", C.Green, Color.White, delegate { Run("auto", false); }, 11F);
            btnNext.Height = 44;
            btnNext.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            left.Controls.Add(btnNext);

            var stagePanel = new Panel { Dock = DockStyle.Fill, BackColor = C.Panel, Padding = new Padding(0, 8, 0, 8) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false, Padding = new Padding(4, 0, 0, 0) };
            stageFlow = flow;
            stagePanel.Controls.Add(flow);

            var head = new Label { Text = "流水线进度", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                                   ForeColor = C.Dim, AutoSize = true, Location = new Point(6, 2), Padding = new Padding(0, 6, 0, 4) };
            flow.Controls.Add(head);

            foreach (var s in stages)
            {
                var r = new StageRow { Title = s.Name, Width = 236, State = false };
                rows.Add(r);
                flow.Controls.Add(r);
            }
            flow.Resize += delegate
            {
                foreach (Control c in flow.Controls) { if (c is StageRow) c.Width = flow.ClientSize.Width - 8; }
            };
            left.Controls.Add(stagePanel);

            tbl.Controls.Add(left, 0, 0);

            // ---- 右：日志
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(8, 14, 14, 8), Padding = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var bar = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg };
            var lbl = new Label { Text = "运行日志", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), ForeColor = C.Dim, AutoSize = true, Location = new Point(2, 8) };
            bar.Controls.Add(lbl);
            btnClear = MkBtn("清空", C.Card, C.Dim, delegate { log.Clear(); }, 8.5F);
            var bCopy = MkBtn("全选复制", C.Card, C.Dim, delegate { log.SelectAll(); log.Copy(); }, 8.5F);
            btnStop = MkBtn("■  停止", C.Card, C.OnRed, delegate { StopRun(); }, 8.5F);
            btnStop.Enabled = false;
            var bs = new Control[] { bCopy, btnClear, btnStop };
            bar.Resize += delegate
            {
                int x = bar.ClientSize.Width;
                for (int i = bs.Length - 1; i >= 0; i--) { x -= bs[i].Width; bs[i].Location = new Point(x, 3); x -= 8; }
            };
            for (int i = bs.Length - 1; i >= 0; i--) bar.Controls.Add(bs[i]);
            right.Controls.Add(bar, 0, 0);

            logBox = new Panel { Dock = DockStyle.Fill, BackColor = C.LogBg, Padding = new Padding(1) };
            log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = C.LogBg,
                ForeColor = C.Log,
                Font = new Font("Consolas", 10F),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                WordWrap = false,
                HideSelection = false,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };
            logBox.Controls.Add(log);
            right.Controls.Add(logBox, 0, 1);

            tbl.Controls.Add(right, 1, 0);
            return tbl;
        }

        Control BuildActions()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg, Padding = new Padding(14, 4, 14, 6) };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 2, 0, 0)
            };

            // 行必须 AutoSize：TopDown 下非 AutoSize 的子面板宽度停在默认 150，会把按钮裁掉
            var row1 = MkRow(4);
            row1.Controls.Add(MkBtn("体检 doctor", C.Card, C.Text, delegate { Run("doctor", false); }, 9F));
            row1.Controls.Add(MkBtn("① 环境 setup", C.Card, C.Text, delegate { Run("setup", false); }, 9F));
            row1.Controls.Add(MkBtn("② 下载 IPSW", C.Card, C.Text, delegate { Run("ipsw", false); }, 9F));
            row1.Controls.Add(MkBtn("③ 构建 rootfs", C.Card, C.Text, delegate { Run("rootfs", false); }, 9F));
            row1.Controls.Add(MkBtn("④ 编译内核", C.Card, C.Text, delegate { Run("kernel", false); }, 9F));
            row1.Controls.Add(MkBtn("⑤ 打包配置", C.Card, C.Text, delegate { Run("build", false); }, 9F));
            flow.Controls.Add(row1);

            // 第二行：真机 / 演示
            var row2 = MkRow(0);
            row2.Controls.Add(MkBtn("备份手机", C.Card, Color.FromArgb(210, 190, 90), delegate { Run("backup", true); }, 9F));
            row2.Controls.Add(MkBtn("⚠  刷入启动", Color.FromArgb(70, 26, 24), C.OnRed, delegate { Run("flash", true); }, 9F));
            row2.Controls.Add(MkBtn("回滚说明", C.Card, C.Dim, delegate { Run("restore", false); }, 9F));
            row2.Controls.Add(MkBtn("QEMU 演示", C.Card, Color.FromArgb(120, 180, 255), delegate { Run("demo", false); }, 9F));
            row2.Controls.Add(MkBtn("进度 status", C.Card, C.Dim, delegate { Run("status", false); }, 9F));
            row2.Controls.Add(MkBtn("帮助 help", C.Card, C.Dim, delegate { Run("help", false); }, 9F));
            flow.Controls.Add(row2);

            flow.Resize += delegate
            {
                int w = Math.Max(200, flow.ClientSize.Width - 6);
                foreach (Control c in flow.Controls) { if (c is FlowLayoutPanel) c.Width = w; }
            };

            p.Controls.Add(flow);
            return p;
        }

        static FlowLayoutPanel MkRow(int marginBottom)
        {
            return new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, marginBottom),
                Padding = new Padding(0)
            };
        }

        Control BuildStatus()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = C.Panel, Padding = new Padding(14, 0, 10, 0) };
            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = C.Line };
            p.Controls.Add(line);

            stateL = new Label { Text = "● 就绪", ForeColor = C.OnGreen, AutoSize = true, Font = new Font("Microsoft YaHei UI", 8.5F), Location = new Point(14, 6) };
            timerL = new Label { Text = "", ForeColor = C.Dim, AutoSize = true, Font = new Font("Microsoft YaHei UI", 8.5F), Location = new Point(110, 6) };
            workL = new Label { Text = "工作目录  " + work, ForeColor = C.Dim, AutoSize = true, Font = new Font("Microsoft YaHei UI", 8.5F), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(400, 6) };
            p.Controls.Add(stateL); p.Controls.Add(timerL); p.Controls.Add(workL);

            marquee = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Size = new Size(120, 8), Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            p.Controls.Add(marquee);

            p.Resize += delegate
            {
                workL.Left = p.ClientSize.Width - workL.Width - 14;
                marquee.Left = workL.Left - marquee.Width - 14;
                marquee.Top = 9;
            };
            return p;
        }

        static Button MkBtnFixed(string text, Size size, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Size = size,
                FlatStyle = FlatStyle.Flat,
                BackColor = C.Card,
                ForeColor = C.Text,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Font = new Font("Microsoft YaHei UI", 8.5F)
            };
            b.FlatAppearance.BorderColor = C.Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 62);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        static Button MkBtn(string text, Color back, Color fore, EventHandler onClick, float size)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(12, 0, 12, 0),
                Font = new Font("Microsoft YaHei UI", size),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = C.Line;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 62);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 67, 76);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        // ------------------------------------------------------ 日志
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        const int EM_GETFIRSTVISIBLELINE = 0xCE;

        // 用户往上翻看历史时暂停自动滚动，回到底部再恢复
        bool AtBottom()
        {
            try
            {
                if (!log.IsHandleCreated || log.TextLength == 0) return true;
                int first = SendMessage(log.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
                int total = log.Lines.Length;
                int lineH = Math.Max(12, log.Font.Height + 2);
                int visible = Math.Max(1, log.ClientSize.Height / lineH);
                return first >= total - visible - 2;
            }
            catch { return true; }
        }

        void AppendLine(string line, Color col)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, Color>(AppendLine), line, col); }
                catch { }
                return;
            }
            bool scroll = AtBottom();
            int start = log.TextLength;
            log.SelectionStart = start;
            log.SelectionColor = col;
            log.AppendText(line + "\r\n");
            log.SelectionColor = log.ForeColor;
            if (scroll) log.ScrollToCaret();

            if (logWriter != null)
            {
                try { logWriter.WriteLine(line); logWriter.Flush(); } catch { }
            }
        }

        static Color ColorOf(string line)
        {
            string s = line.TrimStart();
            if (s.Length == 0) return C.Log;
            if (s.StartsWith("==") || s.StartsWith("[ .. ]") || s.Contains("== ")) return Color.FromArgb(88, 166, 255);
            if (s.StartsWith("[OK]") || s.Contains("HFSPLUS_OK") || s.Contains("_DONE")) return C.OnGreen;
            if (s.StartsWith("[失败]") || s.StartsWith("ERROR") || s.Contains("ERROR:") ||
                s.Contains("失败") || s.Contains("NEED_")) return C.OnRed;
            if (s.StartsWith("[注意]") || s.StartsWith("[提示]") || s.Contains("WARNING") ||
                s.Contains("警告") || s.Contains("!!")) return C.Amber;
            if (s.StartsWith(">")) return Color.FromArgb(150, 150, 160);
            return C.Log;
        }

        void OnData(string line, bool isErr)
        {
            if (line == null) return;
            AppendLine(line, isErr ? Color.FromArgb(248, 130, 120) : ColorOf(line));
        }

        // ------------------------------------------------------ 运行
        static bool IsDanger(string cmd)
        {
            return cmd == "flash" || cmd == "backup";
        }

        void Run(string cmd, bool danger)
        {
            if (proc != null && !proc.HasExited)
            {
                AppendLine("已有任务在运行中，请先「停止」或等它结束。", C.Amber);
                return;
            }
            if (!File.Exists(Engine.ScriptPath))
            {
                MessageBox.Show("找不到引擎脚本:\n" + Engine.ScriptPath, "缺少文件",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (danger)
            {
                string msg;
                if (cmd == "flash")
                    msg = "即将刷入 userdata 并启动 iOS。\n\n" +
                          "· 手机安卓数据会被清空\n" +
                          "· boot / recovery / modem 不会改动\n" +
                          "· 需要已解锁 Bootloader，并已连接 fastboot 设备\n\n确定继续吗？";
                else
                    msg = "即将读取手机分区做备份。\n\n这是只读操作，不会改动手机。\n\n确定继续吗？";

                if (MessageBox.Show(msg, "确认操作", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }

            try
            {
                string logs = Path.Combine(AppConfig.BaseDir, "logs");
                Directory.CreateDirectory(logs);
                logFile = Path.Combine(logs, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + cmd + ".log");
                logWriter = new StreamWriter(logFile, false, new UTF8Encoding(false));
            }
            catch { logWriter = null; }

            var psi = Engine.CreateInfo(cmd, cfg, work);
            AppendLine("> mi8-ios.ps1 " + cmd, Color.FromArgb(150, 160, 170));
            AppendLine("", C.Log);

            try
            {
                proc = Process.Start(psi);
                // 立即关闭 stdin：不给的话 wsl.exe 会一直等输入（GUI 无控制台场景实测挂死）
                try { proc.StandardInput.Close(); } catch { }
            }
            catch (Exception ex)
            {
                AppendLine("启动失败: " + ex.Message, C.OnRed);
                CloseWriter();
                return;
            }

            running = cmd;
            sw.Reset(); sw.Start();
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { OnData(e.Data, false); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { OnData(e.Data, true); };
            proc.Exited += OnExited;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            SetRunning(true);
        }

        void SetRunning(bool on)
        {
            btnStop.Enabled = on;
            btnNext.Enabled = !on;
            marquee.Visible = on;
            stateL.ForeColor = on ? C.Amber : C.OnGreen;
            stateL.Text = on ? ("● 运行中 · " + CmdTitle(running)) : "● 就绪";
            if (!on) { sw.Stop(); }
        }

        static string CmdTitle(string cmd)
        {
            switch (cmd)
            {
                case "auto": return "一键下一步";
                case "setup": return "装环境";
                case "ipsw": return "下载 IPSW";
                case "rootfs": return "构建根文件系统";
                case "kernel": return "编译 XNU 内核";
                case "build": return "打包引导镜像";
                case "backup": return "备份手机分区";
                case "flash": return "刷入并启动";
                case "demo": return "QEMU 演示";
                case "restore": return "回滚说明";
                default: return cmd;
            }
        }

        void OnExited(object sender, EventArgs e)
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            try { proc.WaitForExit(); } catch { }

            bool needHfs = (running == "setup");
            string cmd = running;

            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    CloseWriter();
                    running = null;
                    SetRunning(false);
                    marquee.Visible = false;
                    double sec = sw.Elapsed.TotalSeconds;
                    string dur = string.Format("{0:00}:{1:00}", (int)sec / 60, (int)sec % 60);

                    AppendLine("", C.Log);
                    if (code == 0)
                    {
                        AppendLine(string.Format("—— 完成 · {0} · 用时 {1} ——", CmdTitle(cmd), dur), C.OnGreen);
                        stateL.ForeColor = C.OnGreen;
                        stateL.Text = "● 完成 · " + CmdTitle(cmd);
                    }
                    else
                    {
                        AppendLine(string.Format("—— 退出码 {0} · 用时 {1} ——", code, dur), C.OnRed);
                        AppendLine("出错说明见上方日志；常见排查：.\\mi8-ios.ps1 doctor", C.Amber);
                        stateL.ForeColor = C.OnRed;
                        stateL.Text = "● 失败 · 退出码 " + code;
                    }
                    timerL.Text = "用时 " + dur + (logFile != null ? "   日志 " + Path.GetFileName(logFile) : "");
                    RefreshStages();
                    if (needHfs) QueryHfsAsync();
                    if (cmd == "flash" && code == 0)
                        MessageBox.Show("刷入完成，手机正在启动 iOS。\n\n首启在苹果 logo 上会停留 4–5 分钟，别急。\n卡住就 电源+音量下 回 fastboot。",
                                        "已下发启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }
            catch { }
        }

        void CloseWriter()
        {
            try { if (logWriter != null) { logWriter.Flush(); logWriter.Close(); logWriter.Dispose(); } }
            catch { }
            logWriter = null;
        }

        void StopRun()
        {
            if (proc == null || proc.HasExited) return;
            if (MessageBox.Show("确定终止当前任务吗？\n（远端 GitHub Actions 编译不会被取消）", "停止",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + proc.Id + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { AppendLine("停止失败: " + ex.Message, C.OnRed); }
        }

        // ------------------------------------------------------ 杂项
        void OpenWork()
        {
            try
            {
                Directory.CreateDirectory(work);
                Process.Start(new ProcessStartInfo { FileName = work, UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("打不开工作目录: " + ex.Message); }
        }

        void OpenSettings()
        {
            using (var f = new SettingsForm(cfg, work))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    cfg = AppConfig.Load();
                    string nw = Engine.ResolveWork(cfg.Work);
                    if (nw != work)
                    {
                        work = nw;
                        RebuildStageRows();
                        workL.Text = "工作目录  " + work;
                        QueryHfsAsync();
                    }
                    RefreshStages();
                    AppendLine("配置已更新。工作目录: " + work, C.OnGreen);
                }
            }
        }

        void RebuildStageRows()
        {
            stages.Clear(); rows.Clear();
            BuildStages();
            if (stageFlow == null) return;
            var toRemove = new List<Control>();
            foreach (Control c in stageFlow.Controls) if (c is StageRow) toRemove.Add(c);
            foreach (Control c in toRemove) stageFlow.Controls.Remove(c);
            foreach (var s in stages)
            {
                var r = new StageRow { Title = s.Name, Width = 236, State = false };
                rows.Add(r);
                stageFlow.Controls.Add(r);
            }
            int w = Math.Max(120, stageFlow.ClientSize.Width - 8);
            foreach (var r in rows) r.Width = w;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win10/11 深色标题栏
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (proc != null && !proc.HasExited)
            {
                var r = MessageBox.Show("任务还在运行，退出会中断它。确定退出吗？", "确认退出",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                try { proc.Kill(); } catch { }
            }
            CloseWriter();
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------ 设置窗体
    sealed class SettingsForm : Form
    {
        TextBox tWork, tBoot;
        CheckBox cYes;
        AppConfig cfg;

        public SettingsForm(AppConfig c, string curWork)
        {
            cfg = c;
            Text = "设置";
            ClientSize = new Size(560, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = C.Bg; ForeColor = C.Text;
            Font = new Font("Microsoft YaHei UI", 9F);

            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null) { using (s) Icon = new Icon(s); }
            }
            catch { }

            var l0 = new Label { Text = "设置", Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), ForeColor = C.Text, AutoSize = true, Location = new Point(20, 14) };
            Controls.Add(l0);

            AddLabel("工作目录（留空 = 自动选择剩余空间 ≥ 60GB 的盘）", 56);
            tWork = AddBox(curWork, 78);
            var b1 = MkBrowse(78, delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择工作目录";
                    if (d.ShowDialog(this) == DialogResult.OK) tWork.Text = d.SelectedPath;
                }
            });
            Controls.Add(b1);

            AddLabel("原厂 boot.img（可选，build 步骤需要）", 118);
            tBoot = AddBox(cfg.BootImg, 140);
            var b2 = MkBrowse(140, delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Title = "选择原厂 boot.img";
                    d.Filter = "Android boot 镜像|boot*.img;*.img|所有文件|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) tBoot.Text = d.FileName;
                }
            });
            Controls.Add(b2);

            cYes = new CheckBox { Text = "跳过二次确认（-Yes）—— 危险，仅建议在只读操作时勾选",
                                  ForeColor = C.Dim, AutoSize = true, Location = new Point(20, 180) };
            cYes.Checked = cfg.Yes;
            Controls.Add(cYes);

            var lEngine = new Label
            {
                Text = "引擎: " + Engine.ScriptPath,
                ForeColor = Color.FromArgb(95, 103, 112),
                Font = new Font("Consolas", 8.5F),
                AutoSize = true,
                Location = new Point(20, 214),
                MaximumSize = new Size(520, 0)
            };
            Controls.Add(lEngine);

            var ok = MkBtn("保存", C.Green, Color.White, delegate { DialogResult = DialogResult.OK; Close(); }, 9.5F);
            var cancel = MkBtn("取消", C.Card, C.Dim, delegate { DialogResult = DialogResult.Cancel; Close(); }, 9.5F);
            ok.Size = new Size(120, 38); cancel.Size = new Size(90, 38);
            ok.Location = new Point(336, 254); cancel.Location = new Point(464, 254);
            Controls.Add(ok); Controls.Add(cancel);

            AcceptButton = ok; CancelButton = cancel;

            ok.Click += delegate
            {
                cfg.Work = tWork.Text.Trim();
                cfg.BootImg = tBoot.Text.Trim();
                cfg.Yes = cYes.Checked;
                cfg.Save();
            };
        }

        void AddLabel(string text, int y)
        {
            var l = new Label { Text = text, ForeColor = C.Dim, AutoSize = true, Location = new Point(20, y) };
            Controls.Add(l);
        }

        TextBox AddBox(string val, int y)
        {
            var t = new TextBox
            {
                Text = val,
                Location = new Point(20, y),
                Width = 450,
                Height = 26,
                BackColor = C.Card,
                ForeColor = C.Text,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(t);
            return t;
        }

        Button MkBrowse(int y, EventHandler onClick)
        {
            var b = new Button
            {
                Text = "浏览…",
                Location = new Point(478, y - 1),
                Size = new Size(60, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = C.Card,
                ForeColor = C.Text,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = C.Line;
            b.Click += onClick;
            return b;
        }

        static Button MkBtn(string text, Color back, Color fore, EventHandler onClick, float size)
        {
            var b = new Button
            {
                Text = text, AutoSize = false, FlatStyle = FlatStyle.Flat, BackColor = back,
                ForeColor = fore, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                Font = new Font("Microsoft YaHei UI", size)
            };
            b.FlatAppearance.BorderColor = C.Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 62);
            b.Click += onClick;
            return b;
        }
    }

    // -------------------------------------------------------------- 入口
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool selftest = false;
            string runCmd = null;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a == "--selftest") selftest = true;
                }
                for (int i = 0; i < args.Length; i++)
                    if (args[i] == "--run" && i + 1 < args.Length) runCmd = args[i + 1];
            }

            if (selftest)
            {
                try
                {
                    var cfg = AppConfig.Load();
                    string work = Engine.ResolveWork(cfg.Work);
                    var psi = Engine.CreateInfo("status", cfg, work);
                    // 重定向给控制台时不能带 Encoding
                    psi.RedirectStandardOutput = false;
                    psi.RedirectStandardError = false;
                    psi.StandardOutputEncoding = null;
                    psi.StandardErrorEncoding = null;
                    psi.CreateNoWindow = false;
                    psi.UseShellExecute = false;
                    var p = Process.Start(psi);
                    try { p.StandardInput.Close(); } catch { }
                    p.WaitForExit(120000);
                    return p.ExitCode;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    return 99;
                }
            }

            Application.Run(new MainForm(args));
            return 0;
        }
    }
}
