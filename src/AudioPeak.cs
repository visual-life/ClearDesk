using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearDesk
{
    // Reads the current output peak of one process's audio sessions (WASAPI IAudioMeterInformation).
    // Used as a playback signal when a player's UI stops updating (e.g. NetEase CEF while minimized).
    internal static class AudioPeak
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
            int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            int GetCount(out int count);
            int GetSession(int index, out IAudioSessionControl2 session);
        }
        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            int GetState(out int state); int GetDisplayName(out IntPtr name); int SetDisplayName(IntPtr name, IntPtr ctx);
            int GetIconPath(out IntPtr path); int SetIconPath(IntPtr path, IntPtr ctx); int GetGroupingParam(out Guid g); int SetGroupingParam(ref Guid g, IntPtr ctx);
            int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
            int GetSessionIdentifier(out IntPtr id); int GetSessionInstanceIdentifier(out IntPtr id);
            int GetProcessId(out int pid);
        }
        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation { int GetPeakValue(out float peak); }

        // Highest peak (0..1) over all sessions owned by processes with this name; -1 when no session exists.
        public static float Peak(string processName)
        {
            float best = -1;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device; if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return -1;
                Guid iid = typeof(IAudioSessionManager2).GUID; object o;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return -1;
                IAudioSessionEnumerator sessions; ((IAudioSessionManager2)o).GetSessionEnumerator(out sessions);
                int count; sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 session; if (sessions.GetSession(i, out session) != 0 || session == null) continue;
                    int pid; session.GetProcessId(out pid); if (pid == 0) continue;
                    string name; try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; } catch { continue; }
                    if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) continue;
                    var meter = session as IAudioMeterInformation; float peak;
                    if (meter != null && meter.GetPeakValue(out peak) == 0) best = Math.Max(best, peak);
                    else best = Math.Max(best, 0);
                }
            }
            catch { }
            return best;
        }
    }
}
