using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VolumeBuddy.Interop;

namespace VolumeBuddy.Audio
{
    public class AppSessionInfo
    {
        public int Pid;
        public string Name;
        public string ExePath;
        public float Volume;
        public bool Muted;
        public bool SystemSounds;
        public bool Active;
        public List<ISimpleAudioVolume> Handles = new List<ISimpleAudioVolume>();
    }

    /// <summary>
    /// 原生音量合成器：IAudioSessionManager2 枚举会话 → QI ISimpleAudioVolume 直接读写（约 0.14ms/次）。
    /// </summary>
    public static class AudioSessionService
    {
        static readonly Guid SessionMgrIid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static readonly Guid SimpleVolumeIid = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        public static List<AppSessionInfo> GetSessions()
        {
            var result = new List<AppSessionInfo>();
            try
            {
                var dev = AudioService.GetDefaultRenderDevice();
                if (dev == null) return result;

                var mgr = AudioService.ActivateAct<IAudioSessionManager2>(dev, SessionMgrIid, out int hr);
                if (mgr == null) return result;

                if (mgr.GetSessionEnumerator(out var enumerator) != 0 || enumerator == null) return result;
                if (enumerator.GetCount(out int count) != 0) return result;

                var byKey = new Dictionary<int, AppSessionInfo>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr punk = IntPtr.Zero;
                    IAudioSessionControl2 ctl = null;
                    try
                    {
                        if (enumerator.GetSession(i, out punk) != 0 || punk == IntPtr.Zero) continue;
                        ctl = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(punk);
                        Marshal.Release(punk);
                        punk = IntPtr.Zero;

                        // 0=Inactive（暂停中，保留）、1=Active、2=Expired（已结束，丢弃）
                        if (ctl.GetState(out int state) != 0 || state == 2) continue;

                        Guid volIid = SimpleVolumeIid;
                        IntPtr ctlUnk = Marshal.GetIUnknownForObject(ctl);
                        IntPtr volPtr = IntPtr.Zero;
                        try
                        {
                            if (Marshal.QueryInterface(ctlUnk, ref volIid, out volPtr) != 0 || volPtr == IntPtr.Zero)
                                continue;
                            var vol = (ISimpleAudioVolume)Marshal.GetObjectForIUnknown(volPtr);
                            Marshal.Release(volPtr);

                            // 系统声音会话的进程号为 0；不能依赖 IsSystemSoundsSession（不同系统返回值不稳定）
                            uint pidValue = 0;
                            ctl.GetProcessId(out pidValue);
                            bool sys = pidValue == 0;

                            int key = sys ? 0 : (int)pidValue;
                            if (!byKey.TryGetValue(key, out var info))
                            {
                                info = new AppSessionInfo { Pid = (int)pidValue, SystemSounds = sys, Active = state == 1 };
                                if (!sys && string.IsNullOrWhiteSpace(info.Name)) info.Name = "应用 " + pidValue;
                                if (sys) info.Name = "系统声音";
                                else
                                {
                                    try
                                    {
                                        var proc = Process.GetProcessById((int)pidValue);
                                        info.Name = proc.ProcessName;
                                        try { info.ExePath = proc.MainModule?.FileName; } catch { }
                                    }
                                    catch { info.Name = "未知程序"; }
                                }
                                byKey[key] = info;
                                result.Add(info);
                            }

                            if (info.Handles.Count == 0)
                            {
                                vol.GetMasterVolume(out float v);
                                vol.GetMute(out bool m);
                                info.Volume = v;
                                info.Muted = m;
                            }
                            info.Handles.Add(vol);
                        }
                        finally
                        {
                            if (ctlUnk != IntPtr.Zero) Marshal.Release(ctlUnk);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (ctl != null && Marshal.IsComObject(ctl)) Marshal.ReleaseComObject(ctl);
                        if (punk != IntPtr.Zero) Marshal.Release(punk);
                    }
                }
                // 正在发声的应用排前面
                result.Sort((a, b) => b.Active.CompareTo(a.Active));
            }
            catch { }
            return result;
        }

        public static void SetVolume(AppSessionInfo app, float level)
        {
            Guid ctx = Guid.Empty;
            float v = Math.Clamp(level, 0f, 1f);
            foreach (var h in app.Handles)
            {
                try { h.SetMasterVolume(v, ref ctx); } catch { }
            }
        }

        public static void SetMute(AppSessionInfo app, bool mute)
        {
            Guid ctx = Guid.Empty;
            foreach (var h in app.Handles)
            {
                try { h.SetMute(mute, ref ctx); } catch { }
            }
        }

        public static void Release(List<AppSessionInfo> sessions)
        {
            if (sessions == null) return;
            foreach (var s in sessions)
            {
                foreach (var h in s.Handles)
                {
                    try { if (Marshal.IsComObject(h)) Marshal.ReleaseComObject(h); } catch { }
                }
                s.Handles.Clear();
            }
        }
    }
}
