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
        readonly DispatcherTimer _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        Microsoft.Win32.UserPreferenceChangedEventHandler _prefHandler;

        // 行缓存，避免每 500ms 重建导致闪烁和拖动冲突
        readonly Dictionary<string, DeviceRow> _playRows = new Dictionary<string, DeviceRow>();
        readonly Dictionary<string, DeviceRow> _recRows = new Dictionary<string, DeviceRow>();
        readonly Dictionary<int, AppRow> _appRows = new Dictionary<int, AppRow>();

        List<AppSessionInfo> _currentSessions = new List<AppSessionInfo>();
        List<DeviceInfo> _playDevices = new List<DeviceInfo>();
        List<DeviceInfo> _recDevices = new List<DeviceInfo>();
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
            _trimTimer.Tick += (s, e) => { _trimTimer.Stop(); App.TrimWorkingSet(); };
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

        bool _closing;

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
            if (_closing) return;                 // 正在收起（多由失焦触发），忽略这次点击
            if (IsVisible) HideToTray();
            else ShowPanel();
        }

        /// <summary>从下方淡入并轻微放大（托盘右上弹出感）。</summary>
        void AnimateIn()
        {
            StopAnimations();
            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
            // 动画打在内容层上，不要动 Window.Opacity：对分层窗口逐帧改整体 alpha 会让
            // WPF 反复重建渲染表面，反复开关面板时内存只涨不跌
            var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(10, 0, dur) { EasingFunction = ease };
            var popX = new DoubleAnimation(0.965, 1, dur) { EasingFunction = ease };
            var popY = new DoubleAnimation(0.965, 1, dur) { EasingFunction = ease };
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

        /// <summary>向下淡出后隐藏（退出比入场快，不让人等）。</summary>
        void AnimateOut(Action onDone)
        {
            _closing = true;
            var dur = TimeSpan.FromMilliseconds(120);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(0, 6, dur) { EasingFunction = ease };
            var popX = new DoubleAnimation(1, 0.985, dur) { EasingFunction = ease };
            var popY = new DoubleAnimation(1, 0.985, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, 180);
            Timeline.SetDesiredFrameRate(slide, 180);
            Timeline.SetDesiredFrameRate(popX, 180);
            Timeline.SetDesiredFrameRate(popY, 180);
            fade.Completed += (s2, e2) => { _closing = false; onDone(); };
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
                UiLog("ShowPanel enter closing=" + _closing + " visible=" + IsVisible);
                ApplyWindowMode();

                var wa = SystemParameters.WorkArea;
                if (_settings.Mode == "tray")
                {
                    // 贴任务栏上沿（不遮挡托盘图标），水平方向跟随鼠标并贴边
                    var pos = System.Windows.Forms.Cursor.Position;
                    var src = PresentationSource.FromVisual(this);
                    double dpiX = src?.CompositionTarget.TransformFromDevice.M11 ?? 1.0;
                    double cx = pos.X * dpiX;
                    double left = cx + 56 - Width;
                    left = Math.Min(Math.Max(left, wa.Left + 8), wa.Right - Width - 8);
                    Left = left;
                    // 任务栏自动隐藏时，工作区底部就是屏幕底部，需额外抬高，避免遮住弹出的托盘图标
                    double lift = IsTaskbarAutoHide() ? 60 : 8;
                    Top = wa.Bottom - Height - lift;
                }
                else
                {
                    Left = wa.Left + (wa.Width - Width) / 2;
                    Top = wa.Top + (wa.Height - Height) / 2;
                }

                if (!IsVisible) Show();
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
                RefreshAll();
                RefreshSpatial();
                UiLog("ShowPanel done visible=" + IsVisible + " opacity=" + Opacity +
                      " rect=" + (int)Left + "," + (int)Top + "," + (int)Width + "x" + (int)Height);
            }
            catch (Exception ex)
            {
                UiLog("ShowPanel EXCEPTION " + ex);
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnClosed(EventArgs e)
        {
            _uiTimer.Stop();
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

        void HideToTray()
        {
            UiLog("HideToTray visible=" + IsVisible + " closing=" + _closing);
            if (!IsVisible || _closing) return;
            AnimateOut(() =>
            {
                Hide();
                _uiTimer.Stop();
                // 收起时放掉会话与设备句柄（COM 原生内存），并修剪工作集
                try
                {
                    foreach (var r in _appRows.Values) r.App = null;   // 句柄即将释放，别留着被误用
                    AudioSessionService.Release(_currentSessions);
                    _currentSessions.Clear();
                    _playDevices.Clear();
                    _recDevices.Clear();
                }
                catch { }
                // 延迟 400ms 再修剪：收起动画刚结束时还有一批释放/终结动作在跑，
                // 立刻 trim 会把"正在变脏"的页留在工作集里
                _trimTimer.Stop();
                _trimTimer.Start();
            });
        }

        bool _shotMode;
        bool _envLogged;

        DateTime _shownAt = DateTime.MinValue;

        void Window_Deactivated(object sender, EventArgs e)
        {
            if (_shotMode || _closing) return;
            if (_settings.Mode != "tray") return;
            // 刚弹出时常常还没真正拿到前台权限，此时失焦事件不算数
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 450) { UiLog("deactivated ignored (grace)"); return; }
            UiLog("deactivated -> hide");
            HideToTray();
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
                _playDevices = AudioService.GetDevices(EDataFlow.eRender, playDef);
                _recDevices = AudioService.GetDevices(EDataFlow.eCapture, recDef);
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
                oldTops[kv.Key] = kv.Value.Card.TransformToAncestor(panel).Transform(new Point(0, 0)).Y;
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
                double newY = kv.Value.Card.TransformToAncestor(panel).Transform(new Point(0, 0)).Y;
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
                try { AudioService.SetDefaultDevice(d.Id); } catch { }
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
