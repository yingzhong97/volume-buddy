using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using VolumeBuddy.Interop;

namespace VolumeBuddy.Audio
{
    public class DeviceInfo
    {
        public string Id;
        public string Name;
        public string Bus;
        public bool IsDefault;
        public float Volume;   // 0..1
        public bool Muted;
        public EDataFlow Flow;
    }

    public static class AudioService
    {
        static readonly IMMDeviceEnumerator _enumObj =
            (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());

        // ---------- devices ----------

        public static IMMDevice GetDefaultRenderDevice()
        {
            try
            {
                if (_enumObj.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) == 0)
                    return dev;
            }
            catch { }
            return null;
        }

        public static string GetDefaultDeviceId(EDataFlow flow)
        {
            try
            {
                if (_enumObj.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var dev) != 0 || dev == null)
                    return null;
                dev.GetId(out string id);
                return id;
            }
            catch { return null; }
        }

        static readonly Dictionary<string, (string name, string bus)> _metaCache =
            new Dictionary<string, (string, string)>();
        static readonly Dictionary<string, IAudioEndpointVolume> _epCache =
            new Dictionary<string, IAudioEndpointVolume>();

        /// <summary>取（并缓存）某设备的端点音量接口，避免每次刷新都重新激活。</summary>
        public static IAudioEndpointVolume GetEndpoint(string deviceId)
        {
            if (deviceId == null) return null;
            if (_epCache.TryGetValue(deviceId, out var cached)) return cached;
            var dev = GetDeviceById(deviceId);
            if (dev == null) return null;
            var ep = ActivateAct<IAudioEndpointVolume>(dev, EndpointVolumeIid, out _);
            if (ep != null) _epCache[deviceId] = ep;
            return ep;
        }

        static void DropEndpoint(string deviceId) { _epCache.Remove(deviceId); }

        public static List<DeviceInfo> GetDevices(EDataFlow flow, string defaultId)
        {
            var list = new List<DeviceInfo>();
            _enumObj.EnumAudioEndpoints(flow, (int)DeviceState.Active, out var col);
            if (col == null) return list;
            col.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0 || dev == null) continue;
                var di = new DeviceInfo { Flow = flow, IsDefault = false };
                dev.GetId(out di.Id);
                if (defaultId != null && string.Equals(di.Id, defaultId, StringComparison.OrdinalIgnoreCase))
                    di.IsDefault = true;
                if (_metaCache.TryGetValue(di.Id, out var meta))
                {
                    di.Name = meta.name; di.Bus = meta.bus;
                }
                else
                {
                    di.Name = GetFriendlyName(dev);
                    di.Bus = GetBusName(dev);
                    _metaCache[di.Id] = (di.Name, di.Bus);
                }
                try
                {
                    var ep = GetEndpoint(di.Id);
                    if (ep != null)
                    {
                        ep.GetMasterVolumeLevelScalar(out di.Volume);
                        ep.GetMute(out di.Muted);
                    }
                }
                catch { DropEndpoint(di.Id); }
                list.Add(di);
            }
            // 重名设备追加连接方式后缀，如「数字输出（板载）/ 数字输出（USB）」
            foreach (var g2 in list.GroupBy(d => d.Name).Where(g => g.Count() > 1))
                foreach (var d in g2)
                    d.Name = $"{d.Name}（{d.Bus}）";
            // 默认设备排最前
            list.Sort((a, b) => b.IsDefault.CompareTo(a.IsDefault));
            return list;
        }

        /// <summary>读取设备连接方式（PKEY {a45c254e-df1c-4efd-8020-67d146a850e0},24），转成中文短标签。</summary>
        public static string GetBusName(IMMDevice dev)
        {
            string raw = null;
            try
            {
                dev.OpenPropertyStore(0, out var store);
                var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
                store.GetValue(ref key, out var pv);
                if (pv.vt == 31) raw = Marshal.PtrToStringUni(pv.pointerValue);
            }
            catch { }
            if (string.IsNullOrEmpty(raw)) return "其他";
            raw = raw.ToUpperInvariant();
            if (raw.Contains("USB")) return "USB";
            if (raw.Contains("HDAUDIO") || raw.Contains("HDAUDIO")) return "板载";
            if (raw.Contains("BTHENUM") || raw.Contains("BTHLE")) return "蓝牙";
            if (raw.Contains("PCI")) return "板载";
            return raw.Length > 8 ? "其他" : raw;
        }

        static readonly Guid EndpointVolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        const int ClsCtxAll = 23; // CLSCTX_ALL

        /// <summary>激活设备上的指定接口，返回 (接口, HRESULT)。</summary>
        internal static T ActivateAct<T>(IMMDevice dev, Guid iid, out int hr) where T : class
        {
            Guid g = iid;
            IntPtr ptr = IntPtr.Zero;
            hr = dev.Activate(ref g, ClsCtxAll, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return (T)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }

        public static string GetFriendlyName(IMMDevice dev)
        {
            try
            {
                dev.OpenPropertyStore(0 /*STGM_READ*/, out var store);
                var key = AudioProps.FriendlyName;
                store.GetValue(ref key, out var pv);
                if (pv.vt == 31 /*VT_LPWSTR*/)
                    return Marshal.PtrToStringUni(pv.pointerValue);
            }
            catch { }
            return "(未知设备)";
        }

        public static IMMDevice GetDeviceById(string id)
        {
            if (_enumObj.GetDevice(id, out var dev) == 0) return dev;
            return null;
        }

        // ---------- per-device master volume ----------

        public static (float vol, bool mute) GetEndpointVolume(string deviceId)
        {
            var ep = GetEndpoint(deviceId);
            if (ep == null) return (0f, false);
            try
            {
                ep.GetMasterVolumeLevelScalar(out float v);
                ep.GetMute(out bool m);
                return (v, m);
            }
            catch { DropEndpoint(deviceId); return (0f, false); }
        }

        public static void SetEndpointVolume(string deviceId, float level)
        {
            Guid g = Guid.Empty;
            var ep = GetEndpoint(deviceId);
            if (ep != null)
            {
                try { ep.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g); return; }
                catch { DropEndpoint(deviceId); }
            }
            ep = GetEndpoint(deviceId);
            try { ep?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g); } catch { }
        }

        public static void SetEndpointMute(string deviceId, bool mute)
        {
            Guid g = Guid.Empty;
            var ep = GetEndpoint(deviceId);
            if (ep != null)
            {
                try { ep.SetMute(mute, ref g); return; }
                catch { DropEndpoint(deviceId); }
            }
            ep = GetEndpoint(deviceId);
            try { ep?.SetMute(mute, ref g); } catch { }
        }

        // ---------- default device switching ----------

        public static void SetDefaultDevice(string deviceId)
        {
            var cfg = (IPolicyConfig)(new PolicyConfigClient());
            cfg.SetDefaultEndpoint(deviceId, ERole.eConsole);
            cfg.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
        }

    }
}
