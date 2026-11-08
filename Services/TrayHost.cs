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
        static DispatcherTimer _warmTimer;   // 用字段持有，局部 timer 可能还没 tick 就被回收
        static long _lastToggleTick;

        /// <summary>最近一次左键按下托盘图标的时刻（TickCount64），供失焦隐藏判定使用。</summary>
        public static long LastTrayInteractionAt { get; private set; }

        public static void Init()
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "音量助手",
                Visible = true,
                Icon = MakeIcon(),
            };
            // 用 MouseDown 而不是 MouseClick：左键按下托盘图标时面板会立刻失焦，
            // 若等到抬键才切换，失焦自动隐藏已经先跑一步——轻则这次点击被吞掉（关闭慢半拍），
            // 重则隐藏完成后点击才到达、读到 IsVisible=false 又走「显示」分支，面板自己弹回来。
            _tray.MouseDown += (s, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
                LastTrayInteractionAt = Environment.TickCount64;
                long now = Environment.TickCount64;
                if (now - _lastToggleTick < 90) return;   // 过滤同一次物理点击的重复消息
                _lastToggleTick = now;
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
            win.CancelDeferredHide();
            win.ShowFromTray(settings);
        }

        public static void TogglePanel()
        {
            var win = App.EnsureWindow();
            win.CancelDeferredHide();     // 撤销失焦安排的延迟隐藏，本次点击说了算
            // 用显式状态判断，不用 IsVisible：消息排队/退出动画期间它会短暂与用户意图不一致
            if (win.PanelIsOpen) win.HideFromTray(); else win.ShowFromTray(false);
        }

        /// <summary>空闲预热：后台创建窗口但不显示，让第一次点开没有冷启动感。</summary>
        public static void WarmupLater()
        {
            _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _warmTimer.Tick += (s, e) =>
            {
                _warmTimer.Stop();
                try { App.EnsureWindow(); } catch { }
                // 预热后不再立刻 EmptyWorkingSet：刚预热好的热页被换出，第一次点开反而更卡。
                // 内存修剪统一交给 MainWindow 的「空闲 60 秒」计时器。
            };
            _warmTimer.Start();
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
