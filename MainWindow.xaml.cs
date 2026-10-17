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
            SourceInitialized += (s, e) => EnableAcrylic();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                    Dispatcher.Invoke(() => ApplyTheme(ThemeHelper.IsDark()));
            };

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
            Color WindowBg() => dark ? Color.FromArgb(0xE0, 0x20, 0x22, 0x26) : Color.FromArgb(0xE6, 0xF7, 0xF8, 0xFA);
            var res = Resources;
            res["BgBrush"] = new SolidColorBrush(WindowBg());
            res["CardBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x30, 0x35) : Colors.White);
            res["TabBgBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x26, 0x28, 0x2C) : Color.FromRgb(0xEC, 0xEE, 0xF1));
            res["TextBrush"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1B, 0x1B, 0x1B));
            res["SubBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xA6, 0xAC, 0xB5) : Color.FromRgb(0x5F, 0x66, 0x70));
            res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            res["HoverBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            res["SliderTrackBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x44, 0x48, 0x4E) : Color.FromRgb(0xD8, 0xDC, 0xE0));
            res["WindowBorderBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x44, 0x00, 0x00, 0x00));
            res["CardBorderBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            res["DividerBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            res["RowActiveBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2C, 0x31, 0x38) : Color.FromRgb(0xF0, 0xF4, 0xF9));
        }

        // ================= 亚克力背景 =================

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State; public int Flags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        void EnableAcrylic()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                bool dark = ThemeHelper.IsDark();
                uint tint = dark ? 0xB4222226u : 0xC8F3F6FAu; // AABBGGRR
                var accent = new AccentPolicy { State = 4 /*ACRYLICBLURBEHIND*/, Flags = 2, GradientColor = tint };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WinCompAttrData { Attribute = 19, Data = ptr, SizeOfData = Marshal.SizeOf(accent) };
                SetWindowCompositionAttribute(hwnd, ref data);
                Marshal.FreeHGlobal(ptr);
            }
            catch { }
        }

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
            var dur = TimeSpan.FromMilliseconds(190);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, dur) { EasingFunction = ease });
        }

        /// <summary>向下淡出后隐藏。</summary>
        void AnimateOut(Action onDone)
        {
            _closing = true;
            var dur = TimeSpan.FromMilliseconds(140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fade.Completed += (s2, e2) => { _closing = false; onDone(); };
            BeginAnimation(OpacityProperty, fade);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 10, dur) { EasingFunction = ease });
        }

        void StopAnimations()
        {
            BeginAnimation(OpacityProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PanelOffset.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
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
                _shownAt = DateTime.UtcNow;
                AnimateIn();            // 关键：每次显示都重置透明度并播放入场动画
                ForceForeground();
                _uiTimer.Start();
                RefreshAll();
                RefreshSpatial();
                UiLog("ShowPanel done visible=" + IsVisible + " opacity=" + Opacity);
            }
            catch (Exception ex)
            {
                UiLog("ShowPanel EXCEPTION " + ex);
            }
        }

        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

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
                // 托盘待命时修剪工作集，降低内存占用
                try
                {
                    using (var p = Process.GetCurrentProcess())
                        EmptyWorkingSet(p.Handle);
                }
                catch { }
            });
        }

        bool _shotMode;

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
        public void CaptureTo(string path)
        {
            _shotMode = true;
            Show();
            UpdateLayout();
            RefreshAll();
            RefreshSpatial();
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
            public DropShadowEffect Shadow;
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
                    row.MuteBtn.Content = d.Muted ? "🔇" : "🔊";
                    row.MuteBtn.Opacity = d.Muted ? 1.0 : 0.55;
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

        /// <summary>设置设备行的高亮（默认设备带卡片背景和柔和阴影）。</summary>
        void SetRowActive(DeviceRow row, bool active, bool animate)
        {
            var dur = TimeSpan.FromMilliseconds(200);
            Color bgColor = active ? ((SolidColorBrush)Resources["RowActiveBrush"]).Color : Colors.Transparent;
            double opacity = active ? 1.0 : 0.85;

            if (!animate)
            {
                row.BgBrush.Color = bgColor;
                row.Card.Opacity = opacity;
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            row.BgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(bgColor, dur) { EasingFunction = ease });
            row.Card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(opacity, dur) { EasingFunction = ease });
        }

        DeviceRow BuildDeviceRow(StackPanel panel, DeviceInfo d, bool isPlay)
        {
            var row = new DeviceRow();
            var nameText = new TextBlock
            {
                Text = d.Name,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                MaxHeight = 36,      // 最多两行
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var badge = new Border
            {
                Background = (Brush)Resources["AccentBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock { Text = "默认", FontSize = 10, Foreground = Brushes.White },
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var nameArea = new Grid();
            nameArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            badge.Margin = new Thickness(6, 0, 0, 0);
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
                Content = "🔊",
                Style = (Style)Resources["IconBtn"],
            };
            muteBtn.Click += (s, e) =>
            {
                bool mute = (string)muteBtn.Content == "🔊";
                VolumeWriter.SetMute(d.Id, mute);
                muteBtn.Content = mute ? "🔇" : "🔊";
                muteBtn.Opacity = mute ? 1.0 : 0.55;
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
            row.Shadow = new DropShadowEffect { Opacity = 0 };
            row.Offset = new TranslateTransform();
            row.Card = new Border
            {
                Background = row.BgBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
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
                row.Card.Opacity = app.Active ? 1.0 : 0.62;
                row.MuteBtn.Content = app.Muted ? "🔇" : "🔊";
                row.MuteBtn.Opacity = app.Muted ? 1.0 : 0.55;
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

            var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            var iconSrc = TryGetAppIcon(app.Pid, app.ExePath);
            if (iconSrc != null) icon.Source = iconSrc;

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
                Content = "🔊",
                Style = (Style)Resources["IconBtn"],
            };
            muteBtn.Click += (s, e) =>
            {
                bool mute = (string)muteBtn.Content == "🔊";
                if (row.App != null) AudioSessionService.SetMute(row.App, mute);
                muteBtn.Content = mute ? "🔇" : "🔊";
                muteBtn.Opacity = mute ? 1.0 : 0.55;
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
            nameText.Margin = new Thickness(6, 0, 0, 0);
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
                Padding = new Thickness(8, 7, 8, 7),
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
                                    src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                                        BitmapSizeOptions.FromEmptyOptions());
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
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(15),
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand,
                    Background = active ? (Brush)Resources["AccentBrush"] : (Brush)Resources["CardBrush"],
                };
                chip.BorderThickness = new Thickness(0);
                if (active)
                    chip.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.28, Color = Color.FromRgb(0x00, 0x78, 0xD4) };
                else
                {
                    chip.SetResourceReference(Border.BorderBrushProperty, "SliderTrackBrush");
                    chip.BorderThickness = new Thickness(1);
                }
                var tb = new TextBlock { Text = f.Name, FontSize = 12 };
                tb.Foreground = active ? Brushes.White : (Brush)Resources["TextBrush"];
                chip.Child = tb;
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
        void ShowSettings() { SettingsOverlay.Visibility = Visibility.Visible; }
        void BtnBack_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

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
