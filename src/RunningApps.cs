using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ClearDesk
{
    internal sealed class AppIdentity
    {
        public string Key, Exe, AppId, LaunchPath, Name;
        public static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\').ToLowerInvariant();
            }
            catch
            {
                return (path ?? "").ToLowerInvariant();
            }
        }

        public static bool Matches(AppIdentity a, AppIdentity b)
        {
            if (a == null || b == null)
                return false;
            if (!string.IsNullOrEmpty(a.AppId) && !string.IsNullOrEmpty(b.AppId))
                return string.Equals(a.AppId, b.AppId, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(a.Exe) && string.Equals(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class RunningApp
    {
        public AppIdentity Identity;
        public readonly List<IntPtr> Windows = new List<IntPtr>();
        public string Title;
        public IntPtr Preferred;
        public readonly List<string> AncestorExes = new List<string>();   // executables that started this app's process (launcher → client)
    }

    internal sealed class DockEntry
    {
        public string PinPath;
        public AppIdentity Identity;
        public RunningApp Running;
        public bool Pinned
        {
            get
            {
                return PinPath != null;
            }
        }

        public bool IsRunning
        {
            get
            {
                return Running != null && Running.Windows.Count > 0;
            }
        }

        public string IconPath
        {
            get
            {
                return PinPath ?? Identity.LaunchPath ?? Identity.Exe;
            }
        }

        public string Title
        {
            get
            {
                return Pinned ? Program.DisplayName(PinPath) : Identity.Name ?? Running.Title;
            }
        }
    }

    // A snapshot of taskbar-like top-level windows, not all background processes.
    internal sealed class RunningApps : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr h, StringBuilder text, int size);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetApplicationUserModelId(IntPtr h, ref uint length, StringBuilder id);
        [DllImport("user32.dll")]
        static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        static extern IntPtr GetLastActivePopup(IntPtr h);
        readonly Dictionary<string, AppIdentity> pins = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> launches = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IntPtr> recent = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();
        sealed class WindowMetadata { internal uint Pid; internal string AppId; internal DateTime Expires; }
        readonly Dictionary<IntPtr, WindowMetadata> windowMetadata = new Dictionary<IntPtr, WindowMetadata>();
        static readonly Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal int MetadataReads { get; private set; }
        string WindowIdentity(IntPtr window, uint pid)
        {
            WindowMetadata cached;
            if (windowMetadata.TryGetValue(window, out cached) && cached.Pid == pid && cached.Expires > DateTime.UtcNow) return cached.AppId;
            MetadataReads++;
            var id = ShellIdentity.WindowAppId(window);
            windowMetadata[window] = new WindowMetadata { Pid=pid, AppId=id, Expires=DateTime.UtcNow.AddSeconds(10) };
            return id;
        }
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly Dictionary<uint, KeyValuePair<DateTime, List<string>>> ancestors = new Dictionary<uint, KeyValuePair<DateTime, List<string>>>();
        readonly HashSet<IntPtr> restoring = new HashSet<IntPtr>();
        internal IWindowOps Ops = WinOps.Instance;
        internal Func<uint, List<string>> AncestorLookup = pid => ProcessTree.AncestorExes(pid, 4);
        List<string> AncestorsOf(uint pid)
        {
            KeyValuePair<DateTime, List<string>> c;
            if (ancestors.TryGetValue(pid, out c) && c.Key > DateTime.UtcNow) return c.Value;
            var list = AncestorLookup(pid);
            if (ancestors.Count > 256) ancestors.Clear();
            ancestors[pid] = new KeyValuePair<DateTime, List<string>>(DateTime.UtcNow.AddSeconds(60), list);
            return list;
        }
        DispatcherTimer timer;
        bool disposed;
        string signature;
        public List<RunningApp> Apps = new List<RunningApp>();
        public event Action Changed;
        public void Start()
        {
            ForegroundTracker.Start();
            Refresh();
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            timer.Tick += delegate
            {
                if (DateTime.UtcNow >= LaunchpadWindow.AnimationUntil)
                    Refresh();
            };
            timer.Start();
        }

        public void Refresh()
        {
            if (disposed)
                return;
            var seen = new HashSet<IntPtr>();
            var groups = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
            var processIds = new Dictionary<uint, AppIdentity>();
            IntPtr foreground = Native.GetForegroundWindow();
            Native.EnumWindows(delegate (IntPtr h, IntPtr unused)
            {
                try
                {
                    if (!Native.IsWindowVisible(h))
                        return true;
                    long style = Native.GetLong(h, -20).ToInt64();
                    if ((style & 0x80) != 0 || (style & 0x08000000) != 0)
                        return true;
                    if (GetWindow(h, 4) != IntPtr.Zero && (style & 0x40000) == 0)
                        return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0)
                        return true;
                    string cls = Native.Class(h);
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                        return true;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if (pid == ownPid)
                        return true;
                    var title = new StringBuilder(512);
                    GetWindowText(h, title, title.Capacity);
                    if (title.Length == 0)
                        return true;
                    AppIdentity process;
                    if (!processIds.TryGetValue(pid, out process))
                    {
                        process = ProcessIdentity(pid);
                        processIds[pid] = process;
                    }

                    if (process == null)
                        return true;
                    seen.Add(h);
                    string windowId = WindowIdentity(h, pid);
                    string id = string.IsNullOrEmpty(windowId) ? process.AppId : windowId;
                    string key = string.IsNullOrEmpty(id) ? process.Exe : "app:" + id.ToLowerInvariant();
                    if (string.IsNullOrEmpty(key))
                        key = "pid:" + pid;
                    RunningApp app;
                    if (!groups.TryGetValue(key, out app))
                    {
                        app = new RunningApp
                        {
                            Identity = new AppIdentity
                            {
                                Key = key,
                                Exe = process.Exe,
                                AppId = id,
                                LaunchPath = process.LaunchPath,
                                Name = process.Name
                            },
                            Title = title.ToString()
                        };
                        groups.Add(key, app);
                    }

                    app.Windows.Add(h);
                    foreach (var a in AncestorsOf(pid)) if (!app.AncestorExes.Contains(a)) app.AncestorExes.Add(a);
                    if (h == foreground)
                        recent[key] = h;
                }
                catch
                {
                }

                return true;
            }, IntPtr.Zero);
            foreach (var handle in windowMetadata.Keys.Where(h => !seen.Contains(h)).ToArray()) windowMetadata.Remove(handle);
            order.RemoveAll(k => !groups.ContainsKey(k));
            foreach (string key in groups.Keys)
                if (!order.Contains(key))
                    order.Add(key);
            Apps = order.Select(k => groups[k]).ToList();
            foreach (var app in Apps)
            {
                IntPtr preferred;
                app.Preferred = recent.TryGetValue(app.Identity.Key, out preferred) && app.Windows.Contains(preferred) ? preferred : app.Windows[0];
            }

            foreach (var key in recent.Keys.Where(k => !groups.ContainsKey(k)).ToArray())
                recent.Remove(key);
            string next = string.Join("|", Apps.Select(a => a.Identity.Key + ":" + string.Join(",", a.Windows.Select(w => w.ToInt64()).OrderBy(w => w).Select(w => w.ToString()))));
            if (next != signature)
            {
                signature = next;
                if (Changed != null)
                    Changed();
            }
        }

        static AppIdentity ProcessIdentity(uint pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var path = new StringBuilder(1024);
                int count = path.Capacity;
                bool gotPath = QueryFullProcessImageName(handle, 0, path, ref count);
                if (!gotPath) { path = new StringBuilder(32768); count = path.Capacity; gotPath = QueryFullProcessImageName(handle, 0, path, ref count); }
                string exe = gotPath ? AppIdentity.Normalize(path.ToString()) : null;
                uint length = 0;
                GetApplicationUserModelId(handle, ref length, null);
                string id = null;
                if (length > 0 && length < 32768)
                {
                    var text = new StringBuilder((int)length);
                    if (GetApplicationUserModelId(handle, ref length, text) == 0)
                        id = text.ToString();
                }

                if (exe == null && id == null)
                    return null;
                string name = exe == null ? id : Path.GetFileNameWithoutExtension(exe);
                if (exe != null)
                {
                    try
                    {
                        string display;
                        if (!descriptions.TryGetValue(exe, out display))
                        {
                            display = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                            if (descriptions.Count >= 256) descriptions.Clear();
                            descriptions[exe] = display;
                        }
                        if (!string.IsNullOrWhiteSpace(display)) name = display;
                    }
                    catch
                    {
                    }
                }

                return new AppIdentity
                {
                    Exe = exe,
                    AppId = id,
                    Name = name,
                    LaunchPath = id == null ? exe : "shell:AppsFolder\\" + id
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public AppIdentity ResolvePin(string path)
        {
            AppIdentity id;
            if (!pins.TryGetValue(path, out id))
            {
                id = ShellIdentity.Resolve(path);
                pins[path] = id;
            }

            return id;
        }

        /// <summary>The running app for a Dock entry: same app id / executable, otherwise the app a pinned launcher started (RiotClientServices → LeagueClientUx).</summary>
        RunningApp FindApp(AppIdentity id, IEnumerable<AppIdentity> otherPins)
        {
            var direct = Apps.FirstOrDefault(a => AppIdentity.Matches(id, a.Identity));
            if (direct != null || id == null || string.IsNullOrEmpty(id.Exe)) return direct;
            return Apps.FirstOrDefault(a => DockRestorer.AncestorMatch(id.Exe, a.AncestorExes)
                && !(otherPins ?? Enumerable.Empty<AppIdentity>()).Any(o => o != id && AppIdentity.Matches(o, a.Identity)));
        }

        public List<DockEntry> Entries(IEnumerable<string> pinned)
        {
            var result = new List<DockEntry>();
            var list = pinned.Take(32).ToList();
            var ids = list.Select(ResolvePin).ToList();
            for (int i = 0; i < list.Count; i++)
                result.Add(new DockEntry { PinPath = list[i], Identity = ids[i], Running = FindApp(ids[i], ids) });
            foreach (var app in Apps)
                if (!result.Any(e => e.Running == app || AppIdentity.Matches(e.Identity, app.Identity)))
                    result.Add(new DockEntry { Identity = app.Identity, Running = app });
            return result;
        }

        public void ActivateOrLaunch(DockEntry entry)
        {
            Refresh();
            var app = FindApp(entry.Identity, null);
            if (app != null && app.Windows.Count > 0)
            {
                // Like the taskbar: clicking the app that was in the foreground minimizes it.
                IntPtr target;
                if (DockClick.Decide(app.Windows, app.Preferred, ForegroundTracker.Last, Ops.IsIconic, ForegroundTracker.RootOwner, out target) == DockClick.Action.Minimize)
                    DockRestorer.Minimize(target, Ops);
                else
                    RestoreAsync(target, entry);
                return;
            }

            // No visible window: an app minimized to the tray keeps a hidden main window, show that before starting a second copy.
            var hidden = HiddenWindows(entry.Identity);
            if (hidden.Count > 0) { RestoreAsync(hidden[0], entry); return; }
            Launch(entry);
        }

        void Launch(DockEntry entry)
        {
            string path = entry.PinPath ?? entry.Identity.LaunchPath;
            if (string.IsNullOrEmpty(path))
                return;
            string key = entry.Identity.Key ?? path;
            DateTime previous;
            if (launches.TryGetValue(key, out previous) && (DateTime.UtcNow - previous).TotalSeconds < 3)
                return;
            launches[key] = DateTime.UtcNow;
            Program.OpenItem(path);
        }

        internal List<string> LastRestoreLog = new List<string>();
        /// <summary>Restores off the UI thread; when the window cannot be restored (elevated and hidden, or it vanished) the app is started again, which single-instance apps answer by showing their window.</summary>
        void RestoreAsync(IntPtr window, DockEntry entry)
        {
            lock (restoring) { if (!restoring.Add(window)) return; }
            var dispatcher = System.Windows.Application.Current != null ? System.Windows.Application.Current.Dispatcher : null;
            System.Threading.Tasks.Task.Run(delegate
            {
                var log = new List<string>(); DockRestorer.Result result = DockRestorer.Result.Failed;
                try { result = DockRestorer.Restore(window, Ops, System.Threading.Thread.Sleep, log); }
                catch (Exception e) { log.Add("error " + e.Message); }
                finally { lock (restoring) restoring.Remove(window); }
                LastRestoreLog = log;
                if (result == DockRestorer.Result.Restored) return;
                Action relaunch = delegate { if (!disposed && entry != null) Launch(entry); };
                if (dispatcher != null) dispatcher.BeginInvoke(relaunch); else relaunch();
            });
        }

        public int CloseApplication(DockEntry entry)
        {
            Refresh();
            var app = Apps.FirstOrDefault(a => AppIdentity.Matches(a.Identity, entry.Identity));
            if (app == null) return 0;
            int sent = 0;
            foreach (var window in app.Windows.ToArray())
            {
                uint pid; Native.GetWindowThreadProcessId(window, out pid);
                // Recheck identity immediately before sending WM_CLOSE, never terminate a process.
                var identity = ProcessIdentity(pid);
                if (pid == ownPid || !Native.IsWindow(window) || identity == null ||
                    !string.Equals(identity.Exe, app.Identity.Exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero)) sent++;
            }
            return sent;
        }

        /// <summary>Restores and activates one window (also elevated ones). Used by the Dock's 已打开的窗口 menu.</summary>
        internal static void Activate(IntPtr window)
        {
            System.Threading.Tasks.Task.Run(delegate { try { DockRestorer.Restore(window, WinOps.Instance, System.Threading.Thread.Sleep, null); } catch { } });
        }

        /// <summary>Hidden main windows (tray apps) that belong to this app (same app id or executable).</summary>
        List<IntPtr> HiddenWindows(AppIdentity id) { return HiddenWindowsFor(id, null); }
        internal List<IntPtr> HiddenWindowsFor(AppIdentity id, string exeFragment)
        {
            var found = new List<IntPtr>(); if (id == null) return found;
            var byPid = new Dictionary<uint, AppIdentity>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr unused)
            {
                try
                {
                    if (Native.IsWindowVisible(h)) return true;
                    long style = Native.GetLong(h, -16).ToInt64(), ex = Native.GetLong(h, -20).ToInt64();
                    var title = new StringBuilder(256); int titleLength = GetWindowText(h, title, title.Capacity); if (titleLength == 0) return true;
                    if ((style & 0xC00000) != 0xC00000) return true;   // cheap pre-filter before the more expensive checks
                    Native.RECT r; Native.GetWindowRect(h, out r);
                    if (!DockRestorer.IsMainWindowCandidate(style, ex, GetWindow(h, 4), false, titleLength, r.R - r.L, r.B - r.T)) return true;
                    uint pid; Native.GetWindowThreadProcessId(h, out pid); if (pid == ownPid) return true;
                    AppIdentity p; if (!byPid.TryGetValue(pid, out p)) byPid[pid] = p = ProcessIdentity(pid);
                    if (p != null && (exeFragment != null ? (p.Exe ?? "").Contains(exeFragment) : AppIdentity.Matches(id, p))) found.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public string PinTarget(DockEntry entry)
        {
            if (entry.Pinned)
                return entry.PinPath;
            var match = Program.DesktopItems().Concat(StartMenuLinks()).FirstOrDefault(path => AppIdentity.Matches(ResolvePin(path), entry.Identity));
            if (match != null)
                return match;
            return entry.Identity.LaunchPath;
        }

        static IEnumerable<string> StartMenuLinks()
        {
            var files = new List<string>();
            foreach (var folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            }

            )
                try
                {
                    files.AddRange(Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories));
                }
                catch
                {
                }

            return files;
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null)
                timer.Stop();
            windowMetadata.Clear();
            Changed = null;
        }
    }

    internal static class ShellIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey
        {
            public Guid Format;
            public uint Id;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)]
            public ushort Type;
            [FieldOffset(8)]
            public IntPtr Pointer;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant value);
        public static string WindowAppId(IntPtr h)
        {
            IPropertyStore store = null;
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(h, ref iid, out store) != 0)
                    return null;
                var key = new PropertyKey
                {
                    Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                    Id = 5
                };
                PropVariant value;
                store.GetValue(ref key, out value);
                try
                {
                    return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (store != null)
                    Marshal.ReleaseComObject(store);
            }
        }

        public static AppIdentity Resolve(string path)
        {
            string target = path, id = null;
            object shell = null, shortcut = null, folder = null, item = null;
            try
            {
                if (path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    id = path.Substring(17);
                    target = null;
                }
                else if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    shortcut = ((dynamic)shell).CreateShortcut(path);
                    target = (string)((dynamic)shortcut).TargetPath;
                    Marshal.ReleaseComObject(shortcut);
                    shortcut = null;
                    Marshal.ReleaseComObject(shell);
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    folder = ((dynamic)shell).NameSpace(Path.GetDirectoryName(path));
                    if (folder != null)
                    {
                        item = ((dynamic)folder).ParseName(Path.GetFileName(path));
                        if (item != null)
                            id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var com in new[]
                {
                    item,
                    folder,
                    shortcut,
                    shell
                }

                )
                    if (com != null && Marshal.IsComObject(com))
                        Marshal.ReleaseComObject(com);
            }

            if (Directory.Exists(target))
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string exe = string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) ? AppIdentity.Normalize(target) : null;
            if (string.IsNullOrWhiteSpace(id))
                id = null;
            return new AppIdentity
            {
                Key = id == null ? (exe ?? path) : "app:" + id.ToLowerInvariant(),
                Exe = exe,
                AppId = id,
                LaunchPath = path,
                Name = Program.DisplayName(path)
            };
        }
    }

    // Decides what a Dock click on a running app does (pure, testable).
    internal static class DockClick
    {
        internal enum Action { Activate, Minimize }
        internal static Action Decide(IList<IntPtr> windows, IntPtr preferred, IntPtr lastForeground, Func<IntPtr, bool> iconic, Func<IntPtr, IntPtr> rootOwner, out IntPtr target)
        {
            target = preferred;
            if (lastForeground != IntPtr.Zero)
            {
                IntPtr root = rootOwner(lastForeground);
                foreach (var w in windows)
                    if ((w == lastForeground || w == root) && !iconic(w))
                    {
                        target = w;
                        return Action.Minimize;
                    }
            }
            if (target == IntPtr.Zero && windows.Count > 0) target = windows[0];
            return Action.Activate;
        }
    }

    // Remembers the last foreground window that is not ClearDesk's own, because clicking
    // the Dock activates the Dock itself before the click handler runs.
    internal static class ForegroundTracker
    {
        static Native.EventProc proc;
        static IntPtr hook;
        static readonly uint own = (uint)Process.GetCurrentProcess().Id;
        internal static IntPtr Last;
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        internal static IntPtr RootOwner(IntPtr h) { var r = GetAncestor(h, 3); return r == IntPtr.Zero ? h : r; }
        internal static void Start()
        {
            if (hook != IntPtr.Zero) return;
            proc = delegate (IntPtr hk, uint evt, IntPtr h, int obj, int child, uint thread, uint time)
            {
                if (obj != 0 || h == IntPtr.Zero) return;
                if (evt == 0x16) { if (RootOwner(Last) == RootOwner(h)) Last = IntPtr.Zero; return; }
                if (evt == 3) Observe(h);
            };
            hook = Native.SetWinEventHook(3, 0x16, IntPtr.Zero, proc, 0, 0, 0);
            Observe(Native.GetForegroundWindow());
        }
        internal static void Observe(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == own) return;
            Last = h;
        }
    }
}
