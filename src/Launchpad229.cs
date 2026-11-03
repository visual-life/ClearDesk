using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace ClearDesk
{
    /// <summary>2.2.9: what the launchpad shows. Desktop items + pinned items (in the saved order), minus the removed ones.
    /// Removing only hides an entry (or unpins it); no real file is ever touched.</summary>
    internal static class LaunchpadStore
    {
        static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        static List<string> Pinned { get { return Program.S.LaunchpadItems ?? (Program.S.LaunchpadItems = new List<string>()); } }
        static List<string> Hidden { get { return Program.S.LaunchpadHidden ?? (Program.S.LaunchpadHidden = new List<string>()); } }

        internal static List<string> Compose(IEnumerable<string> desktop, IList<string> pinned, IList<string> hidden)
        {
            var hide = new HashSet<string>(hidden ?? new string[0], Cmp);
            var pin = new HashSet<string>(pinned ?? new string[0], Cmp);
            // pinned items keep their order (the user can reorder them) and come first; then the rest of the desktop
            return (pinned ?? new string[0]).Concat((desktop ?? new string[0]).Where(d => !pin.Contains(d))).Distinct(Cmp).Where(p => !hide.Contains(p)).ToList();
        }
        internal static List<string> Paths() { return Compose(Program.DesktopItems(), Pinned, Hidden); }
        internal static bool IsPinned(string path) { return Pinned.Contains(path, Cmp); }
        internal static IList<string> Removed { get { return Hidden.ToList(); } }

        internal static int Pin(IEnumerable<string> paths)
        {
            int n = 0;
            foreach (var raw in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string p = raw.Trim().Trim('"');
                Hidden.RemoveAll(h => Cmp.Equals(h, p));
                if (!Pinned.Contains(p, Cmp)) { Pinned.Add(p); n++; }
            }
            Changed();
            return n;
        }
        /// <summary>Hides the entry (a pinned one keeps its place, so 恢复 puts it back where it was). Never deletes anything on disk.</summary>
        internal static void Remove(string path)
        {
            if (!Hidden.Contains(path, Cmp)) Hidden.Add(path);
            Changed();
        }
        internal static void Restore(string path)
        {
            if (path == null) Hidden.Clear(); else Hidden.RemoveAll(h => Cmp.Equals(h, path));
            Changed();
        }
        /// <summary>Moves a pinned entry one place earlier (-1) or later (+1).</summary>
        internal static bool Move(string path, int delta)
        {
            int i = Pinned.FindIndex(p => Cmp.Equals(p, path)), j = i + delta;
            if (i < 0 || j < 0 || j >= Pinned.Count) return false;
            var x = Pinned[i]; Pinned.RemoveAt(i); Pinned.Insert(j, x);
            Changed();
            return true;
        }
        internal static Action TestChanged;
        static void Changed()
        {
            Program.Save();
            if (TestChanged != null) TestChanged();
            foreach (var w in Program.OpenWindows.OfType<LaunchpadWindow>().ToArray()) if (!w.WasClosed) w.ReloadItems();
        }
        internal static IEnumerable<string> PinArgs(string[] args)
        {
            int i = Array.IndexOf(args ?? new string[0], "--pin-launchpad");
            return i < 0 ? Enumerable.Empty<string>() : args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--"));
        }
    }

    /// <summary>2.2.9: 以管理员身份运行 (ShellExecute "runas"). Cancelling UAC (1223) is silent.</summary>
    internal static class AdminRun
    {
        static readonly string[] Runnable = { ".exe", ".bat", ".cmd", ".msc", ".com" };
        /// <summary>Tests replace this; never shows a UAC prompt then.</summary>
        internal static Func<ProcessStartInfo, Process> Starter = psi => Process.Start(psi);
        internal static Func<string, string> ShortcutTarget = ReadShortcut;

        internal static bool Supported(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || SystemItems.IsSystem(path)) return false;
            if (Directory.Exists(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Runnable.Contains(ext)) return File.Exists(path);
            if (ext == ".lnk")
            {
                string t = ShortcutTarget(path);    // UWP / store shortcuts have no file target
                if (string.IsNullOrEmpty(t) || t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Directory.Exists(t)) return false;
                if (t.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase) && t.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return Runnable.Contains(Path.GetExtension(t).ToLowerInvariant());
            }
            return false;
        }

        /// <summary>true = started, false = cancelled (UAC 1223) or failed (a failure other than cancel shows a message unless quiet).</summary>
        internal static bool Run(string path, bool quiet = false)
        {
            if (!Supported(path)) return false;
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
                var p = Starter(psi); if (p != null) p.Dispose();
                return true;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }       // the user said No to UAC
            catch (Exception e) { Program.Log(e); if (!quiet) MessageBox.Show("无法以管理员身份运行：" + e.Message, "ClearDesk"); return false; }
        }

        static string ReadShortcut(string path)
        {
            object shell = null, sc = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                sc = ((dynamic)shell).CreateShortcut(path);
                return (string)((dynamic)sc).TargetPath;
            }
            catch { return null; }
            finally
            {
                if (sc != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
                if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }
}
