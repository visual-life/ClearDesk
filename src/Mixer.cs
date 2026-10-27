using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClearDesk
{
    // ---------------------------------------------------------------- Core Audio sessions (2.2.1: the 音量合成器 page of 快捷控制)
    // The interfaces below are private nested types on purpose: AudioPeak declares its own partial versions of some of them, and
    // QuickControls declares a [ComImport] class for the device enumerator. Sharing declarations (or creating the enumerator with
    // "new") once made the CLR type an RCW as the wrong declaration (InvalidCast inside the running app, 2.0.4–2.0.7).
    // Everything is created through CLSIDs / QueryInterface of the object we were handed.
    internal static class SessionAudio
    {
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator sessions);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out ISessionControl2 session);
        }
        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out int pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }
        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMeter { [PreserveSig] int GetPeakValue(out float peak); }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string source, StringBuilder output, int length, IntPtr reserved);

        static readonly Guid Context = Guid.NewGuid();

        /// <summary>Resolves "@%SystemRoot%\system32\AudioSrv.Dll,-202" style names; returns null when it is not a resource reference or can not be resolved.</summary>
        internal static string ResolveResource(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '@') return text;
            try { var sb = new StringBuilder(260); return SHLoadIndirectString(text, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null; } catch { return null; }
        }

        /// <summary>All sessions of the default output device that are not expired. Throws nothing: an empty list and <paramref name="error"/> instead.</summary>
        internal static List<RawSession> ReadSessions(out string error)
        {
            error = null; var list = new List<RawSession>();
            object manager = null; CoreAudio.IMMDeviceEnumerator enumerator = null; CoreAudio.IMMDevice device = null;
            try
            {
                enumerator = CoreAudio.Enumerator();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) { error = "没有可用的输出设备"; return list; }
                Guid iid = typeof(ISessionManager2).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out manager) != 0 || manager == null) { error = "无法读取应用音量"; return list; }
                ISessionEnumerator sessions; if (((ISessionManager2)manager).GetSessionEnumerator(out sessions) != 0 || sessions == null) { error = "无法读取应用音量"; return list; }
                int count; sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    ISessionControl2 control; if (sessions.GetSession(i, out control) != 0 || control == null) continue;
                    try
                    {
                        var raw = new RawSession { Control = control };
                        int state; if (control.GetState(out state) == 0) raw.State = state;
                        if (raw.State == 2) { Marshal.ReleaseComObject(control); continue; }          // expired
                        int pid; if (control.GetProcessId(out pid) == 0) raw.Pid = pid;
                        raw.IsSystem = control.IsSystemSoundsSession() == 0;
                        string name; if (control.GetDisplayName(out name) == 0) raw.DisplayName = ResolveResource(name);
                        var volume = control as ISimpleVolume;
                        if (volume == null) { Marshal.ReleaseComObject(control); continue; }
                        float v; if (volume.GetMasterVolume(out v) == 0) raw.Volume = v;
                        bool muted; if (volume.GetMute(out muted) == 0) raw.Muted = muted;
                        var meter = control as IMeter; float peak; if (meter != null && meter.GetPeakValue(out peak) == 0) raw.Peak = peak;
                        if (raw.Pid > 0) { raw.ExePath = ProcessTree.ExeOf((uint)raw.Pid); if (raw.ExePath != null) { raw.FileDescription = FileDescriptionOf(raw.ExePath); raw.ProcessName = ProcessNameOf(raw.Pid, raw.ExePath); } }
                        list.Add(raw);
                    }
                    catch (Exception e) { Program.Log(e); }
                }
                Marshal.ReleaseComObject(sessions);
            }
            catch (Exception e) { error = "无法读取应用音量"; Program.Log(e); }
            finally
            {
                if (manager != null) try { Marshal.ReleaseComObject(manager); } catch { }
                if (device != null) try { Marshal.ReleaseComObject(device); } catch { }
                if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
            }
            return list;
        }

        static string ProcessNameOf(int pid, string exe)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; } catch { return Path.GetFileNameWithoutExtension(exe); }
        }

        static readonly Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string FileDescriptionOf(string exe)
        {
            string d; if (descriptions.TryGetValue(exe, out d)) return d;
            try { d = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { d = null; }
            if (descriptions.Count > 300) descriptions.Clear();
            descriptions[exe] = d; return d;
        }

        internal static bool SetVolume(MixerRow row, float level)
        {
            bool ok = row.Controls.Count > 0; level = Math.Max(0, Math.Min(1, level)); var ctx = Context;
            foreach (var c in row.Controls) { try { var v = c as ISimpleVolume; ok &= v != null && v.SetMasterVolume(level, ref ctx) == 0; if (ok && level > 0 && row.Muted && v != null) v.SetMute(false, ref ctx); } catch { ok = false; } }
            if (ok) { row.Volume = level; if (level > 0) row.Muted = false; }
            return ok;
        }
        internal static bool SetMuted(MixerRow row, bool muted)
        {
            bool ok = row.Controls.Count > 0; var ctx = Context;
            foreach (var c in row.Controls) { try { var v = c as ISimpleVolume; ok &= v != null && v.SetMute(muted, ref ctx) == 0; } catch { ok = false; } }
            if (ok) row.Muted = muted;
            return ok;
        }
        internal static void Release(IEnumerable<MixerRow> rows)
        {
            foreach (var r in rows ?? Enumerable.Empty<MixerRow>()) { foreach (var c in r.Controls) try { Marshal.ReleaseComObject(c); } catch { } r.Controls.Clear(); }
        }
    }

    internal sealed class RawSession
    {
        public int Pid, State; public string ExePath, DisplayName, FileDescription, ProcessName; public bool IsSystem, Muted; public float Volume, Peak; public object Control;
    }
    internal sealed class MixerRow
    {
        public string Key, Name, ExePath; public bool System, Active, Muted; public float Volume, Peak; public readonly List<object> Controls = new List<object>();
    }

    /// <summary>The mixer's pure rules: which sessions are one row, what a row is called, sorting, slider mathematics.</summary>
    internal static class MixerModel
    {
        internal static string KeyOf(RawSession r)
        {
            if (r.IsSystem) return "system";
            if (!string.IsNullOrEmpty(r.ExePath)) return r.ExePath.ToLowerInvariant();
            return r.Pid > 0 ? "pid:" + r.Pid : "unknown";
        }
        /// <summary>Session name (if it is a plain text), else the file description, else the program name.</summary>
        internal static string NameOf(RawSession r)
        {
            if (r.IsSystem) return "系统声音";
            foreach (var c in new[] { r.DisplayName, r.FileDescription })
            {
                string t = (c ?? "").Trim();
                if (t.Length > 0 && t[0] != '@' && !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return t;
            }
            if (!string.IsNullOrEmpty(r.ProcessName)) return r.ProcessName;
            return r.Pid > 0 ? "进程 " + r.Pid : "未知应用";
        }
        /// <summary>One row per application (several sessions of one program — browsers, game launchers — are controlled together); system sounds first, then by name.</summary>
        internal static List<MixerRow> Group(IEnumerable<RawSession> sessions)
        {
            var map = new Dictionary<string, MixerRow>(); var order = new List<MixerRow>();
            foreach (var s in sessions ?? Enumerable.Empty<RawSession>())
            {
                if (s.State == 2) continue;                                   // expired
                if (!s.IsSystem && s.Pid <= 0 && string.IsNullOrEmpty(s.ExePath)) continue;
                string key = KeyOf(s); MixerRow row;
                if (!map.TryGetValue(key, out row))
                {
                    row = new MixerRow { Key = key, Name = NameOf(s), ExePath = s.ExePath, System = s.IsSystem, Volume = s.Volume, Muted = s.Muted, Active = s.State == 1, Peak = s.Peak };
                    map[key] = row; order.Add(row);
                }
                else { row.Active |= s.State == 1; row.Peak = Math.Max(row.Peak, s.Peak); row.Muted &= s.Muted; }
                if (s.Control != null) row.Controls.Add(s.Control);
            }
            return order.OrderBy(r => r.System ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal static float LevelFromX(double x, double width) { return width <= 0 ? 0 : (float)Math.Max(0, Math.Min(1, x / width)); }
        internal static string Percent(float level) { return ((int)Math.Round(Math.Max(0, Math.Min(1, level)) * 100)).ToString(CultureInfo.InvariantCulture); }
        internal static float Step(float level, int wheelDelta) { return (float)Math.Max(0, Math.Min(1, Math.Round(level * 100 + (wheelDelta > 0 ? 2 : -2)) / 100)); }
        /// <summary>Do two readings show the same rows (same applications in the same order)? Then only the values are updated, not the layout.</summary>
        internal static bool SameRows(IList<MixerRow> a, IList<MixerRow> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i].Key != b[i].Key || a[i].Name != b[i].Name) return false;
            return true;
        }
    }

    // ---------------------------------------------------------------- diagnostics: ClearDesk.exe --mixer-diag [file]
    internal static class MixerDiag
    {
        internal static void Run(string[] args)
        {
            string file = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(Path.GetTempPath(), "cleardesk-mixer-diag.txt");
            var sb = new StringBuilder(); string error;
            Action<string> log = t => { sb.AppendLine(t); };
            log("ClearDesk mixer diag " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            // own silent stream, so that this process has a session we may change and restore without touching the user's apps
            System.Media.SoundPlayer player = null;
            try
            {
                var wav = new MemoryStream(); var w = new BinaryWriter(wav); int rate = 8000, samples = rate * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2); w.Write(new byte[samples * 2]); wav.Position = 0;
                player = new System.Media.SoundPlayer(wav); player.PlayLooping(); System.Threading.Thread.Sleep(900);
            }
            catch (Exception e) { log("own stream failed: " + e.Message); }
            var raws = SessionAudio.ReadSessions(out error); log("error: " + (error ?? "none") + "; raw sessions: " + raws.Count);
            foreach (var r in raws) log("  pid=" + r.Pid + " state=" + r.State + " system=" + r.IsSystem + " vol=" + r.Volume.ToString("0.00", CultureInfo.InvariantCulture) + " muted=" + r.Muted + " peak=" + r.Peak.ToString("0.000", CultureInfo.InvariantCulture) + " display='" + r.DisplayName + "' desc='" + r.FileDescription + "' exe=" + r.ExePath);
            var rows = MixerModel.Group(raws); log("rows: " + rows.Count);
            foreach (var r in rows) log("  [" + (r.System ? "S" : r.Active ? "A" : "i") + "] " + r.Name + "  " + MixerModel.Percent(r.Volume) + "%" + (r.Muted ? " muted" : "") + "  sessions=" + r.Controls.Count + "  key=" + r.Key);
            // round trip on our own row only (restored at once)
            var mine = rows.FirstOrDefault(r => r.ExePath != null && string.Equals(r.ExePath, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase));
            if (mine == null) log("own row: not found");
            else
            {
                float original = mine.Volume; bool om = mine.Muted;
                bool set = SessionAudio.SetVolume(mine, 0.37f); var again = MixerModel.Group(SessionAudio.ReadSessions(out error)).FirstOrDefault(r => r.Key == mine.Key);
                log("own row set 0.37: ok=" + set + " read back=" + (again == null ? "?" : again.Volume.ToString("0.00", CultureInfo.InvariantCulture)));
                bool mu = SessionAudio.SetMuted(mine, true); var again2 = MixerModel.Group(SessionAudio.ReadSessions(out error)).FirstOrDefault(r => r.Key == mine.Key); log("own row mute: ok=" + mu + " read back=" + (again2 == null ? "?" : again2.Muted.ToString()));
                SessionAudio.SetMuted(mine, om); SessionAudio.SetVolume(mine, original); var again3 = MixerModel.Group(SessionAudio.ReadSessions(out error)).FirstOrDefault(r => r.Key == mine.Key);
                log("own row restored: vol=" + (again3 == null ? "?" : again3.Volume.ToString("0.00", CultureInfo.InvariantCulture)) + " muted=" + (again3 == null ? "?" : again3.Muted.ToString()));
            }
            if (player != null) player.Stop();
            try { File.WriteAllText(file, sb.ToString(), Encoding.UTF8); } catch { }
        }
    }
}
