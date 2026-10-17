using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VolumeBuddy.Audio
{
    public class SpatialFormat
    {
        public string Guid;    // 设置用（SVV 需 GUID）；Off 时为空串
        public string Name;    // 显示名
    }

    public static class SpatialService
    {
        static string ExePath => Path.Combine(AppContext.BaseDirectory, "SoundVolumeView.exe");

        // 已知格式 GUID（Sonic/杜比已在本机实测验证）
        static readonly List<SpatialFormat> KnownFormats = new List<SpatialFormat>
        {
            new SpatialFormat { Guid = "",                                      Name = "关闭" },
            new SpatialFormat { Guid = "{B53D940C-B846-4831-9F76-D102B9B725A0}", Name = "Windows Sonic" },
            new SpatialFormat { Guid = "{1459AC38-3875-49BF-BB59-0FE80F4D395D}", Name = "杜比全景声" },
            new SpatialFormat { Guid = "DTS Headphone:X", Name = "DTS" }, // 按名称设置（仅装了 DTS 时显示）
        };

        static bool? _hasDolby;
        static bool HasDolby()
        {
            if (_hasDolby.HasValue) return _hasDolby.Value;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (key != null)
                        foreach (var name in key.GetSubKeyNames())
                            if (name.StartsWith("DolbyLaboratories.", StringComparison.OrdinalIgnoreCase))
                            { _hasDolby = true; return true; }
                }
            }
            catch { }
            _hasDolby = false;
            return false;
        }

        static bool? _hasDts;
        static bool HasDts()
        {
            if (_hasDts.HasValue) return _hasDts.Value;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (key != null)
                        foreach (var name in key.GetSubKeyNames())
                            if (name.StartsWith("DTS", StringComparison.OrdinalIgnoreCase))
                            { _hasDts = true; return true; }
                }
            }
            catch { }
            _hasDts = false;
            return false;
        }

        /// <summary>本机可用的空间音效格式列表（关闭 + 已安装的格式）。</summary>
        public static List<SpatialFormat> GetAvailableFormats()
        {
            var list = new List<SpatialFormat> { KnownFormats[0], KnownFormats[1] }; // 关闭、Sonic 必有
            if (HasDolby()) list.Add(KnownFormats[2]);
            if (HasDts()) list.Add(KnownFormats[3]);
            return list;
        }

        public static string NameOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "关闭";
            var f = KnownFormats.FirstOrDefault(x => string.Equals(x.Guid, guid, StringComparison.OrdinalIgnoreCase));
            return f != null && f.Guid != "" ? f.Name : (guid == KnownFormats[3].Guid ? "DTS" : "未知格式");
        }

        /// <summary>读取某输出设备当前的空间音效 GUID（空串=关闭，null=读取失败）。</summary>
        public static string GetCurrent(string deviceId)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "volumebuddy_spatial.txt");
                Run($"/stext \"{tmp}\"");
                string text = File.ReadAllText(tmp);
                foreach (var block in Regex.Split(text, "=+"))
                {
                    var idm = Regex.Match(block, @"Item ID\s*:\s*(\{[^\r\n]*)");
                    if (!idm.Success) continue;
                    if (!string.Equals(idm.Groups[1].Value.Trim(), deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                    var m = Regex.Match(block, @"Spatial Guid\s*:\s*(\{[0-9A-Fa-f-]+\}|)\s*\r?\n");
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
                return "";
            }
            catch { return null; }
        }

        /// <summary>设置某输出设备的空间音效。formatGuid 为空串表示关闭。</summary>
        public static bool SetCurrent(string deviceId, string formatGuid)
        {
            try
            {
                Run($"/SetSpatial \"{deviceId}\" \"{formatGuid ?? ""}\"");
                return true;
            }
            catch { return false; }
        }

        static void Run(string args)
        {
            if (!File.Exists(ExePath)) throw new FileNotFoundException("SoundVolumeView.exe 缺失", ExePath);
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)) { p.WaitForExit(8000); }
        }
    }
}
