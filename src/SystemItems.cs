using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClearDesk
{
    /// <summary>The Windows "system icons" the 桌面收纳 / 收纳格 can hold (2.2.1): 此电脑, 回收站, 控制面板, 用户文件夹, 网络.
    /// They are stored in BoxData.Items as "shell:::{CLSID}" and opened through explorer.exe, which understands those names everywhere.</summary>
    internal sealed class SystemItem
    {
        public string Path, Name, Clsid, Tip;
    }

    internal static class SystemItems
    {
        internal const string Prefix = "shell:::";
        internal static readonly SystemItem Computer = new SystemItem { Name = "此电脑", Clsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Tip = "打开此电脑" };
        internal static readonly SystemItem RecycleBin = new SystemItem { Name = "回收站", Clsid = "{645FF040-5081-101B-9F08-00AA002F954E}", Tip = "打开回收站" };
        internal static readonly SystemItem ControlPanel = new SystemItem { Name = "控制面板", Clsid = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", Tip = "打开控制面板" };
        internal static readonly SystemItem UserFiles = new SystemItem { Name = "用户文件夹", Clsid = "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", Tip = "打开用户文件夹" };
        internal static readonly SystemItem Network = new SystemItem { Name = "网络", Clsid = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", Tip = "打开网络" };
        static SystemItems() { foreach (var i in All) i.Path = Prefix + i.Clsid; }
        internal static readonly SystemItem[] All = { Computer, RecycleBin, ControlPanel, UserFiles, Network };

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref RBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
        [DllImport("shell32.dll", SetLastError = false)] static extern int SHGetStockIconInfo(int id, uint flags, ref STOCK info);
        [StructLayout(LayoutKind.Sequential)] struct RBINFO { public int size; public long bytes; public long items; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STOCK { public int size; public IntPtr icon; public int index; public int iconIndex; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string path; }

        internal static SystemItem Find(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return All.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        internal static bool IsSystem(string path) { return Find(path) != null; }
        internal static bool IsRecycleBin(string path) { var i = Find(path); return i != null && i == RecycleBin; }
        internal static string NameOf(string path) { var i = Find(path); return i == null ? null : i.Name; }
        /// <summary>"shell:::{CLSID}" → "::{CLSID}", the parsing name SHParseDisplayName understands for every shell version.</summary>
        internal static string ParsingName(string path) { return Find(path) != null ? path.Substring("shell:".Length) : path; }
        /// <summary>The command that opens an item: explorer.exe with the shell name.</summary>
        internal static ProcessStartInfo OpenCommand(string path)
        {
            var i = Find(path); if (i == null) return null;
            return new ProcessStartInfo("explorer.exe", i.Path) { UseShellExecute = true };
        }
        internal static bool Open(string path)
        {
            try { var cmd = OpenCommand(path); if (cmd == null) return false; Process.Start(cmd); return true; }
            catch (Exception e) { Program.Log(e); return false; }
        }

        // ---- recycle bin
        internal static Func<bool> RecycleBinFull = DefaultFull;
        static bool DefaultFull()
        {
            try { var info = new RBINFO { size = Marshal.SizeOf(typeof(RBINFO)) }; return SHQueryRecycleBin(null, ref info) == 0 && info.items > 0; } catch { return false; }
        }
        /// <summary>System image-list index of the empty / full recycle bin icon, for the recycle bin only (others: -1).</summary>
        internal static int StockIconIndex(string path)
        {
            if (!IsRecycleBin(path)) return -1;
            try
            {
                var info = new STOCK { size = Marshal.SizeOf(typeof(STOCK)) };
                // SIID_RECYCLER = 31 (empty), SIID_RECYCLERFULL = 32; SHGSI_SYSICONINDEX = 0x4000
                return SHGetStockIconInfo(RecycleBinFull() ? 32 : 31, 0x4000, ref info) == 0 ? info.index : -1;
            }
            catch { return -1; }
        }
        /// <summary>Asks Explorer to empty the recycle bin (its own confirmation + progress dialogs). False when it could not be started.</summary>
        internal static Func<IntPtr, bool> EmptyAction = hwnd => { try { return SHEmptyRecycleBin(hwnd, null, 0) >= 0; } catch (Exception e) { Program.Log(e); return false; } };
        internal static bool Empty(IntPtr hwnd) { return EmptyAction(hwnd); }

        // ---- icons that are switched on in "桌面图标设置" (HKCU\...\HideDesktopIcons)
        internal static Func<string, int?> DesktopFlag = ReadFlag;
        static int? ReadFlag(string clsid)
        {
            foreach (var key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu" })
                try { using (var k = Registry.CurrentUser.OpenSubKey(key)) { var v = k == null ? null : k.GetValue(clsid); if (v is int) return (int)v; } } catch { }
            return null;
        }
        /// <summary>System icons that are shown on the desktop right now. 0 = shown, 1 = hidden; an unset value follows the Windows default (only the recycle bin is shown).</summary>
        internal static List<string> OnDesktop()
        {
            var list = new List<string>();
            foreach (var i in All)
            {
                int? flag; try { flag = DesktopFlag(i.Clsid); } catch { flag = null; }
                bool shown = flag.HasValue ? flag.Value == 0 : i == RecycleBin;
                if (i == ControlPanel && !flag.HasValue) shown = false;
                if (shown) list.Add(i.Path);
            }
            return list;
        }
    }
}
