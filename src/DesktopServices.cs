using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    public class RestoreState
    {
        public long IconHandle;
        public bool IconsVisible, IconsChanged;
        public int TaskbarProcessId;
        public string TaskbarPath;
        public bool TopTaskbarHidden;
    }

    public class DesktopServices : IDisposable
    {
        MouseListener listener;
        Native.EventProc eventProc;
        IntPtr eventHook;
        bool disposed, autoHidden;
        RestoreState restore = new RestoreState();
        string recovery;
        Process guardian;
        TaskbarService taskbar = new TaskbarService();
        bool taskbarActive;
        Animator animator = new Animator();
        public DesktopServices()
        {
            recovery = Path.Combine(Program.Data, "recovery-v11.json");
            eventProc = WinEvent;
        }

        void EnsureRecovery()
        {
            if (guardian != null || SkipGuardian)
                return;
            var h = Native.Icons();
            restore.IconHandle = h.ToInt64();
            restore.IconsVisible = h == IntPtr.Zero || Native.IsWindowVisible(h);
            WriteRecovery();
            guardian = Process.Start(new ProcessStartInfo(Path.Combine(Program.Base, "ClearDesk.exe"), "--guard " + Process.GetCurrentProcess().Id + " \"" + recovery + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        void WriteRecovery()
        {
            Directory.CreateDirectory(Program.Data);
            string tmp = recovery + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(restore));
            if (File.Exists(recovery))
                File.Replace(tmp, recovery, null);
            else
                File.Move(tmp, recovery);
        }

        public void Apply()
        {
            if (Program.Preview)
                return;
            if (Program.S.Icons && listener == null)
                listener = new MouseListener(delegate
                {
                    if (!disposed && Program.S.Icons)
                        ToggleIcons();
                });
            if (!Program.S.Icons && listener != null)
            {
                listener.Dispose();
                listener = null;
                RestoreIcons();
            }

            if (Program.S.Animation && eventHook == IntPtr.Zero)
            {
                eventHook = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, eventProc, 0, 0, 2);
                if (eventHook == IntPtr.Zero)
                {
                    Program.LogText("无法启用窗口事件监听（最小化动画不可用）。");   // 2.2.3: optional feature, never fatal
                    Program.Balloon("最小化动画暂不可用", "无法启用窗口事件监听，ClearDesk 其他功能不受影响。");
                }
            }

            if (!Program.S.Animation && eventHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(eventHook);
                eventHook = IntPtr.Zero;
                animator.Clear();
            }

            if (!Program.S.Taskbar)
                taskbarStopped = false;
            if (Program.S.Taskbar && !taskbarActive && !taskbarStopped)
            {
                taskbarTries = 0;
                StartTaskbar(true);
            }

            TopTaskbar.Sync();

            if (!Program.S.Taskbar && (taskbarActive || taskbar.ProcessId != 0))
            {
                taskbar.Dispose();
                restore.TaskbarProcessId = 0;
                if (guardian != null) { try { WriteRecovery(); } catch (Exception e) { Program.Log(e); } }
                taskbarActive = false;
            }
        }

        // 2.2.3: the transparent taskbar is an optional component. Starting it can fail (killed, missing, locked, replaced, quarantined) but must never stop ClearDesk:
        // repair from the kept good copy, otherwise tell the user once, keep running and try again a few times later.
        internal bool SkipGuardian;    // tests only: do not start the --guard watchdog
        bool taskbarStopped;           // the user ended TranslucentTB themselves: respect it until the option is toggled or ClearDesk restarts
        int taskbarTries;
        DateTime taskbarNext = DateTime.MaxValue;
        string taskbarNotice;
        internal static readonly int[] TaskbarRetrySeconds = { 60, 300, 1800 };
        internal bool TaskbarActive { get { return taskbarActive; } }
        internal bool TaskbarGivenUp { get { return !taskbarActive && Program.S.Taskbar && taskbarNext == DateTime.MaxValue && taskbarTries > 0; } }
        internal TaskbarService TaskbarComponentService { get { return taskbar; } }

        internal bool StartTaskbar(bool first)
        {
            try
            {
                EnsureRecovery();
                if (taskbar.TryStart())
                {
                    restore.TaskbarProcessId = taskbar.ProcessId;
                    restore.TaskbarPath = taskbar.Exe;
                    WriteRecovery();
                    taskbarActive = true; taskbarNext = DateTime.MaxValue; taskbarNotice = null; taskbarTries = 0;
                    if (taskbar.Repaired != null) Program.LogText("透明任务栏组件：" + taskbar.Repaired);
                    return true;
                }
                TaskbarFailed(taskbar.Failure ?? "未知原因", taskbar.Retryable);
            }
            catch (Exception e)
            {
                Program.Log(e);
                TaskbarFailed(e.Message, true);
            }
            return false;
        }

        void TaskbarFailed(string why, bool retry)
        {
            taskbarActive = false;
            Program.LogText("透明任务栏组件未启动：" + why);
            if (retry && taskbarTries < TaskbarRetrySeconds.Length)
                taskbarNext = DateTime.Now.AddSeconds(TaskbarRetrySeconds[taskbarTries]);
            else
                taskbarNext = DateTime.MaxValue;
            taskbarTries++;
            if (taskbarNotice != why)
            {
                taskbarNotice = why;
                Program.Balloon("透明任务栏暂不可用", why + "\nClearDesk 其他功能不受影响，稍后会自动重试；也可在设置里关闭再打开此功能。");
            }
        }

        void TickTaskbar()
        {
            if (!Program.S.Taskbar) return;
            if (taskbarActive && taskbar.Exited)
            {
                // killed (Task Manager, another tool): nothing to restore, and do not fight the user by relaunching it
                taskbarActive = false; taskbarStopped = true; restore.TaskbarProcessId = 0;
                try { WriteRecovery(); } catch { }
                Program.LogText("透明任务栏组件已被结束，ClearDesk 不会自动重新拉起；在设置里关闭再打开即可恢复。");
                return;
            }
            if (!taskbarActive && !taskbarStopped && DateTime.Now >= taskbarNext)
            {
                taskbarNext = DateTime.MaxValue;
                StartTaskbar(false);
            }
        }

        public void MarkTopTaskbar(bool hidden)
        {
            if (Program.Preview || disposed || (!hidden && !restore.TopTaskbarHidden))
                return;
            EnsureRecovery();
            restore.TopTaskbarHidden = hidden;
            WriteRecovery();
        }

        public void ToggleIcons()
        {
            var h = Native.Icons();
            if (h != IntPtr.Zero)
                SetIcons(!Native.IsWindowVisible(h), false);
        }

        public void SetIcons(bool visible, bool automatic)
        {
            if (Program.Preview)
                return;
            var h = Native.Icons();
            if (h == IntPtr.Zero)
                return;
            EnsureRecovery();
            restore.IconsChanged = true;
            restore.IconHandle = h.ToInt64();
            WriteRecovery();
            Native.ShowWindow(h, visible ? 5 : 0);
            autoHidden = !visible && automatic;
        }

        void RestoreIcons()
        {
            if (restore.IconsChanged)
            {
                var h = Native.Icons();
                if (h != IntPtr.Zero)
                    Native.ShowWindow(h, restore.IconsVisible ? 5 : 0);
                restore.IconsChanged = false;
                autoHidden = false;
                WriteRecovery();
            }
        }

        public void Tick()
        {
            if (disposed)
                return;
            try { TickTaskbar(); } catch (Exception e) { Program.Log(e); }
            if (Program.S.Icons && Program.S.AutoHide)
            {
                var li = new Native.LASTINPUT
                {
                    size = (uint)Marshal.SizeOf(typeof(Native.LASTINPUT))
                };
                if (Native.GetLastInputInfo(ref li))
                {
                    uint idle = unchecked((uint)Environment.TickCount - li.time);
                    if (idle > Program.S.IdleSeconds * 1000)
                    {
                        var h = Native.Icons();
                        if (h != IntPtr.Zero && Native.IsWindowVisible(h))
                            SetIcons(false, true);
                    }
                    else if (autoHidden && Program.S.RestoreOnInput)
                        SetIcons(true, false);
                }
            }
        }

        void WinEvent(IntPtr hook, uint evt, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (disposed || h == IntPtr.Zero || !Program.S.Animation)
                return;
            if (evt == 0x17 && !Program.S.RestoreAnimation)
                return;
            Program.App.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!disposed)
                    animator.CaptureAndPlay(h, evt == 0x17);
            }));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (listener != null)
                listener.Dispose();
            if (eventHook != IntPtr.Zero)
                Native.UnhookWinEvent(eventHook);
            animator.Clear();
            RestoreIcons();
            taskbar.Dispose();
            try { TopTaskbar.Stop(); } catch { }
            if (File.Exists(recovery))
                File.Delete(recovery);
            if (guardian != null)
                guardian.Dispose();
        }

        public static void Guard(string[] args)
        {
            if (args.Length != 3)
                return;
            int pid;
            if (!int.TryParse(args[1], out pid))
                return;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    p.WaitForExit();
            }
            catch
            {
            }

            string path = args[2];
            try
            {
                if (!File.Exists(path))
                    return;
                var r = new JavaScriptSerializer().Deserialize<RestoreState>(File.ReadAllText(path));
                if (r.IconsChanged)
                {
                    var h = Native.Icons();
                    if (h != IntPtr.Zero)
                        Native.ShowWindow(h, r.IconsVisible ? 5 : 0);
                }

                TaskbarService.CloseOwned(r.TaskbarProcessId, r.TaskbarPath);
                if (r.TopTaskbarHidden)
                    TopTaskbar.Restore();
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    public class Snapshot
    {
        public BitmapSource Image;
        public Rect Rect;
    }

    public class Animator
    {
        Dictionary<IntPtr, Snapshot> cache = new Dictionary<IntPtr, Snapshot>();
        bool capturing;
        List<Window> overlays = new List<Window>();
        int generation;
        public void Clear()
        {
            generation++;
            cache.Clear();
            foreach (var w in overlays.ToArray())
                w.Close();
            overlays.Clear();
        }

        public async void CaptureAndPlay(IntPtr h, bool reverse)
        {
            if (reverse)
            {
                Play(h, true);
                return;
            }

            if (capturing)
                return;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (h == IntPtr.Zero || pid == (uint)Process.GetCurrentProcess().Id)
                return;
            var c = Native.Class(h);
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd")
                return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r))
                return;
            int width = r.R - r.L, height = r.B - r.T;
            if (width < 100 || height < 80 || width > 4500 || height > 3000)
                return;
            capturing = true;
            int epoch = generation;
            try
            {
                var image = await Task.Run(delegate
                {
                    using (var bitmap = new System.Drawing.Bitmap(width, height))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bitmap))
                        {
                            var dc = g.GetHdc();
                            bool ok;
                            try
                            {
                                ok = Native.PrintWindow(h, dc, 2);
                            }
                            finally
                            {
                                g.ReleaseHdc(dc);
                            }

                            if (!ok)
                                return null;
                        }

                        var hb = bitmap.GetHbitmap();
                        try
                        {
                            var src = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(width, height));
                            src.Freeze();
                            return src;
                        }
                        finally
                        {
                            Native.DeleteObject(hb);
                        }
                    }
                });
                if (image != null && epoch == generation && Program.S.Animation)
                {
                    if (cache.Count > 3)
                        cache.Clear();
                    var scale = PresentationSource.FromVisual(Program.App.Windows.OfType<Window>().FirstOrDefault());
                    double sx = scale == null ? 1 : scale.CompositionTarget.TransformFromDevice.M11;
                    double sy = scale == null ? 1 : scale.CompositionTarget.TransformFromDevice.M22;
                    cache[h] = new Snapshot
                    {
                        Image = image,
                        Rect = new Rect(r.L * sx, r.T * sy, width * sx, height * sy)
                    };
                    Play(h, false);
                }
            }
            catch (Exception e)
            {
                Program.Log(e);
            }
            finally
            {
                capturing = false;
            }
        }

        public void Play(IntPtr h, bool reverse)
        {
            Snapshot s;
            if (!cache.TryGetValue(h, out s) || overlays.Count > 1)
                return;
            var wa = SystemParameters.WorkArea;
            var rect = s.Rect;
            double endX = wa.Left + wa.Width / 2, endY = wa.Bottom - 25;
            double left = Math.Min(rect.Left, endX - 40), top = Math.Min(rect.Top, endY);
            double width = Math.Max(rect.Right, endX + 40) - left, height = Math.Max(rect.Bottom, endY + 40) - top;
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                IsHitTestVisible = false
            };
            var canvas = new Canvas();
            w.Content = canvas;
            const int count = 40;
            var images = new List<Image>();
            for (int i = 0; i < count; i++)
            {
                int y = i * s.Image.PixelHeight / count, y2 = (i + 1) * s.Image.PixelHeight / count;
                var crop = new CroppedBitmap(s.Image, new Int32Rect(0, y, s.Image.PixelWidth, y2 - y));
                var img = new Image
                {
                    Source = crop,
                    Stretch = Stretch.Fill
                };
                canvas.Children.Add(img);
                images.Add(img);
            }

            overlays.Add(w);
            w.Show();
            var wh = new WindowInteropHelper(w).Handle;
            Native.SetLong(wh, -20, new IntPtr(Native.GetLong(wh, -20).ToInt64() | 0x20 | 0x8000000 | 0x80));
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            var watch = Stopwatch.StartNew();
            timer.Tick += delegate
            {
                double q = Math.Min(1, watch.Elapsed.TotalMilliseconds / Program.S.AnimationMs);
                double p = reverse ? 1 - q : q;
                double smooth = p * p * (3 - 2 * p);
                for (int i = 0; i < count; i++)
                {
                    double f = (double)i / count;
                    double taper = Math.Pow(smooth, Math.Max(.3, 1.6 - f));
                    double stripWidth = rect.Width * (1 - taper) + 46 * taper;
                    double center = rect.Left + rect.Width / 2 + (endX - (rect.Left + rect.Width / 2)) * taper;
                    double y = (rect.Top + f * rect.Height) * (1 - smooth) + (endY + f * 8) * smooth;
                    var im = images[i];
                    im.Width = stripWidth;
                    im.Height = Math.Max(.7, rect.Height / count * (1 - smooth) + 8.0 / count * smooth + 1);
                    Canvas.SetLeft(im, center - left - stripWidth / 2);
                    Canvas.SetTop(im, y - top);
                }

                w.Opacity = 1 - .25 * smooth;
                if (q >= 1)
                {
                    timer.Stop();
                    overlays.Remove(w);
                    w.Close();
                }
            };
            w.Closed += delegate
            {
                timer.Stop();
                overlays.Remove(w);
            };
            timer.Start();
        }
    }
}
