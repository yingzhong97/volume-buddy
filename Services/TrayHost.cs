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
        static long _lastHoverTick;
        static bool _warmupDone;

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
            // 鼠标移到托盘图标上、还没按下时，先把空闲 60 秒被 EmptyWorkingSet 换出的热页取回来。
            // 这样「开机后隔很久第一次点」也不用边点边软缺页；1.5s 节流，避免连续 MouseMove 反复刷新。
            _tray.MouseMove += (s, e) =>
            {
                long now = Environment.TickCount64;
                if (now - _lastHoverTick < 1500) return;
                _lastHoverTick = now;
                App.PrewarmForClick();
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

        /// <summary>
        /// 空闲预热。固定等 1200ms 在开机场景下不可靠：用户可能更早点击，也可能这段时间
        /// UI 线程一直有消息让 idle 回调饿着。改成「第一个空闲时机 + 400ms 兜底」谁先到谁执行。
        /// </summary>
        public static void WarmupLater()
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(DoWarmup));
            _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _warmTimer.Tick += (s, e) => { _warmTimer.Stop(); DoWarmup(); };
            _warmTimer.Start();
        }

        static void DoWarmup()
        {
            if (_warmupDone) return;
            _warmupDone = true;
            try { _warmTimer?.Stop(); } catch { }
            try { App.EnsureWindow().Warmup(); } catch { }
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
