using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Edge = ClearDesk.TopTaskbar.Edge;

namespace ClearDesk
{
    /// <summary>2.3.2: 距屏幕底部距离 = 0 puts the Dock's visible bottom on the physical screen bottom (taskbar hidden / elsewhere) or right above the visible bottom taskbar.</summary>
    internal static class V232Tests
    {
        static TopTaskbar.RECT R(int l, int t, int r, int b) { return new TopTaskbar.RECT { L = l, T = t, R = r, B = b }; }

        /// <summary>A 1920 x 1080 (physical) monitor with a taskbar of the given thickness on an edge.</summary>
        internal static DockGeometry Geo(Edge edge, int bar, bool hiddenByClearDesk, bool workStale, double scale)
        {
            var g = new DockGeometry { Monitor = R(0, 0, 1920, 1080), Scale = scale, TrayEdge = edge, HiddenByClearDesk = hiddenByClearDesk };
            var work = R(0, 0, 1920, 1080); var tray = R(0, 0, 0, 0);
            switch (edge)
            {
                case Edge.Bottom: tray = R(0, 1080 - bar, 1920, 1080); work.B = 1080 - bar; break;
                case Edge.Top: tray = R(0, 0, 1920, bar); work.T = bar; break;
                case Edge.Left: tray = R(0, 0, bar, 1080); work.L = bar; break;
                case Edge.Right: tray = R(1920 - bar, 0, 1920, 1080); work.R = 1920 - bar; break;
            }
            if (hiddenByClearDesk && !workStale) work = R(0, 0, 1920, 1080);     // ClearDesk claimed the strip back
            g.Work = work; g.Tray = tray; return g;
        }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.2"), "2.3.2 version string (or later)");
            foreach (double s in new[] { 1.0, 1.25, 1.5 })
            {
                int bar = (int)Math.Round(48 * s); string tag = " @" + (int)(s * 100) + "%";
                var visible = Geo(Edge.Bottom, bar, false, false, s);
                check(DockPlacement.BottomPx(visible) == 1080 - bar, "2.3.2 taskbar visible at the bottom: the Dock rests right above it" + tag);
                var hidden = Geo(Edge.Bottom, bar, true, false, s);
                var stale = Geo(Edge.Bottom, bar, true, true, s);
                check(DockPlacement.BottomPx(hidden) == 1080 && DockPlacement.BottomPx(stale) == 1080, "2.3.2 完全隐藏任务栏: the Dock rests on the physical bottom, even while the work area is still the old one (the reported gap)" + tag);
                check(DockPlacement.BottomPx(Geo(Edge.None, 0, false, false, s)) == 1080, "2.3.2 no taskbar at the bottom / Windows auto-hid it: physical bottom" + tag);
                check(DockPlacement.BottomPx(Geo(Edge.Left, bar, false, false, s)) == 1080 && DockPlacement.BottomPx(Geo(Edge.Right, bar, false, false, s)) == 1080 && DockPlacement.BottomPx(Geo(Edge.Top, bar, false, false, s)) == 1080,
                    "2.3.2 taskbar on another edge: physical bottom" + tag);
                // the visible plate: window bottom is ShadowPad below the plate, so with offset 0 the plate bottom is the bottom row and the window hangs ShadowPad off screen
                foreach (double offset in new[] { 0.0, 12.0 })
                {
                    double h = 120, top = DockPlacement.Top(hidden, h, offset);
                    double plateBottomPx = (top + h - DockPlacement.ShadowPad) * s;
                    check(Math.Abs(plateBottomPx - (1080 - offset * s)) < .01, "2.3.2 offset " + offset + " px: the visible plate ends " + (offset * s).ToString("0.##") + " physical px above the screen bottom" + tag + " (" + plateBottomPx.ToString("0.##") + ")");
                }
                var wd = DockPlacement.WorkDip(Geo(Edge.Left, bar, false, false, s));
                check(Math.Abs(wd.Left - bar / s) < .01 && Math.Abs(wd.Right - 1920 / s) < .01, "2.3.2 visible left taskbar: the Dock is centred in the remaining width" + tag);
                check(Math.Abs(DockPlacement.MonitorBottomDip(hidden) * s - 1080) < .01, "2.3.2 hidden position is below the monitor's own bottom" + tag);
            }
            check(DockAutoHide.InSummonZone(500, 1079, 100, 900, 1080) && DockAutoHide.InSummonZone(500, 1078, 100, 900, 1080) && !DockAutoHide.InSummonZone(500, 1077, 100, 900, 1080) && !DockAutoHide.InSummonZone(50, 1079, 100, 900, 1080),
                "2.3.2 the summon zone is the last two pixel rows of the monitor over the Dock's width");
            check(DockPlacement.Signature(Geo(Edge.Bottom, 48, false, false, 1)) != DockPlacement.Signature(Geo(Edge.Bottom, 48, true, true, 1)) && DockPlacement.Signature(Geo(Edge.Bottom, 48, true, true, 1)) != DockPlacement.Signature(Geo(Edge.Bottom, 48, true, false, 1))
                && DockPlacement.Signature(Geo(Edge.Bottom, 48, false, false, 1)) != DockPlacement.Signature(Geo(Edge.Bottom, 60, false, false, 1.25)), "2.3.2 taskbar hidden / work area / DPI changes change the signature the Dock watches");
        }

        static async Task Settle(DockWindow dock)
        {
            for (int i = 0; i < 60 && dock.TestTransitioning; i++) await Task.Delay(50);
            await Task.Delay(80);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            var dock = Program.OpenWindows.OfType<DockWindow>().FirstOrDefault(d => !d.WasClosed);
            bool created = false;
            if (dock == null) { dock = new DockWindow(); Program.AddWindow(dock); created = true; await Task.Delay(500); }
            var oldProvider = DockPlacement.Provider; double oldOffset = Program.S.DockOffset; bool oldAuto = Program.S.DockAutoHide;
            try
            {
                Program.S.DockAutoHide = false; Program.S.DockOffset = 0;
                foreach (double s in new[] { 1.0, 1.25, 1.5 })
                {
                    string tag = " @" + (int)(s * 100) + "%"; int bar = (int)Math.Round(48 * s);
                    var cases = new[]
                    {
                        new { Name = "taskbar visible at the bottom", G = Geo(Edge.Bottom, bar, false, false, s), Expect = 1080 - bar },
                        new { Name = "完全隐藏任务栏 (work area already claimed)", G = Geo(Edge.Bottom, bar, true, false, s), Expect = 1080 },
                        new { Name = "完全隐藏任务栏 (work area still old)", G = Geo(Edge.Bottom, bar, true, true, s), Expect = 1080 },
                        new { Name = "Windows auto-hide", G = Geo(Edge.None, 0, false, false, s), Expect = 1080 },
                        new { Name = "taskbar on the left", G = Geo(Edge.Left, bar, false, false, s), Expect = 1080 },
                        new { Name = "taskbar on the right", G = Geo(Edge.Right, bar, false, false, s), Expect = 1080 },
                        new { Name = "taskbar on top", G = Geo(Edge.Top, bar, false, false, s), Expect = 1080 },
                    };
                    foreach (var c in cases)
                    {
                        var g = c.G; DockPlacement.Provider = d => g;
                        dock.Rebuild(); await Settle(dock); dock.ApplyOffset(); await Task.Delay(40);
                        check(Math.Abs(dock.TestPlateBottomPx - c.Expect) < 1.0, "2.3.2 Dock, offset 0, " + c.Name + tag + ": plate bottom at " + dock.TestPlateBottomPx.ToString("0.#") + " px, expected " + c.Expect);
                    }
                }
                // offset follows the setting live
                var visible = Geo(Edge.Bottom, 60, false, false, 1.25); DockPlacement.Provider = d => visible;
                Program.S.DockOffset = 20; Program.RefreshDockOffset(); await Settle(dock);
                check(Math.Abs(dock.TestPlateBottomPx - (1080 - 60 - 20 * 1.25)) < 1.0, "2.3.2 offset 20 px: 25 physical px above the taskbar at 125% (" + dock.TestPlateBottomPx.ToString("0.#") + "; Top " + dock.Top + " ShownTop " + dock.ShownTop + " H " + dock.Height + " offset " + Program.S.DockOffset + ")");
                Program.S.DockOffset = 0; Program.RefreshDockOffset(); await Settle(dock);
                // live: the taskbar goes away (完全隐藏任务栏 switched on), the Dock follows without a restart
                int before = dock.GeometryChanges; var hidden = Geo(Edge.Bottom, 60, true, false, 1.25); DockPlacement.Provider = d => hidden;
                await Task.Delay(900); await Settle(dock);
                check(dock.GeometryChanges > before && Math.Abs(dock.TestPlateBottomPx - 1080) < 1.0, "2.3.2 the Dock follows a taskbar that was hidden while it was running (" + dock.TestPlateBottomPx.ToString("0.#") + ")");
                var back = Geo(Edge.Bottom, 60, false, false, 1.25); DockPlacement.Provider = d => back; await Task.Delay(900); await Settle(dock);
                check(Math.Abs(dock.TestPlateBottomPx - 1020) < 1.0, "2.3.2 ... and comes back above the taskbar when it is shown again (" + dock.TestPlateBottomPx.ToString("0.#") + ")");
                // auto-hide slide-out ends below the physical bottom; coming back returns to the resting place
                DockPlacement.Provider = d => hidden; await Task.Delay(1000);
                Program.S.DockAutoHide = true; var ah = dock.TestAutoHide();
                ah.TestApply(true, false); await Task.Delay(400);
                check(ah.Hidden && (dock.Top + 5) * 1.25 >= 1080 + 4, "2.3.2 auto-hide: slid fully below the monitor's bottom (plate top at " + ((dock.Top + dock.ActualHeight - 5 - 0) * 1.25).ToString("0.#") + ")");
                ah.TestApply(true, true); await Task.Delay(400);
                check(!ah.Hidden && Math.Abs(dock.TestPlateBottomPx - 1080) < 1.0, "2.3.2 auto-hide: summoned, the plate is back on the bottom row (" + dock.TestPlateBottomPx.ToString("0.#") + ")");
                ah.TestApply(false, false); await Task.Delay(400);
            }
            finally
            {
                DockPlacement.Provider = oldProvider; Program.S.DockOffset = oldOffset; Program.S.DockAutoHide = oldAuto;
                dock.Rebuild(); dock.ApplyOffset();
                if (created) dock.Close();
            }
        }
    }
}
