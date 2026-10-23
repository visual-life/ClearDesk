using System;
using System.IO;

namespace ClearDesk
{
    // Opt-in music diagnostics: create %LOCALAPPDATA%\ClearDesk\music-trace.on to log reads.
    internal static class MusicTrace
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk");
        static DateTime checkedAt; static bool on;
        internal static void Write(string line)
        {
            try
            {
                if (DateTime.UtcNow - checkedAt > TimeSpan.FromSeconds(5)) { checkedAt = DateTime.UtcNow; on = File.Exists(Path.Combine(Dir, "music-trace.on")); }
                if (!on) return;
                string path = Path.Combine(Dir, "music-trace.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
