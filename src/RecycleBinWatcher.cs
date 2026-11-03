using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.2.6: watch the recycle-bin folders of the fixed drives so the organizer's recycle-bin icon follows a
    /// delete / restore / empty immediately instead of polling it.
    ///
    /// The icon used to be refreshed by a 3 s timer around SHQueryRecycleBin(NULL), which walks every item of every bin
    /// — measured 25-32 s on a machine whose bins held 35 722 files, and it ran on the UI thread.
    ///
    /// SHChangeNotifyRegister on the shell's recycle-bin folder was tried first and does not deliver anything on the
    /// machine this was measured on (a plain file-system folder behaves the same, so it is not the virtual folder):
    /// the registration succeeds but no message is posted. Watching the folder itself is a plain directory change,
    /// which the file system does report. If a watcher cannot be created (no bin on that drive, no access) the caller's
    /// slow timer still keeps the icon roughly right.</summary>
    internal sealed class RecycleBinWatcher : IDisposable
    {
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly DispatcherTimer debounce;
        readonly Action changed;
        bool disposed;

        /// <summary>True when at least one drive is being watched; false means the caller relies on its timer.</summary>
        public bool Active { get { return watchers.Count > 0; } }

        public RecycleBinWatcher(Action changed)
        {
            this.changed = changed;
            // A delete of 500 files raises 500 events; one refresh afterwards is enough.
            debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            debounce.Tick += delegate { debounce.Stop(); Raise(); };

            string sid;
            try
            {
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (user == null) return;
                sid = user.Value;
            }
            catch (Exception e) { Program.Log(e); return; }

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    string dir = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                    if (!Directory.Exists(dir)) continue;
                    var watcher = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    watcher.Created += delegate { Ping(); };
                    watcher.Deleted += delegate { Ping(); };
                    watcher.Renamed += delegate { Ping(); };
                    // Buffer overflow: a probe now costs a few hundred microseconds, a wrong icon lasts until the timer.
                    watcher.Error += delegate { Ping(); };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception e) { Program.Log(e); }
            }
        }

        void Ping()
        {
            if (disposed) return;
            debounce.Stop();
            debounce.Start();
        }

        void Raise()
        {
            if (disposed || changed == null) return;
            try { changed(); } catch (Exception e) { Program.Log(e); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            debounce.Stop();
            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            watchers.Clear();
        }
    }
}
