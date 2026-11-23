using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClearDesk
{
    internal sealed class AppIdentity
    {
        public string Key, Exe, AppId, LaunchPath, Name;
        /// <summary>2.2.3: for an app hosted by ApplicationFrameHost (UWP), the executable of the app itself (the host's own icon is a blank window).</summary>
        public string RealExe;
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
        public readonly Dictionary<IntPtr, uint> WindowProcesses = new Dictionary<IntPtr, uint>();
        public string Title;
        public bool InTray;
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
                return Running != null && (Running.Windows.Count > 0 || Running.InTray);
            }
        }

        /// <summary>What the icon lookup needs for an app that is not pinned.</summary>
        internal AppIconRequest IconRequest()
        {
            var i = Identity;
            return new AppIconRequest { Exe = i.RealExe ?? i.Exe, AppId = i.AppId, Name = i.Name, LaunchPath = i.LaunchPath, Windows = Running == null ? new IntPtr[0] : Running.Windows.Take(4).ToArray() };
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
        readonly object scanGate = new object();
        readonly Dictionary<string, AppIdentity> pins = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> launches = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IntPtr> recent = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, RunningApp> trayResidents = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        sealed class WindowMetadata { internal uint Pid; internal string AppId; internal DateTime Expires; }
        readonly Dictionary<IntPtr, WindowMetadata> windowMetadata = new Dictionary<IntPtr, WindowMetadata>();
        static readonly Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal int MetadataReads { get; private set; }
        internal Func<IntPtr, string> WindowAppIdLookup = ShellIdentity.WindowAppId;
        string WindowIdentity(IntPtr window, uint pid)
        {
            WindowMetadata cached;
            if (windowMetadata.TryGetValue(window, out cached) && cached.Pid == pid && cached.Expires > DateTime.UtcNow) return cached.AppId;
            MetadataReads++;
            var id = WindowAppIdLookup(window);
            windowMetadata[window] = new WindowMetadata { Pid=pid, AppId=id, Expires=DateTime.UtcNow.AddSeconds(10) };
            return id;
        }
        // 2.2.3: UWP windows belong to ApplicationFrameHost.exe; the app itself runs in another process whose CoreWindow is a child of the frame window
        sealed class HostedApp { internal AppIdentity Real; internal DateTime Expires; }
        readonly Dictionary<IntPtr, HostedApp> hosted = new Dictionary<IntPtr, HostedApp>();
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Native.EnumProc callback, IntPtr parameter);
        internal Func<IntPtr, uint, AppIdentity> HostedLookup;
        AppIdentity RealApp(IntPtr frame, uint hostPid)
        {
            HostedApp c;
            if (hosted.TryGetValue(frame, out c) && c.Expires > DateTime.UtcNow) return c.Real;
            AppIdentity real = null;
            try
            {
                if (HostedLookup != null) real = HostedLookup(frame, hostPid);
                else
                {
                    uint found = 0;
                    EnumChildWindows(frame, delegate (IntPtr child, IntPtr unused)
                    {
                        uint cp; Native.GetWindowThreadProcessId(child, out cp);
                        if (cp != 0 && cp != hostPid && cp != ownPid && Native.Class(child) == "Windows.UI.Core.CoreWindow") { found = cp; return false; }
                        return true;
                    }, IntPtr.Zero);
                    if (found != 0) real = ProcessIdentity(found);
                }
            }
            catch { }
            if (hosted.Count > 128) hosted.Clear();
            hosted[frame] = new HostedApp { Real = real, Expires = DateTime.UtcNow.AddSeconds(real != null ? 60 : 3) };
            return real;
        }
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly Dictionary<uint, KeyValuePair<DateTime, List<string>>> ancestors = new Dictionary<uint, KeyValuePair<DateTime, List<string>>>();
        readonly HashSet<string> activating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal IWindowOps Ops = WinOps.Instance;
        internal Func<string, Task> OpenLaunchItem = path => Program.OpenItemAsync(path);
        internal Func<DockEntry, AppIdentity, TrayWakeResult> WakeTray = DockTrayWake.TryWake;
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
        volatile bool disposed;
        Task scanTask;
        string signature;
        public List<RunningApp> Apps = new List<RunningApp>();
        public event Action Changed;
        internal event Action<string> ActivationNotice;
        public void Start()
        {
            ForegroundTracker.Start();
            RequestRefresh();
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            timer.Tick += delegate
            {
                if (DateTime.UtcNow >= LaunchpadWindow.AnimationUntil)
                    RequestRefresh();
            };
            timer.Start();
        }

        public void Refresh()
        {
            if (!disposed) ApplySnapshot(Scan());
        }

        internal Task RequestRefresh()
        {
            if (disposed) return Task.CompletedTask;
            if (scanTask == null || scanTask.IsCompleted) scanTask = ScanAsync();
            return scanTask;
        }

        async Task ScanAsync()
        {
            try
            {
                var snapshot = await Task.Run(Scan);
                if (!disposed) ApplySnapshot(snapshot);
            }
            catch (Exception e) { Program.Log(e); }
        }

        List<RunningApp> Scan()
        {
            lock (scanGate) return ReadWindows();
        }

        List<RunningApp> ReadWindows()
        {
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
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Ghost")
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
                        var identity = new AppIdentity
                        {
                            Key = key,
                            Exe = process.Exe,
                            AppId = id,
                            LaunchPath = process.LaunchPath,
                            Name = process.Name
                        };
                        if (AppIcons.IsHostExe(process.Exe))
                        {
                            var real = RealApp(h, pid);
                            if (real != null) { identity.RealExe = real.Exe; if (!string.IsNullOrWhiteSpace(real.Name)) identity.Name = real.Name; if (string.IsNullOrEmpty(id)) { id = real.AppId; identity.AppId = id; } }
                            if (!string.IsNullOrEmpty(id)) identity.LaunchPath = "shell:AppsFolder\\" + id;
                            string shown = ShellApps.DisplayName(id); if (!string.IsNullOrWhiteSpace(shown)) identity.Name = shown;
                            if (real == null && string.IsNullOrWhiteSpace(shown)) identity.Name = title.ToString();
                        }
                        app = new RunningApp
                        {
                            Identity = identity,
                            Title = title.ToString()
                        };
                        groups.Add(key, app);
                    }

                    app.Windows.Add(h);
                    app.WindowProcesses[h] = pid;
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
            foreach (var handle in hosted.Keys.Where(h => !seen.Contains(h)).ToArray()) hosted.Remove(handle);
            KeepTrayResidents(groups, trayResidents, DockTrayWake.IsProcess);
            order.RemoveAll(k => !groups.ContainsKey(k));
            foreach (string key in groups.Keys)
                if (!order.Contains(key))
                    order.Add(key);
            var snapshot = order.Select(k => groups[k]).ToList();
            foreach (var app in snapshot)
            {
                IntPtr preferred;
                app.Preferred = recent.TryGetValue(app.Identity.Key, out preferred) && app.Windows.Contains(preferred) ? preferred : app.Windows.FirstOrDefault();
            }

            foreach (var key in recent.Keys.Where(k => !groups.ContainsKey(k)).ToArray())
                recent.Remove(key);
            return snapshot;
        }

        internal static void KeepTrayResidents(Dictionary<string, RunningApp> groups, Dictionary<string, RunningApp> residents,
            Func<uint, string, bool> isProcess)
        {
            foreach (var app in groups.Values.Where(a => DockTrayWake.IsKnown(a.Identity)))
                residents[app.Identity.Key] = app;
            foreach (var key in residents.Keys.Where(k => !groups.ContainsKey(k)).ToArray())
            {
                var resident = residents[key];
                var pidStillThere = resident.WindowProcesses.Values.Distinct().Any(pid => isProcess(pid, resident.Identity.Exe));
                if (!pidStillThere) { residents.Remove(key); continue; }
                groups.Add(key, new RunningApp { Identity = resident.Identity, Title = resident.Title, InTray = true });
            }
        }

        void ApplySnapshot(List<RunningApp> snapshot)
        {
            Apps = snapshot;
            string next = string.Join("|", Apps.Select(a => a.Identity.Key + ":" + string.Join(",", a.Windows.Select(w => w.ToInt64()).OrderBy(w => w).Select(w => w.ToString()))));
            if (next != signature)
            {
                signature = next;
                if (Changed != null)
                    Changed();
            }
        }

        internal static AppIdentity ProcessIdentity(uint pid)
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
                        bool cached;
                        lock (descriptions) cached = descriptions.TryGetValue(exe, out display);
                        if (!cached)
                        {
                            display = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                            lock (descriptions)
                            {
                                if (descriptions.Count >= 256) descriptions.Clear();
                                descriptions[exe] = display;
                            }
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
            lock (pins) if (pins.TryGetValue(path, out id)) return id;
            id = ShellIdentity.Resolve(path);
            lock (pins) pins[path] = id;
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

        public async void ActivateOrLaunch(DockEntry entry)
        {
            if (disposed) return;
            var app = FindApp(entry.Identity, null) ?? entry.Running;
            string key = app?.Identity.Key ?? entry.Identity.Key ?? entry.PinPath ?? entry.Identity.LaunchPath;
            if (string.IsNullOrEmpty(key) || !activating.Add(key)) return;
            IntPtr previous = ForegroundTracker.Last;
            var ops = Ops;
            try
            {
                var result = await TryWindow(app, previous, ops);
                if (result == DockRestorer.Result.Gone && !disposed)
                {
                    // Metadata discovery can wait on a shell extension. Give it a short chance
                    // to find a new window, but keep this launch independent of a stalled scan.
                    await Task.WhenAny(RequestRefresh(), Task.Delay(220));
                    var latest = FindApp(entry.Identity, null);
                    if (!disposed && !ReferenceEquals(app, latest)) result = await TryWindow(latest, previous, ops);
                }
                if (disposed) return;
                if (result == DockRestorer.Result.Gone)
                {
                    var knownApp = FindApp(entry.Identity, null) ?? app;
                    var wake = await Task.Run(() => disposed ? TrayWakeResult.Unavailable : WakeTray(entry, knownApp?.Identity));
                    if (disposed) return;
                    if (wake == TrayWakeResult.NotApplicable) await Launch(entry);
                    else if (wake == TrayWakeResult.Unavailable) ActivationNotice?.Invoke("应用已在后台运行，未能唤回托盘窗口");
                    else _ = RequestRefresh();
                }
                else if (result == DockRestorer.Result.NotResponding) ActivationNotice?.Invoke("应用暂未响应");
            }
            catch (Exception e) { Program.Log(e); }
            finally { activating.Remove(key); }
        }

        Task Launch(DockEntry entry)
        {
            string path = entry.PinPath ?? entry.Identity.LaunchPath;
            if (string.IsNullOrEmpty(path))
                return Task.CompletedTask;
            string key = entry.Identity.Key ?? path;
            DateTime previous;
            if (launches.TryGetValue(key, out previous) && (DateTime.UtcNow - previous).TotalSeconds < 3)
                return Task.CompletedTask;
            launches[key] = DateTime.UtcNow;
            return OpenLaunchItem(path);
        }

        static bool MatchesWindow(RunningApp app, IntPtr window, IWindowOps ops)
        {
            if (!ops.IsWindow(window) || !ops.IsVisible(window)) return false;
            uint expected, actual;
            if (!app.WindowProcesses.TryGetValue(window, out expected)) return true;
            Native.GetWindowThreadProcessId(window, out actual);
            if (actual != expected) return false;
            int cloaked;
            return DwmGetWindowAttribute(window, 14, out cloaked, 4) != 0 || cloaked == 0;
        }

        Task<DockRestorer.Result> TryWindow(RunningApp app, IntPtr previous, IWindowOps ops)
        {
            if (app == null) return Task.FromResult(DockRestorer.Result.Gone);
            return Task.Run(delegate
            {
                if (disposed) return DockRestorer.Result.Failed;
                var windows = app.Windows.Where(window => MatchesWindow(app, window, ops)).ToList();
                if (windows.Count == 0) return DockRestorer.Result.Gone;
                IntPtr target;
                IntPtr preferred = windows.Contains(app.Preferred) ? app.Preferred : windows[0];
                var action = DockClick.Decide(windows, preferred, previous, ops.IsIconic, ForegroundTracker.RootOwner, out target);
                var remaining = DockRestorer.Result.Gone;
                foreach (var window in new[] { target }.Concat(windows).Distinct().Take(4))
                {
                    if (disposed) return DockRestorer.Result.Failed;
                    if (!MatchesWindow(app, window, ops)) continue;
                    if (!ops.IsResponsive(window)) { remaining = DockRestorer.Result.NotResponding; continue; }
                    if (window == target && action == DockClick.Action.Minimize)
                        return DockRestorer.Minimize(window, ops) ? DockRestorer.Result.Restored : DockRestorer.Result.Failed;
                    var result = DockRestorer.Restore(window, ops, Thread.Sleep, null);
                    if (result == DockRestorer.Result.Restored || result == DockRestorer.Result.Failed) return result;
                    if (result == DockRestorer.Result.NotResponding) remaining = result;
                }
                // Never relaunch a visible window that is stalled or refused activation.
                return remaining == DockRestorer.Result.Gone && windows.Any(window => MatchesWindow(app, window, ops))
                    ? DockRestorer.Result.Failed : remaining;
            });
        }

        public int CloseApplication(DockEntry entry)
        {
            var app = Apps.FirstOrDefault(a => AppIdentity.Matches(a.Identity, entry.Identity));
            if (app == null) return 0;
            int sent = 0;
            foreach (var window in app.Windows.ToArray())
            {
                uint pid; Native.GetWindowThreadProcessId(window, out pid);
                // Recheck identity immediately before sending WM_CLOSE, never terminate a process.
                if (pid == ownPid || !MatchesWindow(app, window, Ops) ||
                    !string.Equals(ProcessTree.ExeOf(pid), app.Identity.Exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero)) sent++;
            }
            return sent;
        }

        internal async void ActivateWindow(RunningApp app, IntPtr window)
        {
            try
            {
                var result = await Task.Run(() => !disposed && MatchesWindow(app, window, WinOps.Instance)
                    ? DockRestorer.Restore(window, WinOps.Instance, Thread.Sleep, null)
                    : DockRestorer.Result.Gone);
                if (!disposed && result == DockRestorer.Result.NotResponding) ActivationNotice?.Invoke("应用暂未响应");
            }
            catch (Exception e) { Program.Log(e); }
        }

        /// <summary>Hidden main windows (tray apps) that belong to this app (same app id or executable).</summary>
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

        public Task<string> PinTargetAsync(DockEntry entry)
        {
            if (entry.Pinned) return Task.FromResult(entry.PinPath);
            return Program.RunOnShellThread(() =>
                Program.DesktopItems().Concat(StartMenuLinks())
                    .FirstOrDefault(path => AppIdentity.Matches(ResolvePin(path), entry.Identity))
                ?? entry.Identity.LaunchPath);
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
            ActivationNotice = null;
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
