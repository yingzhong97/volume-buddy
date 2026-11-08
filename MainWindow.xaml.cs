using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VolumeBuddy.Audio;
using VolumeBuddy.Interop;
using VolumeBuddy.Services;

namespace VolumeBuddy
{
    public partial class MainWindow : Window
    {
        AppSettings _settings;
        readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        // 字段持有，别用局部变量：局部 DispatcherTimer 可能还没 tick 就被回收
        // 内存修剪只在长时间空闲时做；原先收起后 400ms 就 trim，快速开关会反复软缺页卡顿
        readonly DispatcherTimer _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        // 失焦后延迟确认再隐藏，避免托盘点击带来的失焦与点击事件互相抢状态
        readonly DispatcherTimer _deferredHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        // 预热时「无感显示一帧」的收尾计时器：让 WPF 先把分层窗口的合成表面建好
        readonly DispatcherTimer _primeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        bool _primed;
        bool _primeShowing;
        Microsoft.Win32.UserPreferenceChangedEventHandler _prefHandler;

        // 行缓存，避免每 500ms 重建导致闪烁和拖动冲突
        readonly Dictionary<string, DeviceRow> _playRows = new Dictionary<string, DeviceRow>();
        readonly Dictionary<string, DeviceRow> _recRows = new Dictionary<string, DeviceRow>();
        readonly Dictionary<int, AppRow> _appRows = new Dictionary<int, AppRow>();

        List<AppSessionInfo> _currentSessions = new List<AppSessionInfo>();
        List<DeviceInfo> _playDevices = new List<DeviceInfo>();
        List<DeviceInfo> _recDevices = new List<DeviceInfo>();
        // 设备显示顺序（按 Id 记录）。切换默认设备时只把新默认抽到最前，其余项整体下移，
        // 相对顺序保持不变；不再每 800ms 用不稳定的 Sort 重洗列表。
        readonly List<string> _playOrder = new List<string>();
        readonly List<string> _recOrder = new List<string>();
        // 刚点击切换、系统还没回读到的默认设备：先按用户意图排序，不用等下一次轮询
        string _pendingPlayDefault, _pendingRecDefault;
        DateTime _pendingPlayAt, _pendingRecAt;
        string _spatialState = "";
        bool _spatialBusy;
        readonly Dictionary<int, ImageSource> _iconCache = new Dictionary<int, ImageSource>();

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            ApplyTheme(ThemeHelper.IsDark());
            ModeTray.IsChecked = _settings.Mode != "window";
            ModeWindow.IsChecked = _settings.Mode == "window";
            ChkAutoStart.IsChecked = _settings.AutoStart;
            ApplyWindowMode();

            _uiTimer.Tick += (s, e) => RefreshAll();
            _trimTimer.Tick += (s, e) =>
            {
                _trimTimer.Stop();
                // 面板确实收起且空闲够久才交还工作集；开关面板/预热一帧期间一律不 trim
                if (!PanelIsOpen && !IsVisible && !_primeShowing) App.TrimWorkingSet();
            };
            _primeTimer.Tick += (s, e) => FinishPrimeShow();
            _deferredHideTimer.Tick += (s, e) =>
            {
                _deferredHideTimer.Stop();
                if (_shotMode || !PanelIsOpen || !IsVisible || IsActive) return;
                if (_settings.Mode != "tray") return;
                // 刚弹出时常常还没真正拿到前台权限，等过保护期再确认
                if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 450)
                {
                    _deferredHideTimer.Start();
                    return;
                }
                // 按下托盘图标本身会让面板失焦，这次失焦不该再补一刀，开关交给托盘点击处理
                if (Environment.TickCount64 - TrayHost.LastTrayInteractionAt < 250) return;
                UiLog("deferred deactivate -> hide");
                HideToTray();
            };
            _prefHandler = (s, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                    Dispatcher.Invoke(() => ApplyTheme(ThemeHelper.IsDark()));
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += _prefHandler;

            try
            {
                var since = DateTime.Now - Process.GetCurrentProcess().StartTime;
                UiLog("window created in " + (int)since.TotalMilliseconds + " ms");
            }
            catch { }
            // 关键：在构造阶段就把 HWND 建好。否则第一次 Show() 时才创建 HWND，
            // ShowPanel 里的 DPI 换算会拿到兜底值 1.0，首次弹出的位置和后续不一致。
            try { new WindowInteropHelper(this).EnsureHandle(); }
            catch (Exception ex) { UiLog("EnsureHandle " + ex.Message); }

            // 预热后窗口一直没显示也能回收内存，但留足 60 秒，别影响刚启动时的第一次点开
            _trimTimer.Start();
        }

        /// <summary>
        /// 空闲预热：把 HWND、布局、设备行和分层窗口的合成表面都在用户点击之前建好。
        /// 「无感显示一帧」是重点——WPF 的合成表面和字体/模板缓存只在 Show 之后才真正分配，
        /// 放到预热里做，第一次点击就和后续点击走同一条路径。
        /// </summary>
        public void Warmup()
        {
            if (_primed || PanelIsOpen) return;
            _primed = true;
            try { UpdateLayout(); }
            catch (Exception ex) { UiLog("warmup layout " + ex.Message); }

            try
            {
                _primeShowing = true;
                IsHitTestVisible = false;       // 这一帧不吃鼠标
                InnerBorder.Opacity = 0;        // 内容层全透明，窗口本身也不会露出来
                if (_settings.Mode == "tray") PositionNearTray();
                Show();                         // ShowActivated=False / ShowInTaskbar=False，不抢焦点
                ApplyNativePosition();
                UpdateLayout();
                RefreshAll();                   // 预建设备行 + 预热 WASAPI/会话枚举的 JIT 与缓存
                _primeTimer.Start();
                UiLog("warmup prime frame");
            }
            catch (Exception ex)
            {
                UiLog("warmup prime EXCEPTION " + ex);
                FinishPrimeShow();
            }
        }

        /// <summary>结束预热的一帧无感显示；ShowPanel 开头也会调用，避免极快点击被这帧 Hide 掉。</summary>
        void FinishPrimeShow()
        {
            if (!_primeShowing) return;
            _primeShowing = false;
            _primeTimer.Stop();
            try { if (IsVisible) Hide(); } catch { }
            IsHitTestVisible = true;
            InnerBorder.Opacity = 1;
        }

        /// <summary>
        /// 鼠标悬停托盘时调用：把 60 秒空闲修剪换出的热页提前取回（软缺页不发生在点击那一瞬）。
        /// 之后重新计时，空闲够久仍会再交还工作集，常驻内存不变。
        /// </summary>
        public void TouchHotSet()
        {
            if (PanelIsOpen || _primeShowing || _shotMode) return;
            try
            {
                UpdateLayout();
                RefreshAll();
                _trimTimer.Stop();
                _trimTimer.Start();
            }
            catch (Exception ex) { UiLog("touch hot set " + ex.Message); }
        }

        // ================= 主题 =================

        void ApplyTheme(bool dark)
        {
            // 注意：这里必须是**不透明**色。窗口用的是 AllowsTransparency + WPF 圆角，
            // 一旦允许桌面透出（或叠加亚克力/模糊），露出的那一层会按窗口矩形填充，圆角就变成直角。
            Color WindowBg() => dark ? Color.FromRgb(0x20, 0x22, 0x26) : Color.FromRgb(0xF7, 0xF8, 0xFA);
            var res = Resources;
            res["BgBrush"] = new SolidColorBrush(WindowBg());
            res["CardBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x30, 0x35) : Colors.White);
            res["TabBgBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x24, 0x26, 0x2A) : Color.FromRgb(0xE9, 0xEB, 0xEE));
            res["TextBrush"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1B, 0x1B, 0x1B));
            res["SubBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xA6, 0xAC, 0xB5) : Color.FromRgb(0x5F, 0x66, 0x70));
            res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            res["HoverBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            res["SliderTrackBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x44, 0x48, 0x4E) : Color.FromRgb(0xD8, 0xDC, 0xE0));
            res["WindowBorderBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x44, 0x00, 0x00, 0x00));
            res["CardBorderBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            res["DividerBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            res["RowActiveBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2F, 0x35, 0x3D) : Color.FromRgb(0xEA, 0xF1, 0xFA));
            res["PressBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            res["ScrollThumbBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x47, 0x00, 0x00, 0x00));
            // 滑块拇指的描边：深色下用卡片色，浅色下用白，保证与所在背景分离
            res["ThumbRingBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x30, 0x35) : Colors.White);
        }

        // ================= 亚克力背景 =================
        // 已停用：SetWindowCompositionAttribute 的亚克力会把整个窗口矩形涂满（含圆角外侧的四个角），
        // 于是屏幕上看是直角。WPF 自己画的圆角是带 alpha 的（截图验证过），所以只要背景不透明，
        // 圆角就能正确显示。0xE6 的透光量本来也几乎看不出模糊，去掉后观感几乎不变。
        // 若以后想恢复磨砂，正确做法是改用 DWMWA_SYSTEMBACKDROP_TYPE + DwmSetWindowAttribute(33, ROUND)，
        // 由 DWM 负责裁剪，别再叠加 SetWindowCompositionAttribute。

        // ================= 托盘 =================

        // ================= 显示 / 隐藏 =================

        /// <summary>
        /// 面板的「意图状态」：开始显示立刻置 true，开始收起立刻置 false。
        /// 不能用 IsVisible 代替——消息排队或动画期间它会短暂与用户意图不一致，
        /// 托盘点击就会读到旧状态，出现「关不掉」或「关掉又弹回来」。
        /// </summary>
        public bool PanelIsOpen { get; private set; }

        /// <summary>撤销失焦安排的延迟隐藏；托盘点击的优先级更高。</summary>
        public void CancelDeferredHide() { _deferredHideTimer.Stop(); }

        void ApplyWindowMode()
        {
            bool trayMode = _settings.Mode != "window";
            ShowInTaskbar = !trayMode;
            Topmost = trayMode;
        }

        public void ShowFromTray(bool openSettings)
        {
            ShowPanel();
            if (openSettings) ShowSettings();
        }

        public void HideFromTray() { HideToTray(); }

        static void UiLog(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "volumebuddy_ui.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        void TogglePanel()
        {
            CancelDeferredHide();
            if (PanelIsOpen) HideToTray();
            else ShowPanel();
        }

        /// <summary>从下方淡入并放大（托盘弹出感）。动画只负责视觉，不参与开关状态判断。</summary>
        void AnimateIn()
        {
            StopAnimations();
            var dur = TimeSpan.FromMilliseconds(210);
            var ease = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
            // 动画打在内容层上，不要动 Window.Opacity：对分层窗口逐帧改整体 alpha 会让
            // WPF 反复重建渲染表面，反复开关面板时内存只涨不跌
            var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(18, 0, dur) { EasingFunction = ease };
            var popX = new DoubleAnimation(0.955, 1, dur) { EasingFunction = ease };
            var popY = new DoubleAnimation(0.955, 1, dur) { EasingFunction = ease };
            // 帧率上限 180fps：给足刷新率上限，动画跟屏走
            Timeline.SetDesiredFrameRate(fade, 180);
            Timeline.SetDesiredFrameRate(slide, 180);
            Timeline.SetDesiredFrameRate(popX, 180);
            Timeline.SetDesiredFrameRate(popY, 180);
            InnerBorder.BeginAnimation(OpacityProperty, fade);
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, slide);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, popX);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, popY);
        }

        /// <summary>
        /// 向下淡出后隐藏。状态在点击瞬间就已置为「收起」，动画期间 IsVisible 仍是 true
        /// 也不会误判：托盘再点一次会立刻取消本动画并重播入场。
        /// </summary>
        void AnimateOut(Action onDone)
        {
            var dur = TimeSpan.FromMilliseconds(160);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(0, 12, dur) { EasingFunction = ease };
            var popX = new DoubleAnimation(1, 0.975, dur) { EasingFunction = ease };
            var popY = new DoubleAnimation(1, 0.975, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, 180);
            Timeline.SetDesiredFrameRate(slide, 180);
            Timeline.SetDesiredFrameRate(popX, 180);
            Timeline.SetDesiredFrameRate(popY, 180);
            fade.Completed += (s, e) => onDone();
            InnerBorder.BeginAnimation(OpacityProperty, fade);
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, slide);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, popX);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, popY);
        }

        void StopAnimations()
        {
            InnerBorder.BeginAnimation(OpacityProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, null);
            InnerBorder.Opacity = 1;
            PanelScale.ScaleX = PanelScale.ScaleY = 1;
            PanelOffset.Y = 0;
        }

        void ShowPanel()
        {
            try
            {
                FinishPrimeShow();      // 预热的无感一帧还没结束时用户就点了：先收尾，别让它把面板 Hide 掉
                CancelDeferredHide();
                UiLog("ShowPanel enter open=" + PanelIsOpen + " visible=" + IsVisible);
                ApplyWindowMode();

                if (_settings.Mode == "tray")
                {
                    PositionNearTray();
                }
                else
                {
                    _hasTargetPos = false;   // 独立窗口模式由 WPF 居中，不需要物理坐标兜底
                    var wa = SystemParameters.WorkArea;
                    Left = wa.Left + (wa.Width - Width) / 2;
                    Top = wa.Top + (wa.Height - Height) / 2;
                }

                if (!IsVisible) Show();
                ApplyNativePosition();   // Show() 可能按 WPF 自己的 DPI 再应用一次 Left/Top，收尾钉回物理坐标
                PanelIsOpen = true;      // 先定状态，后续失焦/动画都不会误判
                ++_animSeq;              // 作废上一轮退出动画的完成回调
                if (!_envLogged)
                {
                    _envLogged = true;
                    var dpiInfo = VisualTreeHelper.GetDpi(this);
                    UiLog("env renderTier=" + (RenderCapability.Tier >> 16) +
                          " dpiScale=" + dpiInfo.DpiScaleX.ToString("0.##") +
                          " gcHeap=" + (GC.GetTotalMemory(false) / 1024 / 1024) + "MB");
                }
                _shownAt = DateTime.UtcNow;
                AnimateIn();            // 关键：每次显示都重置透明度并播放入场动画
                ForceForeground();
                _uiTimer.Start();
                _trimTimer.Stop();
                // 先让窗口上屏，再把耗时的 WASAPI 枚举/会话刷新排到空闲时执行，
                // 避免点击后 UI 线程被同步枚举占住，出现「点一下卡一下」
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (!PanelIsOpen) return;
                    RefreshAll();
                    RefreshSpatial();
                }));
                UiLog("ShowPanel done visible=" + IsVisible + " opacity=" + Opacity +
                      " rect=" + (int)Left + "," + (int)Top + "," + (int)Width + "x" + (int)Height);
            }
            catch (Exception ex)
            {
                UiLog("ShowPanel EXCEPTION " + ex);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                        int x, int y, int cx, int cy, uint flags);

        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        int _targetLeftPx, _targetTopPx;
        bool _hasTargetPos;

        /// <summary>
        /// 贴任务栏上沿定位。几何运算全部在物理像素里完成，DPI 只取自「光标所在显示器」
        /// （GetDpiForMonitor），不取 PresentationSource —— 后者在首次 Show 之前可能为 null，
        /// 正是以前「第一次弹出的位置和后续不一致」的根因。
        /// 工作区同样取光标所在显示器，多屏/缩放下也不会被主屏边界夹回来。
        /// </summary>
        void PositionNearTray()
        {
            var pos = System.Windows.Forms.Cursor.Position;   // 物理像素
            var hMon = MonitorFromPoint(new POINT { X = pos.X, Y = pos.Y }, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            bool hasMon = hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref mi);

            double sx = 1, sy = 1;      // DIP / 物理像素
            uint dx = 96, dy = 96;
            bool gotDpi = false;
            try { gotDpi = hasMon && GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dx, out dy) >= 0 && dx > 0 && dy > 0; }
            catch { }                   // 老系统没有 shcore.dll 时退回 PresentationSource
            if (gotDpi)
            {
                sx = 96.0 / dx; sy = 96.0 / dy;
            }
            else
            {
                var src = PresentationSource.FromVisual(this);
                double a = src?.CompositionTarget.TransformFromDevice.M11 ?? 0;
                double b = src?.CompositionTarget.TransformFromDevice.M22 ?? 0;
                if (a > 0 && b > 0) { sx = a; sy = b; }
            }

            // 窗口实际物理尺寸直接问 HWND，隐藏状态下也有效，完全绕开 WPF 的 DIP 换算差异
            double wDip = Width, hDip = Height;
            if (double.IsNaN(wDip) || wDip <= 0) wDip = 1000;
            if (double.IsNaN(hDip) || hDip <= 0) hDip = 620;
            double wPx = wDip / sx, hPx = hDip / sy;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rc))
            {
                double rw = rc.Right - rc.Left, rh = rc.Bottom - rc.Top;
                if (rw > 0 && rh > 0) { wPx = rw; hPx = rh; }
            }

            double workLeft, workTop, workRight, workBottom;
            if (hasMon)
            {
                workLeft = mi.rcWork.Left; workTop = mi.rcWork.Top;
                workRight = mi.rcWork.Right; workBottom = mi.rcWork.Bottom;
            }
            else
            {
                var wa = SystemParameters.WorkArea;   // DIP，仅作兜底
                workLeft = wa.Left / sx; workTop = wa.Top / sy;
                workRight = wa.Right / sx; workBottom = wa.Bottom / sy;
            }

            double gapX = 8 / sx, gapY = 8 / sy;
            double leftPx = pos.X + 56 - wPx;      // 面板右缘落在光标右侧 56 物理像素处
            double minX = workLeft + gapX;
            double maxX = Math.Max(minX, workRight - wPx - gapX);
            leftPx = Math.Min(Math.Max(leftPx, minX), maxX);

            // 任务栏自动隐藏时工作区底部就是屏幕底，需额外抬高，避免遮住弹出的托盘图标
            double liftPx = (IsTaskbarAutoHide() ? 60 : 8) / sy;
            double topPx = workBottom - hPx - liftPx;
            double minY = workTop + gapY;
            double maxY = Math.Max(minY, workBottom - hPx - gapY);
            topPx = Math.Min(Math.Max(topPx, minY), maxY);

            Left = leftPx * sx;         // 同步 WPF 的内部状态（拖动标题栏时要用）
            Top = topPx * sy;
            _targetLeftPx = (int)Math.Round(leftPx);
            _targetTopPx = (int)Math.Round(topPx);
            _hasTargetPos = true;
            ApplyNativePosition();     // 最终位置用物理像素钉死，不受窗口当前 DPI 影响
        }

        void ApplyNativePosition()
        {
            if (!_hasTargetPos) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, IntPtr.Zero, _targetLeftPx, _targetTopPx, 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnClosed(EventArgs e)
        {
            PanelIsOpen = false;
            _uiTimer.Stop();
            _deferredHideTimer.Stop();
            _trimTimer.Stop();
            _primeTimer.Stop();
            try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _prefHandler; } catch { }
            base.OnClosed(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        const uint ABM_GETSTATE = 0x00000004;
        const int ABS_AUTOHIDE = 0x00000001;

        /// <summary>任务栏是否设置为自动隐藏（此时工作区会把任务栏区域也算进去）。</summary>
        static bool IsTaskbarAutoHide()
        {
            try
            {
                var data = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
                IntPtr ret = SHAppBarMessage(ABM_GETSTATE, ref data);
                return (ret.ToInt64() & ABS_AUTOHIDE) != 0;
            }
            catch { return false; }
        }

        void ForceForeground()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetForegroundWindow(hwnd);
                Activate();
                Focus();
            }
            catch { }
        }

        int _animSeq;   // 每次开关自增，让被取消的旧动画完成回调自动失效

        void HideToTray()
        {
            CancelDeferredHide();
            UiLog("HideToTray open=" + PanelIsOpen + " visible=" + IsVisible);
            if (!PanelIsOpen) return;                // 已收起：可能正在播退出动画，不重播
            PanelIsOpen = false;                    // 意图立刻生效，动画期间再点托盘会立刻重开
            int seq = ++_animSeq;
            _uiTimer.Stop();
            _trimTimer.Stop();
            _trimTimer.Start();                     // 空闲 60 秒后才交还工作集
            // COM 句柄释放放到空闲时做：不占用 UI 线程，下一次点击不用排队。
            // 只看状态不看 IsVisible——退出动画期间窗口还可见，但已经与 UI 无关了。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (PanelIsOpen) return;
                try
                {
                    foreach (var r in _appRows.Values) r.App = null;   // 句柄即将释放，别留着被误用
                    AudioSessionService.Release(_currentSessions);
                    _currentSessions.Clear();
                    _playDevices.Clear();
                    _recDevices.Clear();
                }
                catch { }
            }));
            AnimateOut(() =>
            {
                if (seq != _animSeq || PanelIsOpen) return;   // 用户中途又打开了面板
                Hide();
                StopAnimations();                             // 复位变换，下次入场从干净状态开始
                UiLog("AnimateOut done -> hidden");
            });
        }

        bool _shotMode;
        bool _envLogged;

        DateTime _shownAt = DateTime.MinValue;

        void Window_Deactivated(object sender, EventArgs e)
        {
            if (_shotMode || !PanelIsOpen) return;
            if (_settings.Mode != "tray") return;
            // 延迟确认：按下托盘图标本身会让面板失焦，这次失焦不能决定开关状态，
            // 托盘点击已在 MouseDown 同步完成切换，并会取消这个计时器；
            // 点击其它窗口时，120ms 后由计时器真正收起面板。
            _deferredHideTimer.Stop();
            _deferredHideTimer.Start();
        }

        /// <summary>调试用：把当前界面渲染成 PNG 后退出。</summary>
        public void CaptureTo(string path, bool withSettings = false)
        {
            _shotMode = true;
            Show();
            UpdateLayout();
            RefreshAll();
            RefreshSpatial();
            if (withSettings) ShowSettings();
            // 等空间音效异步读取完成后再截图
            var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            shotTimer.Tick += (s2, e2) =>
            {
                shotTimer.Stop();
                Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    int w = (int)Math.Max(1, ActualWidth), h = (int)Math.Max(1, ActualHeight);
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(this);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                }
                catch { }
                    Environment.Exit(0);
                }, DispatcherPriority.ContextIdle);
            };
            shotTimer.Start();
        }

        void ExitApp()
        {
            _uiTimer.Stop();
            TrayHost.Exit();
        }

        // ================= 刷新 =================

        string _lastPlayDef;

        /// <summary>系统回读还没跟上时用刚点击的设备顶一会儿；2 秒后无论成功与否都以系统值为准。</summary>
        static string ResolveDefault(string systemDefault, ref string pending, ref DateTime pendingAt)
        {
            if (pending == null) return systemDefault;
            if (string.Equals(systemDefault, pending, StringComparison.OrdinalIgnoreCase) ||
                (DateTime.UtcNow - pendingAt).TotalMilliseconds > 2000)
            {
                pending = null;
                return systemDefault;
            }
            return pending;
        }

        /// <summary>
        /// 基于上一次的显示顺序重排：下线设备移除、新设备追加到末尾；
        /// 默认设备只把自己从当前位置抽出来插到最前，其余项依次下移、相对顺序不变。
        /// </summary>
        static List<DeviceInfo> StableOrder(List<DeviceInfo> fresh, List<string> order)
        {
            var byId = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in fresh) byId[d.Id] = d;

            order.RemoveAll(id => !byId.ContainsKey(id));
            foreach (var d in fresh)
                if (!order.Contains(d.Id)) order.Add(d.Id);

            var def = fresh.FirstOrDefault(d => d.IsDefault);
            if (def != null)
            {
                int at = order.FindIndex(id => string.Equals(id, def.Id, StringComparison.OrdinalIgnoreCase));
                if (at > 0) { order.RemoveAt(at); order.Insert(0, def.Id); }
            }

            var result = new List<DeviceInfo>(order.Count);
            foreach (var id in order)
                if (byId.TryGetValue(id, out var d)) result.Add(d);
            return result;
        }

        int _dragCount;
        // 用户刚改过音量的设备：短时间内不用系统读值覆盖滑条，避免回弹
        readonly Dictionary<string, (DateTime at, double value)> _recentSet =
            new Dictionary<string, (DateTime, double)>();

        bool RecentlySet(string id, double currentPercent)
        {
            if (!_recentSet.TryGetValue(id, out var r)) return false;
            if ((DateTime.UtcNow - r.at).TotalMilliseconds > 1200) { _recentSet.Remove(id); return false; }
            return Math.Abs(r.value - currentPercent) < 3;
        }

        void RefreshAll()
        {
            if (_dragCount > 0) return;   // 正在拖动时让出 UI 线程，保证顺滑
            try
            {
                string playDef = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                string recDef = AudioService.GetDefaultDeviceId(EDataFlow.eCapture);
                playDef = ResolveDefault(playDef, ref _pendingPlayDefault, ref _pendingPlayAt);
                recDef = ResolveDefault(recDef, ref _pendingRecDefault, ref _pendingRecAt);
                _playDevices = StableOrder(AudioService.GetDevices(EDataFlow.eRender, playDef), _playOrder);
                _recDevices = StableOrder(AudioService.GetDevices(EDataFlow.eCapture, recDef), _recOrder);
                UpdateDevicePanel(PlayDevices, _playRows, _playDevices, true);
                UpdateDevicePanel(RecDevices, _recRows, _recDevices, false);

                if (playDef != _lastPlayDef)
                {
                    _lastPlayDef = playDef;
                    RefreshSpatial();
                }

                var sessions = AudioSessionService.GetSessions();
                UpdateMixerPanel(sessions);
                AudioSessionService.Release(_currentSessions);
                _currentSessions = sessions;
            }
            catch { }
        }

        // ================= 设备面板 =================

        /// <summary>可点击编辑的百分比文本：点击数字变成输入框，回车/Esc 结束。</summary>
        class PercentEditor
        {
            public Grid Host;
            public TextBlock Text;
            public TextBox Box;
            public bool IsEditing { get { return Box.Visibility == Visibility.Visible; } }
            public void ShowText(string value) { if (!IsEditing) Text.Text = value; }
        }

        PercentEditor MakePercentEditor(double initialValue, Func<double> currentGetter, Action<double> applyPercent)
        {
            var host = new Grid { MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock
            {
                Text = Math.Round(initialValue) + "%",
                FontSize = 11,
                TextAlignment = TextAlignment.Right,
                Cursor = Cursors.Hand,
                ToolTip = "点击输入具体数值",
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");

            var box = new TextBox
            {
                Text = Math.Round(initialValue).ToString("0"),
                Width = 52,
                FontSize = 11,
                TextAlignment = TextAlignment.Right,
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(3, 0, 3, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };
            box.SetResourceReference(TextBox.BackgroundProperty, "CardBrush");
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush");

            host.Children.Add(text);
            host.Children.Add(box);

            bool closing = false;
            void Close(bool commit)
            {
                if (closing) return;
                closing = true;
                try
                {
                    if (commit)
                    {
                        string raw = (box.Text ?? "").Trim().TrimEnd('%', '％', ' ');
                        if (double.TryParse(raw, out double v))
                            applyPercent(Math.Clamp(v, 0, 100));
                    }
                }
                finally
                {
                    box.Visibility = Visibility.Collapsed;
                    text.Visibility = Visibility.Visible;
                    closing = false;
                }
            }

            text.MouseLeftButtonUp += (s2, e2) =>
            {
                box.Text = Math.Round(currentGetter()).ToString("0");
                text.Visibility = Visibility.Collapsed;
                box.Visibility = Visibility.Visible;
                box.Focus();
                box.SelectAll();
                e2.Handled = true;
            };
            box.KeyDown += (s2, e2) =>
            {
                if (e2.Key == Key.Enter) { Close(true); e2.Handled = true; }
                else if (e2.Key == Key.Escape) { Close(false); e2.Handled = true; }
            };
            box.LostFocus += (s2, e2) => Close(true);

            return new PercentEditor { Host = host, Text = text, Box = box };
        }

        class DeviceRow
        {
            public Border Card;
            public TextBlock NameText;
            public Border Badge;
            public Slider Slider;
            public PercentEditor Percent;
            public System.Windows.Controls.Button MuteBtn;
            public bool Dragging;
            public SolidColorBrush BgBrush;
            public TranslateTransform Offset;
            public bool IsDefault;
        }

        void UpdateDevicePanel(StackPanel panel, Dictionary<string, DeviceRow> rows,
                               List<DeviceInfo> devices, bool isPlay)
        {
            // 记录旧位置（用于补间动画）
            var oldTops = new Dictionary<string, double>();
            foreach (var kv in rows)
            {
                if (kv.Value.Card.Parent != panel) continue;
                kv.Value.Offset.BeginAnimation(TranslateTransform.YProperty, null);
                kv.Value.Offset.Y = 0;
                // 预热/悬停时窗口是隐藏的，坐标换算可能不可用；取不到就不做位移动画即可
                try { oldTops[kv.Key] = kv.Value.Card.TransformToAncestor(panel).Transform(new Point(0, 0)).Y; }
                catch { }
            }

            // 移除已消失的设备行
            var validIds = new HashSet<string>(devices.Select(d => d.Id));
            foreach (var dead in rows.Where(kv => !validIds.Contains(kv.Key)).ToList())
            {
                panel.Children.Remove(dead.Value.Card);
                rows.Remove(dead.Key);
            }

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (!rows.TryGetValue(d.Id, out var row))
                {
                    row = BuildDeviceRow(panel, d, isPlay);
                    rows[d.Id] = row;
                    panel.Children.Insert(Math.Min(i, panel.Children.Count), row.Card);
                }
                else
                {
                    // 位置不对（例如默认设备换到了最前）则调整顺序
                    int idx = panel.Children.IndexOf(row.Card);
                    if (idx != i)
                    {
                        panel.Children.Remove(row.Card);
                        panel.Children.Insert(Math.Min(i, panel.Children.Count), row.Card);
                    }
                }

                row.Badge.Visibility = d.IsDefault ? Visibility.Visible : Visibility.Collapsed;
                if (row.IsDefault != d.IsDefault)
                {
                    row.IsDefault = d.IsDefault;
                    SetRowActive(row, d.IsDefault, true);
                }
                if (!row.Dragging)
                {
                    _suppressSliderEcho = true;
                    if (!VolumeWriter.HasPending(d.Id) && !RecentlySet(d.Id, row.Slider.Value) &&
                        Math.Abs(row.Slider.Value - d.Volume * 100) > 0.5) row.Slider.Value = d.Volume * 100;
                    row.Percent.ShowText(Math.Round(d.Volume * 100) + "%");
                    SetMuteVisual(row.MuteBtn, d.Muted);
                    _suppressSliderEcho = false;
                }
            }

            // 顺序变化时做滑动动画
            panel.UpdateLayout();
            foreach (var kv in rows)
            {
                if (kv.Value.Card.Parent != panel) continue;
                if (!oldTops.TryGetValue(kv.Key, out double oldY)) continue;
                double newY;
                try { newY = kv.Value.Card.TransformToAncestor(panel).Transform(new Point(0, 0)).Y; }
                catch { continue; }
                double delta = oldY - newY;
                if (Math.Abs(delta) < 0.5) continue;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                kv.Value.Offset.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            }
        }

        /// <summary>设置设备行的高亮：默认设备铺一层淡色底，其余透明（不再整体降透明度，避免文字发灰）。</summary>
        void SetRowActive(DeviceRow row, bool active, bool animate)
        {
            Color bgColor = active ? ((SolidColorBrush)Resources["RowActiveBrush"]).Color : Colors.Transparent;

            if (!animate)
            {
                row.BgBrush.Color = bgColor;
                return;
            }
            var ease = new PowerEase { Power = 2, EasingMode = EasingMode.EaseOut };
            row.BgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(bgColor, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }

        // Segoe MDL2 图标（与标题栏图标同一套字形，别用彩色 emoji）
        const string GlyphVolume = "\uE767";
        const string GlyphMute = "\uE74F";
        const string GlyphSpeaker = "\uE767";

        static void SetMuteVisual(System.Windows.Controls.Button btn, bool muted)
        {
            btn.Content = muted ? GlyphMute : GlyphVolume;
            btn.Opacity = muted ? 1.0 : 0.55;
        }

        DeviceRow BuildDeviceRow(StackPanel panel, DeviceInfo d, bool isPlay)
        {
            var row = new DeviceRow();
            var nameText = new TextBlock
            {
                Text = d.Name,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                MaxHeight = 38,      // 最多两行
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var badge = new Border
            {
                Background = (Brush)Resources["AccentBrush"],
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                Child = new TextBlock { Text = "默认", FontSize = 10, Foreground = Brushes.White },
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };
            var nameArea = new Grid();
            nameArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            badge.Margin = new Thickness(6, 2, 0, 0);
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(badge, 1);
            nameArea.Children.Add(nameText);
            nameArea.Children.Add(badge);
            nameArea.Cursor = Cursors.Hand;
            ToolTipService.SetToolTip(nameArea, "点击切换为默认设备");
            nameArea.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    AudioService.SetDefaultDevice(d.Id);
                    // 先按用户意图把这一项置顶，不等系统回读/下一次 800ms 轮询
                    if (isPlay) { _pendingPlayDefault = d.Id; _pendingPlayAt = DateTime.UtcNow; }
                    else { _pendingRecDefault = d.Id; _pendingRecAt = DateTime.UtcNow; }
                }
                catch { }
                RefreshAll();
            };

            var muteBtn = new System.Windows.Controls.Button
            {
                Content = GlyphVolume,
                Style = (Style)Resources["MuteBtn"],
                Opacity = 0.55,
            };
            muteBtn.Click += (s, e) =>
            {
                bool mute = !Equals(muteBtn.Content, GlyphMute);
                VolumeWriter.SetMute(d.Id, mute);
                SetMuteVisual(muteBtn, mute);
            };

            var slider = new Slider { Minimum = 0, Maximum = 100, Style = (Style)Resources["ModernSlider"], Value = d.Volume * 100 };
            PercentEditor percent = null;
            percent = MakePercentEditor(d.Volume * 100, () => slider.Value, v =>
            {
                _suppressSliderEcho = true;
                slider.Value = v;
                _suppressSliderEcho = false;
                _recentSet[d.Id] = (DateTime.UtcNow, v);
                VolumeWriter.SetVolume(d.Id, (float)(v / 100.0));
                percent.ShowText(Math.Round(v) + "%");
            });

            bool[] dragging = new bool[1];
            slider.AddHandler(Thumb.DragStartedEvent, (RoutedEventHandler)((s, e) =>
            { dragging[0] = row.Dragging = true; _dragCount++; }), true);
            slider.AddHandler(Thumb.DragCompletedEvent, (RoutedEventHandler)((s, e) =>
            { dragging[0] = row.Dragging = false; if (_dragCount > 0) _dragCount--; }), true);
            slider.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEcho) return;
                percent.ShowText(Math.Round(e.NewValue) + "%");
                _recentSet[d.Id] = (DateTime.UtcNow, e.NewValue);
                // 写音量在后台线程完成，UI 线程零阻塞
                VolumeWriter.SetVolume(d.Id, (float)(e.NewValue / 100.0));
            };

            row.Slider = slider; row.Percent = percent; row.MuteBtn = muteBtn; row.NameText = nameText; row.Badge = badge;

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(nameArea, 0);
            Grid.SetColumn(muteBtn, 1);
            top.Children.Add(nameArea);
            top.Children.Add(muteBtn);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 0);
            Grid.SetColumn(percent.Host, 1);
            bottom.Margin = new Thickness(0, 2, 0, 0);
            bottom.Children.Add(slider);
            bottom.Children.Add(percent.Host);

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(bottom);

            row.BgBrush = new SolidColorBrush(Colors.Transparent);
            row.Offset = new TranslateTransform();
            row.Card = new Border
            {
                Background = row.BgBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                RenderTransform = row.Offset,
                Child = stack,
            };
            // 非选中行悬停时轻微高亮
            row.Card.MouseEnter += (s2, e2) =>
            {
                if (!row.IsDefault)
                    row.BgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation((Color)((SolidColorBrush)Resources["HoverBrush"]).Color,
                            TimeSpan.FromMilliseconds(120)));
            };
            row.Card.MouseLeave += (s2, e2) =>
            {
                if (!row.IsDefault)
                    row.BgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(120)));
            };
            return row;
        }

        bool _suppressSliderEcho;

        // ================= 混音器面板 =================

        class AppRow
        {
            public Border Card;
            public Slider Slider;
            public PercentEditor Percent;
            public AppSessionInfo App;
            public System.Windows.Controls.Button MuteBtn;
            public bool Dragging;
            public DateTime LastApply;
        }

        void UpdateMixerPanel(List<AppSessionInfo> apps)
        {
            MixHint.Text = "应用音量";

            // 空列表提示
            if (apps.Count == 0)
            {
                if (MixApps.Children.Count == 0)
                {
                    var empty = new TextBlock
                    {
                        Text = "当前没有正在发声的应用",
                        FontSize = 11,
                        Margin = new Thickness(10, 4, 10, 0),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
                    empty.Tag = "empty";
                    MixApps.Children.Add(empty);
                }
                return;
            }
            for (int k = MixApps.Children.Count - 1; k >= 0; k--)
                if (MixApps.Children[k] is TextBlock tb && "empty".Equals(tb.Tag)) MixApps.Children.RemoveAt(k);

            var validKeys = new HashSet<int>(apps.Select(a => a.Pid));
            foreach (var dead in _appRows.Where(kv => !validKeys.Contains(kv.Key)).ToList())
            {
                MixApps.Children.Remove(dead.Value.Card);
                _appRows.Remove(dead.Key);
            }

            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                if (!_appRows.TryGetValue(app.Pid, out var row))
                {
                    row = BuildAppRow(MixApps, app);
                    _appRows[app.Pid] = row;
                    MixApps.Children.Insert(Math.Min(i, MixApps.Children.Count), row.Card);
                }

                row.App = app;   // 每次刷新替换为最新句柄
                row.Card.Opacity = app.Active ? 1.0 : 0.68;
                SetMuteVisual(row.MuteBtn, app.Muted);
                if (!row.Dragging)
                {
                    _suppressSliderEcho = true;
                    if (Math.Abs(row.Slider.Value - app.Volume * 100) > 0.5) row.Slider.Value = app.Volume * 100;
                    row.Percent.ShowText(Math.Round(app.Volume * 100) + "%");
                    _suppressSliderEcho = false;
                }
            }
        }

        AppRow BuildAppRow(StackPanel panel, AppSessionInfo app)
        {
            var row = new AppRow();

            var iconSrc = TryGetAppIcon(app.Pid, app.ExePath);
            var icon = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (iconSrc != null)
            {
                icon.Child = new Image { Source = iconSrc, Stretch = Stretch.Uniform };
            }
            else
            {
                // 没有可读图标（如“系统声音”）也占住同一块位置，保证各行文字左边缘对齐
                icon.SetResourceReference(Border.BackgroundProperty, "TabBgBrush");
                var glyph = new TextBlock
                {
                    Text = GlyphSpeaker,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
                icon.Child = glyph;
            }

            var nameText = new TextBlock
            {
                Text = app.Name,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var slider = new Slider { Minimum = 0, Maximum = 100, Style = (Style)Resources["ModernSlider"], Value = app.Volume * 100 };
            PercentEditor percent = null;
            percent = MakePercentEditor(app.Volume * 100, () => slider.Value, v =>
            {
                _suppressSliderEcho = true;
                slider.Value = v;
                _suppressSliderEcho = false;
                if (row.App != null) AudioSessionService.SetVolume(row.App, (float)(v / 100.0));
                percent.ShowText(Math.Round(v) + "%");
            });

            var muteBtn = new System.Windows.Controls.Button
            {
                Content = GlyphVolume,
                Style = (Style)Resources["MuteBtn"],
                Opacity = 0.55,
            };
            muteBtn.Click += (s, e) =>
            {
                bool mute = !Equals(muteBtn.Content, GlyphMute);
                if (row.App != null) AudioSessionService.SetMute(row.App, mute);
                SetMuteVisual(muteBtn, mute);
            };

            slider.AddHandler(Thumb.DragStartedEvent, (RoutedEventHandler)((s, e) => { row.Dragging = true; _dragCount++; }), true);
            slider.AddHandler(Thumb.DragCompletedEvent, (RoutedEventHandler)((s, e) =>
            {
                row.Dragging = false;
                if (_dragCount > 0) _dragCount--;
            }), true);
            slider.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEcho) return;
                percent.ShowText(Math.Round(e.NewValue) + "%");
                if (row.App != null) AudioSessionService.SetVolume(row.App, (float)(e.NewValue / 100.0));
            };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameText, 1);
            nameText.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(percent.Host, 2);
            Grid.SetColumn(muteBtn, 3);
            top.Children.Add(icon);
            top.Children.Add(nameText);
            top.Children.Add(percent.Host);
            top.Children.Add(muteBtn);

            var bottom = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            bottom.Children.Add(slider);

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(bottom);

            var appBg = new SolidColorBrush(Colors.Transparent);
            row.Card = new Border
            {
                Background = appBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Child = stack,
            };
            row.Card.MouseEnter += (s2, e2) =>
                appBg.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation((Color)((SolidColorBrush)Resources["HoverBrush"]).Color, TimeSpan.FromMilliseconds(110)));
            row.Card.MouseLeave += (s2, e2) =>
                appBg.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(110)));
            row.Slider = slider; row.Percent = percent; row.MuteBtn = muteBtn; row.App = app;
            return row;
        }

        ImageSource TryGetAppIcon(int pid, string exePath = null)
        {
            if (_iconCache.TryGetValue(pid, out var cached)) return cached;
            ImageSource src = null;
            try
            {
                if (pid > 0)
                {
                    using (var proc = Process.GetProcessById(pid))
                    {
                        string path = exePath;
                        if (string.IsNullOrEmpty(path))
                            try { path = proc.MainModule?.FileName; } catch { }
                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        {
                            using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                            {
                                if (ico != null)
                                {
                                    src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                                        BitmapSizeOptions.FromEmptyOptions());
                                    // 冻结后 WPF 渲染时不用为每帧复制一份位图
                                    if (src.CanFreeze) src.Freeze();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            _iconCache[pid] = src;
            return src;
        }

        // ================= 空间音效 =================

        void RefreshSpatial()
        {
            if (_spatialBusy) return;
            _spatialBusy = true;
            string playDef = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
            System.Threading.Tasks.Task.Run(() =>
            {
                string cur = playDef == null ? null : SpatialService.GetCurrent(playDef);
                var formats = SpatialService.GetAvailableFormats();
                Dispatcher.Invoke(() =>
                {
                    _spatialBusy = false;
                    if (cur != null) _spatialState = cur;
                    BuildSpatialChips(formats);
                });
            });
        }

        void BuildSpatialChips(List<SpatialFormat> formats)
        {
            SpatialChips.Children.Clear();
            foreach (var f in formats)
            {
                bool active = string.Equals(f.Guid, _spatialState, StringComparison.OrdinalIgnoreCase) ||
                              (f.Guid == "" && string.IsNullOrEmpty(_spatialState));

                var scale = new ScaleTransform(1, 1);
                // 每个芯片用自己的画刷副本：ResourceDictionary 里的画刷是已冻结的，不能做 ColorAnimation
                Color accentColor = ((SolidColorBrush)Resources["AccentBrush"]).Color;
                Color idleColor = ((SolidColorBrush)Resources["TabBgBrush"]).Color;
                Color hoverColor = ((SolidColorBrush)Resources["PressBrush"]).Color;
                var chipBg = new SolidColorBrush(active ? accentColor : idleColor);

                var chip = new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(13, 5, 13, 5),
                    Margin = new Thickness(0, 0, 7, 7),
                    Cursor = Cursors.Hand,
                    RenderTransform = scale,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Background = chipBg,
                };
                if (active)
                {
                    // 选中态只用极轻的投影提示层次，不做光晕
                    chip.Effect = new DropShadowEffect
                    {
                        BlurRadius = 10,
                        ShadowDepth = 1,
                        Direction = 270,
                        Opacity = 0.18,
                        Color = Color.FromRgb(0x00, 0x78, 0xD4),
                    };
                }

                var tb = new TextBlock { Text = f.Name, FontSize = 12 };
                tb.Foreground = active ? Brushes.White : (Brush)Resources["TextBrush"];
                if (active) tb.FontWeight = FontWeights.Medium;
                chip.Child = tb;

                if (!active)
                {
                    chip.MouseEnter += (s, e) =>
                        chipBg.BeginAnimation(SolidColorBrush.ColorProperty,
                            new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(110)));
                    chip.MouseLeave += (s, e) =>
                        chipBg.BeginAnimation(SolidColorBrush.ColorProperty,
                            new ColorAnimation(idleColor, TimeSpan.FromMilliseconds(110)));
                }

                // 按下反馈：缩到 0.96，松开回弹
                chip.MouseLeftButtonDown += (s, e) => { scale.ScaleX = scale.ScaleY = 0.96; };
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    var ease = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
                };
                chip.MouseLeave += (s, e) => { scale.ScaleX = scale.ScaleY = 1; };

                var guid = f.Guid;
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    string playDef = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                    if (playDef == null) return;
                    string devId = playDef;
                    string targetGuid = guid;
                    HintText.Text = "正在切换…";
                    System.Threading.Tasks.Task.Run(() => SpatialService.SetCurrent(devId, targetGuid));
                    _ = Dispatcher.InvokeAsync(() => { _spatialState = guid; BuildSpatialChips(formats); }, DispatcherPriority.Background);
                    // 稍后读回真实状态
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                    t.Tick += (s2, e2) => { t.Stop(); HintText.Text = ""; RefreshSpatial(); };
                    t.Start();
                };
                SpatialChips.Children.Add(chip);
            }
        }

        // ================= 事件 =================

        void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) try { DragMove(); } catch { }
        }

        void BtnSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

        void ShowSettings()
        {
            var dur = TimeSpan.FromMilliseconds(170);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, dur) { EasingFunction = ease });
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, dur) { EasingFunction = ease });
        }

        void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            var dur = TimeSpan.FromMilliseconds(110);
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            fade.Completed += (s2, e2) => SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.BeginAnimation(OpacityProperty, fade);
        }

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (ModeTray == null || _settings == null) return;
            _settings.Mode = ModeTray.IsChecked == true ? "tray" : "window";
            _settings.Save();
            ApplyWindowMode();
        }

        void AutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.Save();
            _settings.ApplyAutoStart();
        }

        void BtnClose_Click(object sender, RoutedEventArgs e) => HideToTray();
    }
}
