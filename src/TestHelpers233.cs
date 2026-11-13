using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClearDesk
{
    /// <summary>2.3.3: tiny helper programs the tests start (ClearDesk.exe --flash-helper / --tray-helper). They never touch anyone else's windows or tray icons.</summary>
    internal static class TestHelpers233
    {
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
        static bool Flash(IntPtr h, uint flags) { var i = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = h, dwFlags = flags, uCount = 0, dwTimeout = 0 }; return FlashWindowEx(ref i); }

        sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        /// <summary>--flash-helper TITLE CONTROLFILE [MAXSECONDS]. Shows a window far off screen (shown without activating it), writes CONTROLFILE.ready with its handle, then obeys lines in CONTROLFILE: flash / stop / exit.</summary>
        internal static void RunFlash(string[] args)
        {
            string title = args.Length > 1 ? args[1] : "ClearDesk flash helper", ctl = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "cd-flash.ctl");
            int max = 60; if (args.Length > 3) int.TryParse(args[3], out max);
            var form = new QuietForm { Text = title, StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), Size = new Size(260, 120), ShowInTaskbar = true };
            int done = 0; var started = DateTime.UtcNow;
            var timer = new Timer { Interval = 80 };
            timer.Tick += delegate
            {
                if ((DateTime.UtcNow - started).TotalSeconds > max) { form.Close(); return; }
                string[] lines; try { lines = File.Exists(ctl) ? File.ReadAllLines(ctl) : new string[0]; } catch { return; }
                for (; done < lines.Length; done++)
                {
                    string c = lines[done].Trim();
                    if (c == "flash") Flash(form.Handle, 0x3 | 0xC);          // FLASHW_ALL | FLASHW_TIMERNOFG: flashes until the window is brought to the front
                    else if (c == "stop") Flash(form.Handle, 0);              // FLASHW_STOP
                    else if (c == "exit") { form.Close(); return; }
                }
            };
            form.Shown += delegate { try { File.WriteAllText(ctl + ".ready", form.Handle.ToInt64().ToString()); } catch { } timer.Start(); };
            Application.EnableVisualStyles();
            Application.Run(form);
        }

        /// <summary>--tray-helper TIP LOGFILE CONTROLFILE [MAXSECONDS]. A program with one notification-area icon; every click it receives is appended to LOGFILE (left / right / double / menu).</summary>
        internal static void RunTray(string[] args)
        {
            string tip = args.Length > 1 ? args[1] : "ClearDesk tray helper", log = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "cd-tray.log"), ctl = args.Length > 3 ? args[3] : log + ".ctl";
            int max = 90; if (args.Length > 4) int.TryParse(args[4], out max);
            Application.EnableVisualStyles();
            var menu = new ContextMenuStrip(); var exit = new ToolStripMenuItem("退出（测试）");
            menu.Items.Add(exit);
            var icon = new NotifyIcon { Icon = SystemIcons.Information, Text = tip.Length > 60 ? tip.Substring(0, 60) : tip, ContextMenuStrip = menu, Visible = true };
            var ctx = new ApplicationContext();
            Action<string> write = delegate (string s) { try { File.AppendAllText(log, s + Environment.NewLine); } catch { } };
            icon.MouseClick += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) write("left"); };
            icon.MouseDoubleClick += delegate (object o, MouseEventArgs e) { write("double"); };
            icon.MouseUp += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Right) write("right"); };
            menu.Opened += delegate { write("menu-open"); };
            exit.Click += delegate { write("menu-exit"); ctx.ExitThread(); };
            int done = 0; var started = DateTime.UtcNow; DateTime menuAt = DateTime.MaxValue;
            menu.Opened += delegate { menuAt = DateTime.UtcNow; };
            var timer = new Timer { Interval = 80 };
            timer.Tick += delegate
            {
                if ((DateTime.UtcNow - started).TotalSeconds > max) { ctx.ExitThread(); return; }
                if (menu.Visible && (DateTime.UtcNow - menuAt).TotalSeconds > 2.5) menu.Close();      // a test must never leave a menu open
                string[] lines; try { lines = File.Exists(ctl) ? File.ReadAllLines(ctl) : new string[0]; } catch { return; }
                for (; done < lines.Length; done++) if (lines[done].Trim() == "exit") { ctx.ExitThread(); return; }
            };
            timer.Start();
            try { File.WriteAllText(ctl + ".ready", "1"); } catch { }
            Application.Run(ctx);
            icon.Visible = false; icon.Dispose();
        }
    }
}
