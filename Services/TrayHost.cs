using System;
using System.Drawing;
using System.Windows;
using Point = System.Drawing.Point;
using System.Windows.Threading;

namespace VolumeBuddy.Services
{
    /// <summary>
    /// 托盘宿主：启动时只创建托盘图标（不创建 WPF 窗口），点开时才惰性创建窗口，
    /// 从而把冷启动从约 375ms 降到几十毫秒；空闲时再预热窗口，保证第一次点开也很快。
    /// </summary>
    public static class TrayHost
    {
        static System.Windows.Forms.NotifyIcon _tray;

        public static void Init()
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "音量助手",
                Visible = true,
                Icon = MakeIcon(),
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    TogglePanel();
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开面板", null, (s, e) => ShowPanel(false));
            menu.Items.Add("设置", null, (s, e) => ShowPanel(true));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Exit());
            _tray.ContextMenuStrip = menu;
        }

        public static void ShowPanel(bool settings)
        {
            var win = App.EnsureWindow();
            win.ShowFromTray(settings);
        }

        public static void TogglePanel()
        {
            var win = App.EnsureWindow();
            if (win.IsVisible) win.HideFromTray(); else win.ShowFromTray(false);
        }

        /// <summary>空闲预热：后台创建窗口但不显示，让第一次点开没有冷启动感。</summary>
        public static void WarmupLater()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try { App.EnsureWindow(); } catch { }
            };
            t.Start();
        }

        public static void Exit()
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            Environment.Exit(0);
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var accent = new SolidBrush(Color.FromArgb(0, 120, 212)))
                    g.FillEllipse(accent, 1, 1, 30, 30);
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillPolygon(white, new[]
                    {
                        new Point(8, 13), new Point(14, 13),
                        new Point(20, 8), new Point(20, 24),
                        new Point(14, 19), new Point(8, 19),
                    });
                    g.FillRectangle(white, 22, 12, 2, 8);
                    g.FillRectangle(white, 26, 10, 2, 12);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
