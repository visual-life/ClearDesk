using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearDesk
{
    // ---- 2.3.4: UI Automation through COM (IUIAutomation). The managed System.Windows.Automation wrapper has no ShowContextMenu; the COM interface has it
    // (IUIAutomationElement3). Only the vtable slots that are used are declared; the rest are placeholders that keep the order.
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class CUIAutomation234 { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIA234
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IEl234 element);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker(); void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition(); void CreateCacheRequest();
        [PreserveSig] int CreateTrueCondition(out IntPtr condition);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEl234
    {
        void SetFocus(); void GetRuntimeId();
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IEl234 element);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IElArr234 elements);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object value);
    }

    // IUIAutomationElement (3..84) + IUIAutomationElement2 (85..90) = 88 methods before ShowContextMenu
    [ComImport, Guid("8471df34-aee0-4a01-a7de-7db9af12c296"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEl3_234
    {
        void M1(); void M2(); void M3(); void M4(); void M5(); void M6(); void M7(); void M8(); void M9(); void M10(); void M11(); void M12(); void M13(); void M14(); void M15(); void M16(); void M17(); void M18(); void M19(); void M20(); void M21(); void M22(); void M23(); void M24(); void M25(); void M26(); void M27(); void M28(); void M29(); void M30(); void M31(); void M32(); void M33(); void M34(); void M35(); void M36(); void M37(); void M38(); void M39(); void M40(); void M41(); void M42(); void M43(); void M44(); void M45(); void M46(); void M47(); void M48(); void M49(); void M50(); void M51(); void M52(); void M53(); void M54(); void M55(); void M56(); void M57(); void M58(); void M59(); void M60(); void M61(); void M62(); void M63(); void M64(); void M65(); void M66(); void M67(); void M68(); void M69(); void M70(); void M71(); void M72(); void M73(); void M74(); void M75(); void M76(); void M77(); void M78(); void M79(); void M80(); void M81(); void M82(); void M83(); void M84(); void M85(); void M86(); void M87(); void M88();
        [PreserveSig] int ShowContextMenu();
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IElArr234 { [PreserveSig] int get_Length(out int length); [PreserveSig] int GetElement(int index, out IEl234 element); }

    /// <summary>2.3.4: operating notification-area icons without the taskbar being visible.
    /// ShowContextMenu (UI Automation) makes the shell hand the icon's right click to its program exactly as if the icon had been right-clicked, but needs no mouse and no visible taskbar.
    /// (2.3.5: the flyout is opened normally and left alone; the 2.3.4 empty-region trick was removed.)</summary>
    internal static class UiaCom
    {
        internal const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
        const int UIA_ClassName = 30003, UIA_Name = 30005, UIA_AutomationId = 30011;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr h, out RECT box);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int a, int b, int c, int d);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        internal static IntPtr TrayWindow() { return FindWindow("Shell_TrayWnd", null); }
        internal static IntPtr OverflowWindowHandle() { return FindWindow(OverflowClass, null); }

        static string Prop(IEl234 e, int id) { object v; try { if (e.GetCurrentPropertyValue(id, out v) != 0 || v == null) return ""; return v.ToString(); } catch { return ""; } }

        internal static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cut = name.IndexOfAny(new[] { '\r', '\n' });
            return (cut >= 0 ? name.Substring(0, cut) : name).Trim();
        }

        /// <summary>Names (as UI Automation reports them) of every notification-area icon below <paramref name="root"/>. COM only; used by tests and diagnostics.</summary>
        internal static List<string> IconNames(IntPtr root)
        {
            var list = new List<string>();
            Walk(root, (e, id, name) => { if (id == "NotifyItemIcon") list.Add(name); return false; });
            return list;
        }

        static bool Walk(IntPtr root, Func<IEl234, string, string, bool> visit)
        {
            IntPtr cond = IntPtr.Zero;
            try
            {
                if (root == IntPtr.Zero) return false;
                var ui = (IUIA234)new CUIAutomation234();
                IEl234 top; if (ui.ElementFromHandle(root, out top) != 0 || top == null) return false;
                if (ui.CreateTrueCondition(out cond) != 0) return false;
                IElArr234 arr; if (top.FindAll(4, cond, out arr) != 0 || arr == null) return false;      // 4 = TreeScope_Descendants
                int n; arr.get_Length(out n);
                for (int i = 0; i < n; i++)
                {
                    IEl234 e; if (arr.GetElement(i, out e) != 0 || e == null) continue;
                    if (visit(e, Prop(e, UIA_AutomationId), Prop(e, UIA_Name))) return true;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            finally { if (cond != IntPtr.Zero) try { Marshal.Release(cond); } catch { } }
            return false;
        }

        /// <summary>Asks the shell to deliver the right click (context menu) of the icon called <paramref name="cleanName"/> to its program. Returns null when the shell accepted it, otherwise why not.</summary>
        internal static string ShowContextMenu(IntPtr root, string cleanName)
        {
            string result = "没有找到图标“" + cleanName + "”";
            try
            {
                try { AllowSetForegroundWindow(-1); } catch { }          // ASFW_ANY: the program that gets the click may bring its menu to the front
                bool found = Walk(root, (e, id, name) =>
                {
                    if (id != "NotifyItemIcon" || Clean(name) != cleanName) return false;
                    try
                    {
                        var e3 = (IEl3_234)e;
                        int hr = e3.ShowContextMenu();
                        result = hr == 0 ? null : "ShowContextMenu 失败（0x" + hr.ToString("X8") + "）";
                    }
                    catch (InvalidCastException) { result = "这个系统的任务栏不支持 ShowContextMenu"; }
                    catch (Exception ex) { result = "ShowContextMenu 出错：" + ex.Message; }
                    return true;
                });
                if (!found && result != null && result.StartsWith("没有找到")) return result;
            }
            catch (Exception ex) { result = "ShowContextMenu 出错：" + ex.Message; }
            return result;
        }

        // ---- the "hidden icons" flyout, kept invisible
        internal static bool RegionIsEmpty(IntPtr h) { RECT r; return h != IntPtr.Zero && GetWindowRgnBox(h, out r) == 1; }       // NULLREGION
        internal static bool HasRegion(IntPtr h) { RECT r; int t = h == IntPtr.Zero ? 0 : GetWindowRgnBox(h, out r); return t == 1 || t == 2 || t == 3; }
        internal static void MakeVisible(IntPtr h) { if (h != IntPtr.Zero && HasRegion(h)) SetWindowRgn(h, IntPtr.Zero, true); }

        // 2.3.5: ClearDesk never changes the shape of the "hidden icons" flyout any more (2.3.4 gave it an empty window region while it read the icons; when ClearDesk was closed or
        // killed in that moment the region stayed, and the ^ button of the taskbar showed nothing). What is left here only puts a leftover empty region right.
        internal static string FlyoutClass { get { var c = Environment.GetEnvironmentVariable("CLEARDESK_FLYOUT_CLASS"); return string.IsNullOrEmpty(c) ? OverflowClass : c; } }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        delegate bool EnumProc(IntPtr h, IntPtr l);

        /// <summary>Gives every "hidden icons" flyout window that has an EMPTY region (left by 2.3.4) its normal shape back. Safe to call at any time; returns how many it fixed.</summary>
        internal static int HealFlyouts()
        {
            int n = 0;
            try
            {
                string cls = FlyoutClass;
                EnumWindows((h, l) =>
                {
                    var sb = new System.Text.StringBuilder(160); GetClassName(h, sb, 160);
                    if (sb.ToString() == cls && RegionIsEmpty(h)) { SetWindowRgn(h, IntPtr.Zero, true); n++; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return n;
        }
        internal static void ResetFlyoutNow() { HealFlyouts(); }

        /// <summary>After a right click on a hidden icon the flyout stays open while the program's menu is up; if it is still open after 9 s it is closed (the way it was opened). Changes nothing else.</summary>
        internal static void CloseWhenIdle(Action closeIt)
        {
            new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000)
                {
                    IntPtr h = OverflowWindowHandle();
                    if (h == IntPtr.Zero || !IsWindowVisible(h)) return;
                    if (sw.ElapsedMilliseconds > 9000) { try { if (closeIt != null) closeIt(); } catch { } return; }
                    Thread.Sleep(120);
                }
            }) { IsBackground = true, Name = "ClearDesk tray flyout close" }.Start();
        }
    }
}
