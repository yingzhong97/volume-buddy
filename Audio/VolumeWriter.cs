using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VolumeBuddy.Interop;

namespace VolumeBuddy.Audio
{
    /// <summary>
    /// 音量写入器：SetMasterVolumeLevelScalar 单次约 3ms，若在 UI 线程随鼠标拖动调用会明显卡顿。
    /// 这里改为后台 MTA 线程异步应用，并按设备合并“最新值”，UI 线程零阻塞。
    /// </summary>
    public static class VolumeWriter
    {
        class Pending
        {
            public float Volume = float.NaN;
            public bool MuteRequested;
            public bool MuteValue;
            public long Serial;
            public long AppliedSerial;
        }

        static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        static readonly object _lock = new object();
        static Thread _thread;
        static long _serial;

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        static void EnsureStarted()
        {
            if (_thread != null) return;
            lock (_lock)
            {
                if (_thread != null) return;
                _thread = new Thread(Worker) { IsBackground = true, Name = "VolumeWriter" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
        }

        public static void SetVolume(string deviceId, float volume)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            EnsureStarted();
            lock (_lock)
            {
                if (!_pending.TryGetValue(deviceId, out var p)) _pending[deviceId] = p = new Pending();
                p.Volume = Math.Clamp(volume, 0f, 1f);
                p.Serial = ++_serial;
            }
            _signal.Set();
        }

        public static void SetMute(string deviceId, bool mute)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            EnsureStarted();
            lock (_lock)
            {
                if (!_pending.TryGetValue(deviceId, out var p)) _pending[deviceId] = p = new Pending();
                p.MuteRequested = true;
                p.MuteValue = mute;
                p.Serial = ++_serial;
            }
            _signal.Set();
        }

        /// <summary>该设备是否还有未落盘的写入（用于避免刷新时读到旧值回弹）。</summary>
        public static bool HasPending(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            lock (_lock)
            {
                return _pending.TryGetValue(deviceId, out var p) && p.Serial != p.AppliedSerial;
            }
        }

        static void Worker()
        {
            try { CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/); } catch { }

            IMMDeviceEnumerator en = null;
            var endpoints = new Dictionary<string, IAudioEndpointVolume>();
            var epIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

            while (true)
            {
                _signal.WaitOne(80);

                List<KeyValuePair<string, (float vol, bool muteSet, bool mute, long serial)>> jobs;
                lock (_lock)
                {
                    jobs = new List<KeyValuePair<string, (float, bool, bool, long)>>();
                    foreach (var kv in _pending)
                    {
                        if (kv.Value.Serial == kv.Value.AppliedSerial) continue;
                        jobs.Add(new KeyValuePair<string, (float, bool, bool, long)>(kv.Key,
                            (kv.Value.Volume, kv.Value.MuteRequested, kv.Value.MuteValue, kv.Value.Serial)));
                        kv.Value.AppliedSerial = kv.Value.Serial;
                    }
                }

                foreach (var job in jobs)
                {
                    try
                    {
                        if (!endpoints.TryGetValue(job.Key, out var ep))
                        {
                            if (en == null) en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                            if (en.GetDevice(job.Key, out var dev) != 0 || dev == null) continue;
                            Guid g = epIid;
                            if (dev.Activate(ref g, 23, IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) continue;
                            ep = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr);
                            Marshal.Release(ptr);
                            endpoints[job.Key] = ep;
                        }

                        Guid ctx = Guid.Empty;
                        var (vol, muteSet, mute, _) = job.Value;
                        if (!float.IsNaN(vol)) ep.SetMasterVolumeLevelScalar(vol, ref ctx);
                        if (muteSet) ep.SetMute(mute, ref ctx);
                    }
                    catch
                    {
                        try { endpoints.Remove(job.Key); } catch { }
                    }
                }
            }
        }
    }
}
