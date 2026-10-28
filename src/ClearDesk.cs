using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClearDesk
{
    public class BoxData
    {
        public string Id = Guid.NewGuid().ToString("N"), Title = "桌面收纳";
        public double X = 20, Y = 20, Width = 520, Height = 360;
        public bool MaterialSelected = false;
        public bool Locked = false, Collapsed = false, List = false, Desktop = false, Enabled = true;
        public string Material = "liquid";
        public double Scale = 1;
        public double CustomW = 0, CustomH = 0;
        // 2.2.4: the place in device pixels (and the window DPI it was measured at). Device pixels do not depend on how each monitor scales DIPs, so a widget on a second
        // monitor with a different scale can be put back exactly; only used when the DPI is unchanged.
        public int PxX = int.MinValue, PxY = int.MinValue, PxDpi = 0;
        // 2.2.2: look options shared by every widget
        public bool Frameless = false, NoTextShadow = false;
        public string BgImage = "", BgFit = "cover";
        public double BgOpacity = 1, BgDim = 0.25;
        public List<NoteImage> Images = new List<NoteImage>();
        public string Text = "", NoteColor = "amber";
        public List<string> Items = new List<string>();
        public List<string> Excluded = new List<string>();
    }

    public class NoteImage
    {
        public string File = "";
        public double Scale = 1;
    }

    public class Settings
    {
        public bool Icons = true, AutoHide = false, RestoreOnInput = true, Taskbar = false, TaskbarFade = false, Animation = false, RestoreAnimation = true, Dock = true, Clock = true, Performance = true, Calendar = false, Weather = false, Almanac = false, Music = false, WeatherOnline = false, HideAtStart = false;
        public int ConfigVersion = 1;
        public bool GpuRendering = false;
        public bool AllowUnverifiedTaskbar = false;   // 2.2.4: off by default; lets a TranslucentTB.exe of the shipped version with a valid signature run although its hash differs
        public bool Magnet = true, DesktopMenu = true, ModernMenu = true;
        public bool DockAutoHide = false, QuickControls = false, TopTaskbarAutoHide = false, LiveDesktopBlur = false;
        public List<BoxData> Notes = new List<BoxData>();
        public List<string> ToolWidgets = new List<string>();   // 2.2.0: which of the new widgets are on
        public List<string> LaunchpadItems = new List<string>();
        public int IdleSeconds = 20, DockSize = 48, AnimationMs = 400;
        public double DockOffset = 12;
        public string MusicSource = "auto";
        public bool MusicLyrics = false;
        public Dictionary<string,string> MusicLyricFiles = new Dictionary<string,string>();
        public List<BoxData> Boxes = new List<BoxData>();
        public List<string> Pins = new List<string>();
        public Dictionary<string, BoxData> Widgets = new Dictionary<string, BoxData>();
        // 2.1.0 online update
        public bool AutoUpdateCheck = true;
        public string UpdateSource = "", SkipVersion = "", LastUpdateCheck = "", RemindAfter = "", UpdateRedirect = "";
        public List<string> UpdateMirrors = new List<string>();
        // 2.1.1 weather location: Windows location first, then IP; WeatherManual picks a place by hand (county level).
        public bool WeatherSystemLocation = true, WeatherManual = false;
        public string ManualProvince = "", ManualCity = "", ManualDistrict = "", ManualCountry = "CN";
        public double ManualLat = 0, ManualLon = 0;
        internal GeoPlace ManualPlace()
        {
            if (!WeatherManual) return null;
            var p = new GeoPlace { Kind = "manual", Province = ManualProvince ?? "", City = ManualCity ?? "", District = ManualDistrict ?? "", CountryCode = ManualCountry ?? "", Source = "手动选择" };
            if (!(ManualLat == 0 && ManualLon == 0) && Math.Abs(ManualLat) <= 90 && Math.Abs(ManualLon) <= 180) { p.Lat = ManualLat; p.Lon = ManualLon; }
            return p.Display.Length > 0 || p.HasCoords ? p : null;
        }
    }

    public static class Program
    {
        public static string Base = AppDomain.CurrentDomain.BaseDirectory;
        internal static string[] LaunchArgs = new string[0];
        public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk");
        public static Settings S;
        public static Application App;
        public static DesktopServices Services;
        public static bool Preview = false, Quitting = false, Rendering = false;
        static LaunchpadWindow launchpad;
        static Mutex mutex;
        static Forms.NotifyIcon tray;
        static Window settings;
        static List<GlassWindow> windows = new List<GlassWindow>();
        internal static void RefreshDockOffset() { foreach (var d in windows.OfType<DockWindow>()) d.ApplyOffset(); }
        internal static void RefreshDockAutoHide() { foreach (var d in windows.OfType<DockWindow>()) d.RefreshAutoHide(); }
        internal static IEnumerable<GlassWindow> OpenWindows { get { return windows.ToArray(); } }
        static DispatcherTimer refresh;
        public static string Config
        {
            get
            {
                return Path.Combine(Data, "settings.json");
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                Run(args);
            }
            catch (Exception e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Base, "startup-error.txt"), e.ToString());
                }
                catch
                {
                }
                try { Directory.CreateDirectory(Data); File.WriteAllText(Path.Combine(Data, "startup-error.txt"), e.ToString()); } catch { }

                MessageBox.Show(e.Message, "ClearDesk 启动失败");
            }
        }

        static void Run(string[] args)
        {
            if(args.Length>0 && args[0]=="--music-diag") { MusicDiag.Run(args.Length>1?args[1]:"music-diag.txt"); return; }
            if(args.Contains("--client-lyrics-test")) { ClientLyricsTests.Live(); return; }
            if (args.Length > 0 && args[0] == "--dock-fixture")
            {
                DockBehaviorTest.Fixture();
                return;
            }

            if (args.Length > 0 && args[0] == "--guard")
            {
                DesktopServices.Guard(args);
                return;
            }

            if (args.Length > 0 && args[0] == "--apply-update") { UpdateApplier.RunHelper(args); return; }          // 2.1.0: updater helper (runs from a temp copy)
            if (args.Length > 0 && args[0] == "--update-test-server") { UpdateTestServer.RunBlocking(args); return; }
            if (args.Length > 0 && args[0] == "--dock-activate") { DockDiag.Activate(args); return; }       // 2.2.0: run the Dock's restore code on one running app (verification)
            if (args.Length > 0 && args[0] == "--mixer-diag") { MixerDiag.Run(args); return; }               // 2.2.1: list the audio sessions the volume mixer page shows
            if (args.Length > 0 && args[0] == "--taskbar-diag") { TaskbarDiag.Run(args); return; }              // 2.2.4: the transparent-taskbar component: folder, expected vs actual hashes, signature, running copies
            if (args.Length > 0 && args[0] == "--dock-diag") { DockDiag.Run(args); return; }                // 2.2.0: list the windows the Dock sees and which are elevated
            if (args.Length > 0 && args[0] == "--weather-diag") { WeatherDiag.Run(args); return; }          // 2.1.1: diagnose location and weather sources
            LaunchArgs = args;

            Rendering = args.Contains("--lyrics-test") || args.Contains("--weather-test") || args.Contains("--popup-benchmark") || args.Contains("--render") || args.Contains("--benchmark") || args.Contains("--regression-test");
            Preview = args.Contains("--preview") || Rendering || args.Contains("--integration-test");
            if (Preview)
                Data = Path.Combine(Base, "preview-data");
            if (args.Length > 0 && args[0] == "--self-test")
            {
                SelfTest();
                return;
            }

            if (args.Length > 0 && args[0] == "--uninstall")
            {
                try { DesktopMenu.Unregister(); } catch (Exception e) { Log(e); }
                try { new StartupManager().Disable(); } catch (Exception e) { Log(e); }
                return;
            }

            bool created;
            mutex = new Mutex(true, "Local\\ClearDesk." + Environment.UserName + (args.Contains("--regression-test") ? ".Regression" : Preview ? ".Preview" : ""), out created);
            if (!created)
            {
                if (!Preview && InstancePipe.Send(args))
                    return;
                if (args.Contains("--startup")) return; // duplicate login launch: stay silent
                MessageBox.Show("ClearDesk 已在运行，请双击通知区域的图标打开设置。", "ClearDesk");
                return;
            }

            // Run-key launches start in System32; make relative lookups follow the install folder.
            try { Environment.CurrentDirectory = Base; } catch { }
            if (!Preview && args.Contains("--startup")) { try { StartupGate.WaitForShell(); } catch (Exception e) { Log(e); } }
            Directory.CreateDirectory(Data);
            Load();
            if (!Preview)
            {
                try { var startup = new StartupManager(); if (startup.Heal()) LogText(startup.Log); } catch (Exception e) { Log(e); }
            }
            // GPU rendering maps D3D/driver heaps (~150-170 MB private on iGPUs) for a
            // handful of small, mostly static widgets. Default to software rendering.
            if (!S.GpuRendering && Environment.GetEnvironmentVariable("CLEARDESK_GPU") != "1")
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            App = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            App.DispatcherUnhandledException += delegate (object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                Log(e.Exception);
                e.Handled = true;
                MessageBox.Show("操作未完成：" + e.Exception.Message, "ClearDesk");
            };
            // 2.2.4: Windows logoff / shutdown / restart and any other way out: the unsaved changes are written first
            App.SessionEnding += delegate { FlushSave(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { FlushSave(); };
            try { Microsoft.Win32.SystemEvents.SessionEnding += delegate { FlushSave(); }; } catch { }
            App.Exit += delegate
            {
                FlushSave();
                Quitting = true;
                if (Services != null)
                    Services.Dispose();
                if (tray != null)
                    tray.Dispose();
            };
            Resources();
            Services = new DesktopServices();
            SetupTray();
            Rebuild();
            if (!Preview)
            {
                // Crash-safe: if an earlier session (and its guard) died with the top taskbar hidden, bring it back first.
                try { TopTaskbar.Restore(); } catch (Exception e) { Log(e); }
                try { Services.Apply(); } catch (Exception e) { Log(e); LogText("启动时应用桌面服务失败（已忽略，ClearDesk 继续运行）：" + e.Message); }   // 2.2.3: an optional service must never stop ClearDesk from starting
                if (S.HideAtStart)
                    Services.SetIcons(false, false);
            }

            refresh = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            refresh.Tick += delegate
            {
                if (!Preview)
                    Services.Tick();
            };
            refresh.Start();
            MemoryTrim.Start();
            if (!Preview)
            {
                InstancePipe.Listen(HandleRemote);
                try { rightClicks = new RightClickTracker(); } catch (Exception e) { Log(e); }   // 2.2.1: new widgets appear where the desktop was right-clicked
                DesktopMenu.Sync();
                ModernMenu.RepairIfMoved();
            }
            // 2.1.0: online update — report the last update, undo an interrupted one, then check in the background (delayed, ~every 12 h)
            if (!Preview || args.Contains("--update-e2e"))
            {
                try { if (!args.Contains("--update-e2e")) UpdateService.ReportLastResult(); UpdateService.Housekeeping(); if (!Preview) UpdateService.Start(); } catch (Exception e) { Log(e); }
                if (args.Contains("--update-e2e")) UpdateE2E.Start();
            }
            string requested = WidgetCatalog.Requested(args);
            if (requested != null)
                App.Dispatcher.BeginInvoke(new Action(delegate { NewWidget(requested); }), DispatcherPriority.ApplicationIdle);
            if (args.Contains("--render"))
            {
                S.Calendar = true;
                S.Almanac = true;
                S.Weather = true;
                S.Music = true;
                S.WeatherOnline = false;
                Rebuild();
            }

            if (requested == null && !args.Contains("--lyrics-test") && !args.Contains("--weather-test") && !args.Contains("--popup-benchmark") && !args.Contains("--startup") && !args.Contains("--benchmark") && !args.Contains("--integration-test") && !args.Contains("--regression-test") && !args.Contains("--update-e2e"))
                ShowSettings();
            if (args.Contains("--popup-benchmark"))
                PopupBenchmark.Start();
            if (args.Contains("--regression-test"))
                RegressionTest.Start();
            if (args.Contains("--weather-test"))
                FeatureTests.LiveWeather();
            if (args.Contains("--lyrics-test"))
                LyricsTests.Live();
            if (args.Contains("--benchmark"))
                Benchmark.Start();
            if (args.Contains("--integration-test"))
                IntegrationTest.Start();
            if (args.Contains("--render"))
            {
                Launchpad();
                foreach (var dw in windows.OfType<DockWindow>())
                    dw.TestHover(.5);
                var rt = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };
                rt.Tick += delegate
                {
                    rt.Stop();
                    try
                    {
                        string folder = Path.Combine(Base, "preview");
                        Directory.CreateDirectory(folder);
                        foreach (var win in windows.Cast<Window>().Concat(new[] { settings }).Where(w => w != null))
                        {
                            var element = (FrameworkElement)win.Content;
                            element.UpdateLayout();
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(element);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            string fn = win is GlassWindow ? ((GlassWindow)win).ExportName : "settings";
                            using (var stream = File.Create(Path.Combine(folder, fn + ".png")))
                                encoder.Save(stream);
                        }

                        File.WriteAllText(Path.Combine(folder, "render-status.txt"), "Rendered all enabled widgets and settings successfully.");
                    }
                    catch (Exception e)
                    {
                        File.WriteAllText(Path.Combine(Base, "render-error.txt"), e.ToString());
                    }

                    Quit();
                };
                rt.Start();
            }

            App.Run();
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        /// <summary>2.2.4: settings.json exists but could not be read (locked): defaults are used for now and nothing is written until it can be read again.</summary>
        internal static bool ConfigBlocked;
        internal static string LoadNote;

        internal static void Load()
        {
            S = new Settings();
            ConfigBlocked = false; LoadNote = null;
            bool existed = File.Exists(Config);
            var r = ConfigStore.Load(Config);
            if (r.S != null) S = r.S;
            existed = existed || r.Existed;
            if (r.Blocked) ConfigBlocked = true;
            if (r.Note.Length > 0) { LoadNote = r.Note; LogText(r.Note); }
            if (r.Corrupt && r.S == null)
            {
                try { File.Copy(Config, Config + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            }
            if (r.S != null && r.Source != Config && !ConfigBlocked) dirty = true;      // heal settings.json from the backup it was restored from

            if (S == null)
                S = new Settings();
            if (S.Boxes == null)
                S.Boxes = new List<BoxData>();
            if (S.Pins == null)
                S.Pins = new List<string>();
            if (S.ToolWidgets == null)
                S.ToolWidgets = new List<string>();
            if (S.Notes == null)
                S.Notes = new List<BoxData>();
            if (S.Widgets == null)
                S.Widgets = new Dictionary<string, BoxData>();
            if (S.ConfigVersion < 2)
            {
                S.ConfigVersion = 2;
                S.Animation = false;
                S.TaskbarFade = false;
            }

            if (S.ConfigVersion < 3)
                S.ConfigVersion = 3;
            if (S.ConfigVersion < 4)
            {
                foreach (var box in S.Boxes)
                {
                    box.Collapsed = false;
                    box.Height = Math.Max(170, box.Height);
                }

                S.ConfigVersion = 4;
            }

            if (S.ConfigVersion < 5)
            {
                // 2.0.1: Launchpad gains materials; default to live blur so windows behind stay unreadable.
                var lp = Widget("launchpad", 0, 0, 880, 520);
                lp.Material = "frosted";
                lp.MaterialSelected = true;
                S.ConfigVersion = 5;
            }

            if (S.LaunchpadItems == null)
                S.LaunchpadItems = new List<string>();
            if (S.UpdateMirrors == null) S.UpdateMirrors = new List<string>();
            if (S.UpdateSource == null) S.UpdateSource = "";
            if (S.SkipVersion == null) S.SkipVersion = "";
            if (S.ManualProvince == null) S.ManualProvince = "";
            if (S.ManualCity == null) S.ManualCity = "";
            if (S.ManualDistrict == null) S.ManualDistrict = "";
            if (S.ManualCountry == null) S.ManualCountry = "CN";
            if (S.LastUpdateCheck == null) S.LastUpdateCheck = "";
            if (S.RemindAfter == null) S.RemindAfter = "";
            if (S.UpdateRedirect == null) S.UpdateRedirect = "";
            if (S.Boxes.Count == 0)
                S.Boxes.Add(new BoxData { Desktop = true });
            S.IdleSeconds = Math.Max(3, Math.Min(600, S.IdleSeconds));
            S.DockSize = Math.Max(32, Math.Min(72, S.DockSize));
            S.AnimationMs = Math.Max(150, Math.Min(900, S.AnimationMs));
            if (S.ConfigVersion < 6)
            {
                // 2.2.4: widget keys are case-insensitive and lower-case; numbers JSON cannot carry are repaired
                S.ConfigVersion = 6;
            }
            ConfigStore.Migrate(S);
            if (ConfigStore.Sanitize(S)) { LogText("设置里有无效的位置/大小数值，已修复（无法修复的组件回到默认位置）。"); dirty = true; }
            if (!existed && !ConfigBlocked)
            {
                S.Pins = DesktopItems().Where(p => Path.GetExtension(p).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(p).Equals("Lume", StringComparison.OrdinalIgnoreCase)).Take(7).ToList();
                Save();
            }
        }

        static readonly object SaveLock = new object();
        static volatile bool dirty;
        static DispatcherTimer saveTimer, retryTimer;
        internal static int SaveFailures;
        internal static string LastSaveError;

        // Serialized (the update check saves from a worker thread). 2.2.4: written to disk and retried (sharing violations AND access-denied), never throws, and while settings.json
        // is unreadable (ConfigBlocked) it is left alone. A failed save stays "dirty" and is retried in the background and at exit.
        public static void Save()
        {
            lock (SaveLock)
            {
                if (S == null) return;
                if (ConfigBlocked)
                {
                    dirty = true;
                    if (!TryUnblock()) return;
                }
                string error;
                if (ConfigStore.Write(Config, S, out error)) { dirty = false; LastSaveError = null; return; }
                dirty = true; SaveFailures++; LastSaveError = error;
                if (SaveFailures <= 3 || SaveFailures % 20 == 0) LogText("保存设置失败（稍后重试）：" + error);
            }
            ScheduleRetry();
        }

        /// <summary>The unreadable settings.json can be read now: use what is in it (the in-memory defaults are dropped) and show the widgets again.</summary>
        static bool TryUnblock()
        {
            var r = ConfigStore.Load(Config);
            if (r.Blocked) return false;
            ConfigBlocked = false;
            if (r.S != null)
            {
                S = r.S; if (S.Boxes == null) S.Boxes = new List<BoxData>(); if (S.Pins == null) S.Pins = new List<string>(); if (S.ToolWidgets == null) S.ToolWidgets = new List<string>();
                if (S.Notes == null) S.Notes = new List<BoxData>(); if (S.Widgets == null) S.Widgets = new Dictionary<string, BoxData>(); if (S.LaunchpadItems == null) S.LaunchpadItems = new List<string>();
                ConfigStore.Migrate(S); ConfigStore.Sanitize(S);
                LogText("settings.json 又可以读取了，已使用其中保存的设置。");
                try { if (App != null) App.Dispatcher.BeginInvoke(new Action(Rebuild)); } catch { }
                return false;      // nothing to write now: the file on disk is the truth
            }
            return true;
        }

        /// <summary>Position / size changes: written shortly after the last change (a drag end saves at once).</summary>
        internal static void SaveSoon()
        {
            dirty = true;
            try
            {
                if (App == null || App.Dispatcher == null) return;
                App.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (saveTimer == null) { saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; saveTimer.Tick += delegate { saveTimer.Stop(); if (dirty) Save(); }; }
                    saveTimer.Stop(); saveTimer.Start();
                }));
            }
            catch { }
        }

        static void ScheduleRetry()
        {
            try
            {
                if (App == null || App.Dispatcher == null) return;
                App.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (retryTimer == null) { retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; retryTimer.Tick += delegate { retryTimer.Stop(); if (dirty || ConfigBlocked) Save(); }; }
                    if (!retryTimer.IsEnabled) retryTimer.Start();
                }));
            }
            catch { }
        }

        /// <summary>Everything still unsaved goes to disk now (exit, Windows logoff / shutdown, crash handlers).</summary>
        internal static void FlushSave()
        {
            try { if (S != null) Save(); } catch (Exception e) { Log(e); }
        }
        internal static bool Dirty { get { return dirty; } }

        internal static void LogText(string text)
        {
            try { Directory.CreateDirectory(Data); File.AppendAllText(Path.Combine(Data, "errors.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } catch { }
        }

        public static void Log(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Data);
                File.AppendAllText(Path.Combine(Data, "errors.log"), DateTime.Now.ToString("s") + " " + e.GetType().Name + ": " + e.Message + Environment.NewLine);
            }
            catch
            {
            }
        }

        public static IEnumerable<string> DesktopItems()
        {
            var all = new List<string>();
            foreach (var d in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }

            )
                try
                {
                    all.AddRange(Directory.GetFileSystemEntries(d).Where(p => !Path.GetFileName(p).StartsWith("~$") && !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0));
                }
                catch
                {
                }

            return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => Path.GetFileName(p));
        }

        public static string DisplayName(string p)
        {
            string system = SystemItems.NameOf(p); if (system != null) return system;
            string ext = Path.GetExtension(p);
            return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(p) : Path.GetFileName(p);
        }

        public static void OpenItem(string p)
        {
            if (SystemItems.IsSystem(p)) { if (!SystemItems.Open(p)) MessageBox.Show("无法打开：" + SystemItems.NameOf(p), "ClearDesk"); return; }
            try
            {
                if (!p.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && !Directory.Exists(p))
                {
                    MessageBox.Show("原文件不存在或已移动：\n" + p, "ClearDesk");
                    return;
                }

                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true, WorkingDirectory = p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? Base : Directory.Exists(p) ? p : Path.GetDirectoryName(p) });
            }
            catch (Exception e)
            {
                MessageBox.Show("无法打开：" + e.Message, "ClearDesk");
            }
        }

        static System.Drawing.Icon TrayIcon()
        {
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearDesk.AppIcon.ico"))
                    return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            }
            catch
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(Base, "ClearDesk.exe"));
            }
        }

        static void SetupTray()
        {
            tray = new Forms.NotifyIcon
            {
                Icon = TrayIcon(),
                Text = "ClearDesk · 你的清透桌面",
                Visible = true
            };
            var m = new Forms.ContextMenuStrip();
            m.Items.Add("打开设置", null, delegate
            {
                App.Dispatcher.Invoke(new Action(ShowSettings));
            });
            var create = new Forms.ToolStripMenuItem("新建组件");
            foreach (var kind in WidgetCatalog.Kinds)
            {
                string id = kind.Key;
                create.DropDownItems.Add(kind.Value, null, delegate { App.Dispatcher.Invoke(new Action(delegate { NewWidget(id); })); });
            }
            m.Items.Add(create);
            m.Items.Add("显示 / 隐藏桌面图标", null, delegate
            {
                App.Dispatcher.Invoke(new Action(delegate
                {
                    Services.ToggleIcons();
                }));
            });
            m.Items.Add("恢复桌面图标", null, delegate
            {
                App.Dispatcher.Invoke(new Action(delegate
                {
                    Services.SetIcons(true, false);
                }));
            });
            m.Items.Add("修复透明任务栏组件…", null, delegate
            {
                App.Dispatcher.BeginInvoke(new Action(RepairTaskbarComponent));
            });
            m.Items.Add("退出并恢复桌面", null, delegate
            {
                App.Dispatcher.Invoke(new Action(Quit));
            });
            LightMenuRenderer.Style(m);
            tray.ContextMenuStrip = m;
            tray.DoubleClick += delegate
            {
                App.Dispatcher.Invoke(new Action(ShowSettings));
            };
            tray.BalloonTipClicked += delegate { var a = balloonClick; if (a != null) { try { App.Dispatcher.BeginInvoke(a); } catch { } } };
        }

        static Action balloonClick;
        internal static void Balloon(string title, string text) { Balloon(title, text, null); }
        internal static void Balloon(string title, string text, Action onClick)
        {
            try { balloonClick = onClick; if (tray != null) tray.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info); } catch { }
        }

        /// <summary>Opens errors.log in the default text editor (the balloon "点这里打开日志").</summary>
        internal static void OpenLog()
        {
            try { string log = Path.Combine(Data, "errors.log"); if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); }
            catch (Exception e) { Log(e); }
        }

        /// <summary>The tray / settings command "修复透明任务栏组件": repair, then show what was found and done.</summary>
        internal static void RepairTaskbarComponent()
        {
            string text = Services != null ? Services.RepairTaskbar() : "ClearDesk 服务尚未启动。";
            try { MessageBox.Show(text + Environment.NewLine + "日志：" + Path.Combine(Data, "errors.log"), "修复透明任务栏组件", MessageBoxButton.OK, MessageBoxImage.Information); } catch { }
        }

        public static void Quit()
        {
            Quitting = true;
            Save();
            foreach (var w in windows.ToArray())
                w.Close();
            App.Shutdown();
        }

        public static void Rebuild()
        {
            foreach (var w in windows.ToArray())
                w.Close();
            windows.Clear();
            foreach (var b in S.Boxes.Where(b => b.Enabled))
                AddWindow(new OrganizerWindow(b));
            if (S.Clock)
                AddWindow(new ClockWindow());
            if (S.Performance)
                AddWindow(new PerformanceWindow());
            if (S.Dock)
                AddWindow(new DockWindow());
            if (S.Calendar)
                AddWindow(new CalendarWindow(false));
            if (S.Almanac)
                AddWindow(new CalendarWindow(true));
            if (S.Weather)
                AddWindow(new WeatherWindow());
            if (S.Music)
                AddWindow(new MusicWindow());
            if (S.QuickControls)
                AddWindow(new QuickControlWindow());
            foreach (var kind in (S.ToolWidgets ?? new List<string>()).ToArray())
            { var tw = ToolWidgets.Create(kind); if (tw != null) AddWindow(tw); }
            foreach (var n in S.Notes.Where(n => n.Enabled).ToArray())
                AddWindow(new NoteWindow(n));
        }

        static RightClickTracker rightClicks;
        static void HandleRemote(string[] args)
        {
            if (args != null && args.Contains("--startup")) return; // a second autostart launch must not pop the settings window
            string kind = WidgetCatalog.Requested(args);
            if (kind != null) NewWidget(kind, PlaceMode.DesktopClick);
            else ShowSettings();
        }

        internal static IList<Rect> SnapTargets(Window self)
        {
            return windows.Where(w => w != self && !w.WasClosed && w.IsVisible && !(w is LaunchpadWindow) && w.ActualWidth > 0)
                .Select(w => new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight)).ToList();
        }

        // 2.2.1: top-left for a new widget of this size at the last right-click, or null (default placement).
        internal static Point? RequestedPosition(Size size, PlaceMode mode)
        {
            if (mode == PlaceMode.Default) return null;
            try
            {
                int px, py; if (!WidgetPlacement.TryRecent(WidgetPlacement.NowMs(), out px, out py)) return null;
                if (mode == PlaceMode.DesktopClick && !Native.DesktopAt(new Native.POINT { X = px, Y = py })) return null;   // the right-click was not on the desktop
                var src = windows.Where(w => w.IsVisible).Select(w => PresentationSource.FromVisual(w)).FirstOrDefault(x => x != null && x.CompositionTarget != null);
                Matrix m = src != null ? src.CompositionTarget.TransformFromDevice : Matrix.Identity;
                if (src == null) { try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) m = new Matrix(96.0 / g.DpiX, 0, 0, 96.0 / g.DpiY, 0, 0); } catch { } }
                Func<System.Drawing.Rectangle, Rect> dip = r => { var a = m.Transform(new Point(r.Left, r.Top)); var b = m.Transform(new Point(r.Right, r.Bottom)); return new Rect(a, b); };
                var screens = System.Windows.Forms.Screen.AllScreens;
                var work = screens.Select(sc => dip(sc.WorkingArea)).ToList(); var areas = screens.SelectMany(sc => new[] { dip(sc.WorkingArea), dip(sc.Bounds) }).Distinct().ToList();
                var cursor = m.Transform(new Point(px, py));
                bool snap = S.Magnet && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == 0;
                return WidgetPlacement.Compute(cursor, size, work, SnapTargets(null), areas, snap, false);
            }
            catch (Exception e) { Log(e); return null; }
        }

        public static NoteWindow NewNote(PlaceMode mode = PlaceMode.Default)
        {
            var wa = SystemParameters.WorkArea;
            int n = S.Notes.Count;
            var b = new BoxData
            {
                Title = "便签",
                X = wa.Left + wa.Width / 2 - 140 + (n % 6) * 28,
                Y = wa.Top + wa.Height / 3 - 100 + (n % 6) * 28,
                Width = 280,
                Height = 240,
                NoteColor = NoteWindow.Palette[n % (NoteWindow.Palette.Length - 1)][0]
            };
            var at = RequestedPosition(new Size(b.Width, b.Height), mode); if (at.HasValue) { b.X = at.Value.X; b.Y = at.Value.Y; }
            S.Notes.Add(b);
            Save();
            var w = new NoteWindow(b);
            AddWindow(w);
            w.Activate();
            w.TestBox.Focus();
            return w;
        }

        // Creates (or reveals) a widget by catalog kind. Returns the window shown.
        public static GlassWindow NewWidget(string kind, PlaceMode mode = PlaceMode.Default, bool exact = false)
        {
            string instance = kind;      // 2.2.2: "todo~2" names one instance; a plain kind of a multi-instance widget means "one more" unless exact
            if (exact && ToolWidgets.IsTool(kind)) kind = ToolWidgets.KindOf(kind);
            else if (ToolWidgets.IsTool(kind)) instance = ToolWidgets.NewInstanceId(kind);
            if (!WidgetCatalog.IsKnown(kind)) return null;
            if (kind == "note") return NewNote(mode);
            if (kind == "box") { NewBox(mode); return windows.LastOrDefault(); }
            Func<GlassWindow, bool> match;
            Func<GlassWindow> create;
            switch (kind)
            {
                case "clock": S.Clock = true; match = w => w is ClockWindow; create = () => new ClockWindow(); break;
                case "weather": S.Weather = true; match = w => w is WeatherWindow; create = () => new WeatherWindow(); break;
                case "music": S.Music = true; match = w => w is MusicWindow; create = () => new MusicWindow(); break;
                case "calendar": S.Calendar = true; match = w => w is CalendarWindow && w.ExportName == "calendar"; create = () => new CalendarWindow(false); break;
                case "quick": S.QuickControls = true; match = w => w is QuickControlWindow; create = () => new QuickControlWindow(); break;
                case "almanac": S.Almanac = true; match = w => w is CalendarWindow && w.ExportName == "almanac"; create = () => new CalendarWindow(true); break;
                default:
                    if (ToolWidgets.IsTool(kind)) { ToolWidgets.Set(instance, true); match = w => w.ExportName == instance; create = () => ToolWidgets.Create(instance); break; }
                    S.Performance = true; match = w => w is PerformanceWindow; create = () => new PerformanceWindow(); break;
            }
            BoxData state;
            if (S.Widgets.TryGetValue(instance, out state)) state.Enabled = true;
            Save();
            var existing = windows.FirstOrDefault(w => !w.WasClosed && match(w));
            if (existing != null)
            {
                existing.Show();
                existing.Toast("\uE8A7", "已在桌面上");
                return existing;
            }
            var created = create();
            var at = RequestedPosition(new Size(created.Width, created.Height), mode);
            if (at.HasValue) created.PlaceAt(at.Value);
            AddWindow(created);
            Save();   // 2.2.2: a new widget's own place (the right-click spot!) is written now — it used to wait for the next unrelated save and was lost on a restart
            created.Toast("\uE710", "已添加");
            return created;
        }

        public static void AddWindow(GlassWindow w)
        {
            windows.Add(w);
            w.Closed += delegate
            {
                windows.Remove(w);
            };
            w.Show();
        }

        public static void NewBox() { NewBox(PlaceMode.Default); }
        public static void NewBox(PlaceMode mode)
        {
            var b = new BoxData
            {
                Title = "新的收纳格",
                X = 70 + S.Boxes.Count * 25,
                Y = 100 + S.Boxes.Count * 25,
                Width = 420,
                Height = 300
            };
            var at = RequestedPosition(new Size(b.Width, b.Height), mode); if (at.HasValue) { b.X = at.Value.X; b.Y = at.Value.Y; }
            S.Boxes.Add(b);
            Save();
            AddWindow(new OrganizerWindow(b));
        }

        internal static void SendToOrganizer(string path, BoxData target)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("项目已不存在，请刷新启动台。", path);
            if (target == null)
            {
                target = new BoxData { Title = "新的收纳格", X = 70, Y = 100, Width = 420, Height = 300 };
                S.Boxes.Add(target);
            }
            if (!target.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) target.Items.Add(path);
            target.Excluded.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            target.Enabled = true; target.Collapsed = false; target.Height = Math.Max(170, target.Height);
            Save();
            var existing = windows.OfType<OrganizerWindow>().FirstOrDefault(w => w.ExportName == target.Id && !w.WasClosed);
            if (existing == null) AddWindow(new OrganizerWindow(target));
            else existing.RefreshReferences();
        }

        public static BoxData Widget(string id, double x, double y, double width, double height)
        {
            BoxData b;
            if (!S.Widgets.TryGetValue(id, out b))
            {
                b = new BoxData
                {
                    Id = id,
                    Title = id,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                };
                S.Widgets[id] = b;
            }

            return b;
        }

        public static void RefreshDock()
        {
            foreach (var w in windows.OfType<DockWindow>().ToArray())
                w.Rebuild();
        }

        public static void Launchpad()
        {
            if (launchpad != null && !launchpad.WasClosed)
            {
                launchpad.Dismiss();
                return;
            }

            launchpad = new LaunchpadWindow();
            var w = launchpad;
            w.Closed += delegate
            {
                if (launchpad == w)
                    launchpad = null;
            };
            AddWindow(w);
            w.Activate();
        }

        public static void Resources()
        {
            string x = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='Button'><Setter Property='Foreground' Value='#F4F6FC'/><Setter Property='Background' Value='#263346'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='14,8'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='B' CornerRadius='9' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#46556A'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Opacity' Value='.65'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='ProgressBar'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ProgressBar'><Border Background='{TemplateBinding Background}' CornerRadius='2'><Grid x:Name='PART_Track' ClipToBounds='True'><Border x:Name='PART_Indicator' Background='{TemplateBinding Foreground}' HorizontalAlignment='Left' CornerRadius='2'/></Grid></Border></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='CheckBox'><Setter Property='Foreground' Value='#F2F5FA'/><Setter Property='Margin' Value='0,10'/><Setter Property='FontSize' Value='14'/></Style>
 <Style TargetType='TextBox'><Setter Property='Background' Value='#26313E'/><Setter Property='Foreground' Value='White'/><Setter Property='CaretBrush' Value='White'/><Setter Property='Padding' Value='8'/><Setter Property='BorderBrush' Value='#485568'/></Style>
 <Style TargetType='ContextMenu'><Setter Property='Background' Value='#242832'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderBrush' Value='#4A5361'/><Setter Property='Padding' Value='5'/></Style>
 <Style TargetType='MenuItem'><Setter Property='Foreground' Value='White'/><Setter Property='Padding' Value='12,7'/></Style>
 </ResourceDictionary>";
            App.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(x);
            MenuTheme.Apply(App.Resources);
        }

        public static TextBlock Text(string s, double size = 14, string color = "#F4F6FC")
        {
            return new TextBlock
            {
                Text = s,
                FontSize = size,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }

        public static Button Button(string s, Action action)
        {
            var b = new Button
            {
                Content = s,
                Margin = new Thickness(0, 4, 8, 4)
            };
            b.Click += delegate
            {
                action();
            };
            return b;
        }

        static void Check(Panel p, string title, bool value, Action<bool> apply)
        {
            var c = new CheckBox
            {
                Content = title,
                IsChecked = value
            };
            c.Click += delegate
            {
                apply(c.IsChecked == true);
                Save();
            };
            p.Children.Add(c);
        }

        static Border Card(string title, Panel body)
        {
            var s = new StackPanel();
            s.Children.Add(Text(title, 18));
            s.Children.Add(body);
            return new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromRgb(25, 32, 43)),
                Padding = new Thickness(20),
                Margin = new Thickness(0, 0, 0, 14),
                Child = s
            };
        }

        static void Head(Panel p, string t)
        {
            var b = Text(t, 12, "#9AAFC5");
            b.Margin = new Thickness(0, 8, 0, 8);
            p.Children.Add(b);
        }

        public static bool Startup()
        {
            if (Preview)
                return false;
            try { return new StartupManager().Enabled; }
            catch (Exception e)
            {
                Log(e);
                return false;
            }
        }

        // Returns the message to show; ok=false means the setting could not be applied.
        internal static string SetStartup(bool v, out bool ok)
        {
            ok = true;
            if (Preview) return "预览模式：未更改开机自启动。";
            var manager = new StartupManager();
            if (!v) { manager.Disable(); return "已关闭开机自启动。"; }
            string message; ok = manager.Enable(out message); return message;
        }

        internal static string LastStartupMessage = ""; internal static bool LastStartupOk = true;
        static void SetStartup(bool v) { bool ok; LastStartupMessage = SetStartup(v, out ok); LastStartupOk = ok; }
        internal static string StartupDescription()
        {
            if (Preview) return "预览模式：不读取也不更改开机自启动。";
            try { return new StartupManager().Describe(); } catch (Exception e) { Log(e); return "无法读取开机自启动状态。"; }
        }

        public static void ShowSettings()
        {
            if (settings != null)
            {
                settings.Show();
                if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
                settings.Activate();
                return;
            }
            settings = new SettingsWindow(SetStartup);
            settings.Closed += delegate { settings = null; MemoryTrim.Soon(TimeSpan.FromSeconds(2)); };
            settings.Show();
        }

        static void SelfTest()
        {
            var results = new List<string>();
            try
            {
                var s = new Settings();
                s.Boxes.Add(new BoxData { Title = "测试中文", Items = new List<string> { "C:\\a b\\x.lnk" } });
                string j = new JavaScriptSerializer().Serialize(s);
                var t = new JavaScriptSerializer().Deserialize<Settings>(j);
                if (t.Boxes[0].Title != "测试中文" || t.Boxes[0].Items[0] != s.Boxes[0].Items[0])
                    throw new Exception("Config round-trip");
                results.Add("PASS: Chinese/path config round-trip");
                MusicTests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V2Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V203Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V204Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V210Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V211Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                V220Tests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                var d = new DateTime(2026, 10, 19);
                string lunar = CalendarWindow.Lunar(d);
                if (string.IsNullOrEmpty(lunar))
                    throw new Exception("Lunar");
                results.Add("PASS: lunar conversion " + lunar);
                if (WeatherWindow.Description(95) != "雷雨")
                    throw new Exception("Weather mapping");
                results.Add("PASS: weather code mapping");
                if (new Lunar.Solar(2026, 2, 17).Lunar.DayInChinese != "初一")
                    throw new Exception("Lunar New Year reference");
                results.Add("PASS: known Lunar New Year reference (2026-02-17)");
                Native.MouseProc callback = delegate (int code, IntPtr w, IntPtr l)
                {
                    return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
                };
                IntPtr mh = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
                results.Add(mh == IntPtr.Zero ? "LIMITATION: mouse hook registration denied in test environment" : "PASS: mouse hook registered and released");
                if (mh != IntPtr.Zero)
                    Native.UnhookWindowsHookEx(mh);
                Native.EventProc ec = delegate (IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
                {
                };
                IntPtr eh = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, ec, 0, 0, 2);
                results.Add(eh == IntPtr.Zero ? "LIMITATION: window event hook registration denied" : "PASS: window event hook registered and released");
                if (eh != IntPtr.Zero)
                    Native.UnhookWinEvent(eh);
                GC.KeepAlive(callback);
                GC.KeepAlive(ec);
                if (Native.Icons() == IntPtr.Zero)
                    results.Add("INFO: Explorer desktop not available");
                else
                    results.Add("PASS: Explorer desktop discovery (read-only)");
                results.Add("PASS: " + Native.Taskbars().Count + " taskbars discovered (read-only)");
                results.Add("PASS: " + DesktopItems().Count() + " desktop references enumerated (read-only)");
            }
            catch (Exception e)
            {
                results.Add("FAIL: " + e);
                Environment.ExitCode = 1;
            }

            File.WriteAllLines(Path.Combine(Base, "self-test.txt"), results);
        }
    }
}
