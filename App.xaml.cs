using System.Linq;
using System.Threading;
using VolumeBuddy.Services;

namespace VolumeBuddy
{
    public partial class App : System.Windows.Application
    {
        public static EventWaitHandle ShowEvent;
        static Mutex _mutex;
        static MainWindow _window;

        /// <summary>惰性创建主窗口：启动时只驻留托盘，真正需要面板时才构建界面。</summary>
        public static MainWindow EnsureWindow()
        {
            if (_window != null) return _window;
            _window = new MainWindow();
            return _window;
        }

        public static void CrashLog(string source, Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "volumebuddy_crash.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [" + source + "] " + ex + Environment.NewLine +
                    new string('-', 60) + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            _mutex = new Mutex(true, "VolumeBuddy_9F3A2C_SingleInstance", out bool isNew);
            ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "VolumeBuddy_9F3A2C_Show");

            if (!isNew)
            {
                ShowEvent.Set();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 全局异常兜底：写日志，避免静默退出
            AppDomain.CurrentDomain.UnhandledException += (s2, e2) =>
                CrashLog("AppDomain", e2.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s2, e2) =>
            {
                CrashLog("Task", e2.Exception);
                e2.SetObserved();
            };
            DispatcherUnhandledException += (s2, e2) =>
            {
                CrashLog("Dispatcher", e2.Exception);
                e2.Handled = true;
            };

            // 托盘先行（不创建窗口）
            TrayHost.Init();
            try
            {
                var since = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "volumebuddy_ui.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " STARTUP tray ready in " + (int)since.TotalMilliseconds + " ms\r\n");
            }
            catch { }

            // 调试用：--shot=<png路径> 渲染一张界面截图后退出
            var shotArg = e.Args.FirstOrDefault(a => a.StartsWith("--shot="));
            if (shotArg != null)
            {
                EnsureWindow().CaptureTo(shotArg.Substring("--shot=".Length));
                return;
            }

            // 单实例唤起（第二个实例启动时把面板叫出来）
            ThreadPool.RegisterWaitForSingleObject(ShowEvent,
                (state, timedOut) => Dispatcher.Invoke(() => TrayHost.ShowPanel(false)),
                null, -1, false);

            var settings = Services.AppSettings.Load();
            if (settings.Mode == "window") EnsureWindow().ShowFromTray(false);
            else TrayHost.WarmupLater();     // 托盘模式：空闲时预热窗口
        }
    }
}
