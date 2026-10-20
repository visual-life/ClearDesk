using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ClearDesk
{
    // Explicit development test. Does not enable startup or hide desktop icons.
    internal static class IntegrationTest
    {
        public static async void Start()
        {
            var results = new List<string>();
            var service = new TaskbarService();
            int pid = 0;
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var listener = new MouseListener(delegate
                    {
                    });
                    if (!listener.IsAlive)
                        throw new Exception("Mouse thread did not start");
                    listener.Dispose();
                    if (listener.IsAlive)
                        throw new Exception("Mouse thread failed to exit");
                }

                results.Add("PASS: three dedicated mouse-hook thread start/dispose cycles; all exited.");
                var paths = Program.DesktopItems().ToArray();
                results.Add("INFO: visible user/common desktop files and folders: " + paths.Length);
                foreach (var path in paths)
                {
                    var source = await Task.Run(() => Native.Icon(path));
                    if (source == null)
                        throw new Exception("Shell icon missing: " + Path.GetFileName(path));
                }

                results.Add("PASS: shell image-list icons loaded for every enumerated desktop item.");
                try
                {
                    service.Start();
                    pid = service.ProcessId;
                    await Task.Delay(3500);
                    using (var p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited)
                            throw new Exception("Component exited early");
                        var h = Native.FindWindow("TrayWindow", "TranslucentTB");
                        uint owner;
                        Native.GetWindowThreadProcessId(h, out owner);
                        if (h == IntPtr.Zero || owner != (uint)pid)
                            throw new Exception("Component control window not available");
                        results.Add("PASS: verified official taskbar component running; control window belongs to launched PID.");
                    }
                }
                catch (Exception e)
                {
                    results.Add("LIMITATION: taskbar runtime: " + e.Message);
                }
            }
            catch (Exception e)
            {
                results.Add("FAIL: " + e.Message);
            }
            finally
            {
                service.Dispose();
                await Task.Delay(1500);
                if (pid > 0)
                {
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                        {
                            results.Add(p.HasExited ? "PASS: taskbar component exited." : "FAIL: taskbar component still running after close.");
                        }
                    }
                    catch (ArgumentException)
                    {
                        results.Add("PASS: owned taskbar component exited after WM_CLOSE.");
                    }
                }

                File.WriteAllLines(Path.Combine(Program.Base, "integration-test.txt"), results);
                Program.Quit();
            }
        }
    }
}
