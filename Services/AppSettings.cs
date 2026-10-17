using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace VolumeBuddy.Services
{
    public class AppSettings
    {
        public string Mode { get; set; } = "tray";       // tray | window
        public bool AutoStart { get; set; } = true;

        static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeBuddy");
        static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void ApplyAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (AutoStart)
                    {
                        string exe = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exe))
                            key.SetValue("VolumeBuddy", "\"" + exe + "\" --tray");
                    }
                    else key.DeleteValue("VolumeBuddy", false);
                }
            }
            catch { }
        }
    }

    public static class ThemeHelper
    {
        /// <summary>系统是否使用深色模式。</summary>
        public static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
                }
            }
            catch { }
            return false;
        }
    }
}
