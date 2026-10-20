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
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        DispatcherTimer timer;
        bool disposed;
        string signature;
        public List<RunningApp> Apps = new List<RunningApp>();
        public event Action Changed;
        public void Start()
        {
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
                    string windowId = ShellIdentity.WindowAppId(h);
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
                    if (h == foreground)
                        recent[key] = h;
                }
                catch
                {
                }

                return true;
            }, IntPtr.Zero);
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
                var path = new StringBuilder(32768);
                int count = path.Capacity;
                string exe = QueryFullProcessImageName(handle, 0, path, ref count) ? AppIdentity.Normalize(path.ToString()) : null;
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
                        string display = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                        if (!string.IsNullOrWhiteSpace(display))
                            name = display;
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

        public List<DockEntry> Entries(IEnumerable<string> pinned)
        {
            var result = new List<DockEntry>();
            foreach (var path in pinned.Take(32))
            {
                var id = ResolvePin(path);
                result.Add(new DockEntry { PinPath = path, Identity = id, Running = Apps.FirstOrDefault(a => AppIdentity.Matches(id, a.Identity)) });
            }

            foreach (var app in Apps)
                if (!result.Any(e => AppIdentity.Matches(e.Identity, app.Identity)))
                    result.Add(new DockEntry { Identity = app.Identity, Running = app });
            return result;
        }

        public void ActivateOrLaunch(DockEntry entry)
        {
            Refresh();
            var app = Apps.FirstOrDefault(a => AppIdentity.Matches(entry.Identity, a.Identity));
            if (app != null && app.Windows.Count > 0)
            {
                Activate(app.Preferred);
                return;
            }

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

        internal static void Activate(IntPtr window)
        {
            if (!Native.IsWindow(window))
                return;
            if (Native.IsIconic(window))
                ShowWindowAsync(window, 9);
            IntPtr popup = GetLastActivePopup(window);
            if (popup != IntPtr.Zero && Native.IsWindowVisible(popup))
                window = popup;
            SetForegroundWindow(window);
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
}
