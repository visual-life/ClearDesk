using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDesk
{
    /// <summary>2.2.5 "设备电量": one device and what could really be read from it. Percent == null means nothing was read: never estimated or made up.</summary>
    internal sealed class BatteryReading
    {
        public string Key = "", Name = "", Kind = "", Source = "";
        public int? Percent;          // only a value the device itself reported
        public bool? Charging;
        public string Level;          // device reports only a coarse level (e.g. 罗技 "满 / 较好 / 低 / 极低") or a voltage, not a percentage
        public string Note;           // why nothing could be read
        public bool Readable { get { return Percent.HasValue || Level != null; } }
        public override string ToString() { return Name + " [" + Kind + "] " + (Percent.HasValue ? Percent + "%" : Level ?? "不支持读取电量") + (Charging == true ? " 充电中" : "") + " (" + Source + (Note != null ? "；" + Note : "") + ")"; }
    }

    /// <summary>
    /// Reads battery levels of wireless devices without any vendor software:
    ///  1. Bluetooth (Classic + LE): the battery value Windows itself shows in Settings (DEVPKEY_Bluetooth_Battery on the device node).
    ///  2. Standard HID battery reports: a HID collection with Generic Device Controls "Battery Strength" (0x06/0x20) or Battery System "RemainingCapacity" (0x85/0x66),
    ///     read with HidD_GetInputReport / HidD_GetFeature. This is how many 2.4 GHz receivers and headsets report.
    ///  3. Logitech HID++ 2.0 (receiver VID 046D: Unifying, Lightspeed, Bolt; also directly connected): feature 0x1004 Unified Battery, 0x1000 Battery Status, 0x1001
    ///     Battery Voltage. Only read-type requests are sent (feature lookup, get status, get name); nothing is configured or written to the device.
    /// Everything that cannot be read is listed as "不支持读取电量".
    /// </summary>
    internal static class DeviceBattery
    {
        internal static Func<List<BatteryReading>> Provider;     // test hook
        internal static List<string> Trace = new List<string>(); // what was tried (for the diagnostics / the test report)

        internal static List<BatteryReading> Scan()
        {
            if (Provider != null) return Provider();
            var list = new List<BatteryReading>();
            lock (Trace) Trace.Clear();
            try { list.AddRange(Bluetooth()); } catch (Exception e) { Log("蓝牙枚举失败：" + e.Message); }
            try { list.AddRange(Hid()); } catch (Exception e) { Log("HID 枚举失败：" + e.Message); }
            // the same device can show up twice (BT battery + its HID collection): keep the readable entry
            var outList = new List<BatteryReading>();
            foreach (var g in list.GroupBy(r => r.Key))
                outList.Add(g.OrderByDescending(r => r.Readable).First());
            return outList.OrderByDescending(r => r.Readable).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void Log(string s) { lock (Trace) Trace.Add(s); }

        // ================================================================ SetupAPI
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr cls, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, ref SP_DEVINFO_DATA info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out int type, byte[] buffer, int size, out int required, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out int parent, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID(int devInst, StringBuilder buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_Property(int devInst, ref DEVPROPKEY key, out int type, byte[] buffer, ref int size, int flags);
        const int DIGCF_PRESENT = 2, DIGCF_ALLCLASSES = 4, DIGCF_DEVICEINTERFACE = 0x10;

        static DEVPROPKEY BtBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static DEVPROPKEY FriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static DEVPROPKEY DeviceDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static DEVPROPKEY BusReportedDesc = new DEVPROPKEY("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
        static DEVPROPKEY IsConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);        // DEVPKEY_Device_IsConnected? (aep) — best effort only
        static DEVPROPKEY ContainerId = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key, out int type)
        {
            int req; type = 0;
            SetupDiGetDeviceProperty(set, ref info, ref key, out type, null, 0, out req, 0);
            if (req <= 0) return null;
            var b = new byte[req];
            return SetupDiGetDeviceProperty(set, ref info, ref key, out type, b, b.Length, out req, 0) ? b : null;
        }
        static string PropString(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            int t; var b = Prop(set, ref info, key, out t);
            return b == null || t != 0x12 ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
        }
        static string NodeString(int devInst, DEVPROPKEY key)
        {
            int t, size = 0; CM_Get_DevNode_Property(devInst, ref key, out t, null, ref size, 0);
            if (size <= 0) return null; var b = new byte[size];
            return CM_Get_DevNode_Property(devInst, ref key, out t, b, ref size, 0) == 0 && t == 0x12 ? Encoding.Unicode.GetString(b, 0, size).TrimEnd('\0') : null;
        }
        static string NodeId(int devInst) { var sb = new StringBuilder(512); return CM_Get_Device_ID(devInst, sb, sb.Capacity, 0) == 0 ? sb.ToString() : ""; }

        // ================================================================ 1. Bluetooth
        static IEnumerable<BatteryReading> Bluetooth()
        {
            var result = new List<BatteryReading>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var seen = new HashSet<string>();
                for (int i = 0; ; i++)
                {
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                    var id = new StringBuilder(512); int req;
                    if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out req)) continue;
                    string iid = id.ToString();
                    bool btDev = iid.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || iid.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase);
                    int t; var bat = Prop(set, ref info, BtBattery, out t);
                    if (!btDev && bat == null) continue;
                    string name = PropString(set, ref info, FriendlyName) ?? PropString(set, ref info, DeviceDesc) ?? iid;
                    string addr = BtAddress(iid);
                    string key = "bt:" + (addr ?? iid);
                    if (!seen.Add(key)) { if (bat != null) { var prev = result.First(r => r.Key == key); if (!prev.Percent.HasValue) prev.Percent = bat[0]; } continue; }
                    var r = new BatteryReading { Key = key, Name = name, Kind = "蓝牙", Source = "Windows 蓝牙电量（与“设置 → 蓝牙和设备”相同）" };
                    if (bat != null && bat.Length >= 1 && bat[0] <= 100) r.Percent = bat[0];
                    else r.Note = "这台设备没有通过蓝牙报告电量（或当前未连接）";
                    Log("蓝牙 " + name + " " + iid + " -> " + (r.Percent.HasValue ? r.Percent + "%" : "无电量属性"));
                    result.Add(r);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        static string BtAddress(string iid)
        {
            var m = System.Text.RegularExpressions.Regex.Match(iid, @"DEV_([0-9A-F]{12})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        // ================================================================ HID
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder s, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps,
                NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [StructLayout(LayoutKind.Explicit, Size = 72)] struct HIDP_VALUE_CAPS
        {
            [FieldOffset(0)] public ushort UsagePage; [FieldOffset(2)] public byte ReportID; [FieldOffset(3)] public byte IsAlias;
            [FieldOffset(16)] public byte IsRange;
            [FieldOffset(28)] public int LogicalMin; [FieldOffset(32)] public int LogicalMax;
            [FieldOffset(56)] public ushort UsageMin; // NotRange.Usage when IsRange == 0
        }
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out int value, IntPtr pp, byte[] report, int len);
        const int HIDP_STATUS_SUCCESS = 0x00110000;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        sealed class HidIf { public string Path; public int DevInst; public ushort Vid, Pid, Page, Usage; public int InLen, OutLen, FeatLen; public string Product; public string Parent; }

        static List<HidIf> HidInterfaces()
        {
            var list = new List<HidIf>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, ref info);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, ref info)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4);
                        var h = new HidIf { Path = path, DevInst = info.DevInst };
                        int parent; if (CM_Get_Parent(out parent, info.DevInst, 0) == 0)
                        {
                            h.Parent = NodeId(parent);
                            int gp; if (h.Parent.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0 && CM_Get_Parent(out gp, parent, 0) == 0) h.Parent = NodeId(gp);   // one USB composite device = one entry
                        }
                        using (var f = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))     // access 0: attributes / caps only, works for mice and keyboards too
                        {
                            if (f.IsInvalid) continue;
                            var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                            if (HidD_GetAttributes(f, ref a)) { h.Vid = a.VendorID; h.Pid = a.ProductID; }
                            IntPtr pp;
                            if (HidD_GetPreparsedData(f, out pp))
                            {
                                try { HIDP_CAPS c; if (HidP_GetCaps(pp, out c) == HIDP_STATUS_SUCCESS) { h.Page = c.UsagePage; h.Usage = c.Usage; h.InLen = c.InputReportByteLength; h.OutLen = c.OutputReportByteLength; h.FeatLen = c.FeatureReportByteLength; } }
                                finally { HidD_FreePreparsedData(pp); }
                            }
                            var sb = new StringBuilder(256); if (HidD_GetProductString(f, sb, 512)) h.Product = sb.ToString().Trim();
                        }
                        if (string.IsNullOrEmpty(h.Product)) h.Product = NodeString(info.DevInst, BusReportedDesc);
                        list.Add(h);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static string Physical(HidIf h)
        {
            // the USB / BT device a HID collection belongs to: its parent node without the &COL / &MI_ suffixes
            string p = h.Parent ?? h.Path;
            return p.ToUpperInvariant();
        }

        static IEnumerable<BatteryReading> Hid()
        {
            var result = new List<BatteryReading>();
            var all = HidInterfaces();
            Log("HID 接口 " + all.Count + " 个");
            var byDevice = all.GroupBy(Physical).ToList();
            foreach (var dev in byDevice)
            {
                var ifs = dev.ToList();
                var first = ifs[0];
                bool bt = dev.Key.StartsWith("BTHENUM", StringComparison.Ordinal) || dev.Key.StartsWith("BTHLE", StringComparison.Ordinal) || dev.Key.Contains("{00001124") || dev.Key.Contains("{00001812");
                bool pointing = ifs.Any(x => x.Page == 1 && (x.Usage == 2 || x.Usage == 6));        // mouse / keyboard
                bool headset = ifs.Any(x => x.Page == 0x0C || x.Page == 0x0B);                        // consumer / telephony (headsets)
                string name = ifs.Select(x => x.Product).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? ("HID 设备 " + first.Vid.ToString("X4") + ":" + first.Pid.ToString("X4"));
                string key = "hid:" + first.Vid.ToString("X4") + ":" + first.Pid.ToString("X4") + ":" + dev.Key;

                // Logitech HID++ (receiver or direct): each paired device is its own entry
                var hpp = ifs.FirstOrDefault(x => first.Vid == 0x046D && (x.Page & 0xFF00) == 0xFF00 && x.Usage == 2 && x.OutLen >= 20);
                if (hpp != null)
                {
                    var found = Logitech.Read(hpp.Path, first.Pid, name);
                    if (found.Count > 0) { result.AddRange(found); continue; }
                    result.Add(new BatteryReading { Key = key, Name = name, Kind = "罗技接收器", Source = "罗技 HID++ 2.0 " + first.Vid.ToString("X4") + ":" + first.Pid.ToString("X4"), Note = "接收器上没有设备应答（设备可能关机、休眠或未配对）" });
                    continue;
                }

                // standard HID battery reports
                BatteryReading std = null;
                foreach (var x in ifs) { std = StandardHid(x, name, key); if (std != null) break; }
                if (std != null) { result.Add(std); continue; }

                if (bt) continue;                     // Bluetooth devices are already listed from the Bluetooth side
                if (!(pointing || headset) || first.Vid == 0 || first.Pid == 0) continue;
                if (dev.Key.StartsWith("ACPI", StringComparison.Ordinal) || dev.Key.Contains("I2C")) continue;     // built-in keyboard / touchpad of a laptop
                result.Add(new BatteryReading { Key = key, Name = name, Kind = (name.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("2.4G") || name.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0 ? "2.4G 接收器" : "USB 设备"), Source = "HID " + first.Vid.ToString("X4") + ":" + first.Pid.ToString("X4"), Note = "设备没有标准 HID 电量报告，也不是可识别的罗技 HID++ 设备（可能是有线设备，或厂商私有协议）" });
                Log("HID " + name + " " + first.Vid.ToString("X4") + ":" + first.Pid.ToString("X4") + " -> 无电量报告");
            }
            return result;
        }

        static BatteryReading StandardHid(HidIf x, string name, string key)
        {
            using (var f = CreateFile(x.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (f.IsInvalid) return null;
                IntPtr pp; if (!HidD_GetPreparsedData(f, out pp)) return null;
                try
                {
                    HIDP_CAPS c; if (HidP_GetCaps(pp, out c) != HIDP_STATUS_SUCCESS) return null;
                    foreach (int type in new[] { 0, 2 })      // HidP_Input, HidP_Feature
                    {
                        ushort n = type == 0 ? c.NumberInputValueCaps : c.NumberFeatureValueCaps;
                        int len = type == 0 ? c.InputReportByteLength : c.FeatureReportByteLength;
                        if (n == 0 || len == 0) continue;
                        var caps = new HIDP_VALUE_CAPS[n];
                        if (HidP_GetValueCaps(type, caps, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                        foreach (var vc in caps.Take(n))
                        {
                            bool strength = vc.UsagePage == 0x06 && vc.UsageMin == 0x20;      // Generic Device Controls: Battery Strength
                            bool capacity = vc.UsagePage == 0x85 && vc.UsageMin == 0x66;      // Battery System: RemainingCapacity
                            if (!strength && !capacity) continue;
                            var report = new byte[len]; report[0] = vc.ReportID;
                            bool ok = type == 0 ? HidD_GetInputReport(f, report, len) : HidD_GetFeature(f, report, len);
                            if (!ok) { Log("HID " + name + "：电量报告 " + vc.ReportID + " 读取失败 " + Marshal.GetLastWin32Error()); continue; }
                            int v; if (HidP_GetUsageValue(type, vc.UsagePage, 0, vc.UsageMin, out v, pp, report, len) != HIDP_STATUS_SUCCESS) continue;
                            int max = vc.LogicalMax > vc.LogicalMin ? vc.LogicalMax : 100, min = vc.LogicalMin;
                            if (v < min || v > max) continue;
                            int pct = (int)Math.Round((v - min) * 100.0 / (max - min));
                            Log("HID " + name + "：标准电量 " + v + " (" + min + ".." + max + ") = " + pct + "%");
                            return new BatteryReading { Key = key, Name = name, Kind = "HID", Percent = pct, Source = "标准 HID 电量报告 (" + (strength ? "0x06/0x20 Battery Strength" : "0x85/0x66 RemainingCapacity") + ")" };
                        }
                    }
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            return null;
        }

        // ================================================================ Logitech HID++ 2.0
        internal static class Logitech
        {
            const byte SW = 0x0A;     // our software id (low nibble of the function byte)
            internal static readonly int[] ReceiverPids = { 0xC52B, 0xC532, 0xC534, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D };

            internal static List<BatteryReading> Read(string path, ushort pid, string productName)
            {
                var list = new List<BatteryReading>();
                using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))      // overlapped
                {
                    if (h.IsInvalid) { Log("罗技 " + productName + "：打不开 HID++ 接口 " + Marshal.GetLastWin32Error()); return list; }
                    using (var fs = new FileStream(h, FileAccess.ReadWrite, 20, true))
                    {
                        bool receiver = ReceiverPids.Contains(pid) || productName.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0 || productName.IndexOf("接收器", StringComparison.Ordinal) >= 0;
                        var indexes = receiver ? Enumerable.Range(1, 6).Select(i => (byte)i) : new[] { (byte)0xFF };
                        foreach (byte idx in indexes)
                        {
                            try { var r = Device(fs, idx, pid, productName, receiver); if (r != null) list.Add(r); }
                            catch (Exception e) { Log("罗技 设备 " + idx + "：" + e.Message); }
                        }
                    }
                }
                return list;
            }

            static BatteryReading Device(FileStream fs, byte idx, ushort pid, string productName, bool receiver)
            {
                byte[] resp;
                // IRoot.GetFeature(0x0000) also tells whether a device is paired + online at this slot
                int f1004 = Feature(fs, idx, 0x1004, out resp);
                if (f1004 == -2) return null;                            // no device / offline at this index
                int f1000 = f1004 > 0 ? 0 : Feature(fs, idx, 0x1000, out resp);
                int f1001 = (f1004 > 0 || f1000 > 0) ? 0 : Feature(fs, idx, 0x1001, out resp);
                string name = Name(fs, idx) ?? (receiver ? "罗技设备 #" + idx : productName);
                var r = new BatteryReading { Key = "logi:" + pid.ToString("X4") + ":" + idx + ":" + name, Name = name, Kind = receiver ? "罗技接收器" : "罗技", Source = "罗技 HID++ 2.0" };
                if (f1004 > 0 && Call(fs, idx, (byte)f1004, 1, null, out resp))
                {
                    // UnifiedBattery.get_status: [0] state of charge %, [1] level flags (1 critical, 2 low, 4 good, 8 full), [2] charging status
                    int levelBits = resp[1];
                    if (resp[0] > 0 && resp[0] <= 100) r.Percent = resp[0];
                    else r.Level = (levelBits & 8) != 0 ? "满" : (levelBits & 4) != 0 ? "较好" : (levelBits & 2) != 0 ? "低" : (levelBits & 1) != 0 ? "极低" : null;
                    r.Charging = resp[2] == 1 || resp[2] == 2;
                    r.Source += "（0x1004 Unified Battery）";
                }
                else if (f1000 > 0 && Call(fs, idx, (byte)f1000, 0, null, out resp))
                {
                    if (resp[0] > 0 && resp[0] <= 100) r.Percent = resp[0];
                    r.Charging = resp[2] >= 1 && resp[2] <= 4;
                    r.Source += "（0x1000 Battery Status）";
                }
                else if (f1001 > 0 && Call(fs, idx, (byte)f1001, 0, null, out resp))
                {
                    int mv = (resp[0] << 8) | resp[1];
                    if (mv > 2500 && mv < 5000) r.Level = "电压 " + (mv / 1000.0).ToString("0.00") + " V";        // a voltage is shown as a voltage: no made-up percentage
                    r.Charging = (resp[2] & 0x80) != 0;
                    r.Source += "（0x1001 Battery Voltage，设备只报告电压）";
                }
                else r.Note = "设备在线，但没有可读的电量功能";
                Log("罗技 " + idx + " " + r);
                return r;
            }

            /// <summary>Feature index of a HID++ 2.0 feature; 0 = not supported, -2 = no device at this index.</summary>
            static int Feature(FileStream fs, byte idx, int feature, out byte[] resp)
            {
                int err;
                if (!Call(fs, idx, 0x00, 0, new[] { (byte)(feature >> 8), (byte)feature }, out resp, out err)) return err == -2 ? -2 : (err == 0 ? -2 : 0);
                return resp[0];
            }

            static string Name(FileStream fs, byte idx)
            {
                byte[] resp; int fi = Feature(fs, idx, 0x0005, out resp);
                if (fi <= 0 || !Call(fs, idx, (byte)fi, 0, null, out resp)) return null;
                int count = resp[0]; if (count <= 0 || count > 64) return null;
                var sb = new StringBuilder();
                while (sb.Length < count)
                {
                    if (!Call(fs, idx, (byte)fi, 1, new[] { (byte)sb.Length }, out resp)) break;
                    int take = Math.Min(16, count - sb.Length); string part = Encoding.ASCII.GetString(resp, 0, take);
                    sb.Append(part); if (part.Length == 0) break;
                }
                string n = sb.ToString().TrimEnd('\0', ' ');
                return n.Length > 0 ? n : null;
            }

            static bool Call(FileStream fs, byte idx, byte feature, int fn, byte[] args, out byte[] resp) { int e; return Call(fs, idx, feature, fn, args, out resp, out e); }

            /// <summary>Sends one long HID++ request and waits (max 600 ms) for the matching answer. err: -2 no device / offline, other = HID++ error code, 0 = timeout.</summary>
            static bool Call(FileStream fs, byte idx, byte feature, int fn, byte[] args, out byte[] resp, out int err)
            {
                resp = null; err = 0;
                var req = new byte[20]; req[0] = 0x11; req[1] = idx; req[2] = feature; req[3] = (byte)((fn << 4) | SW);
                if (args != null) Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
                fs.Write(req, 0, 20);
                var deadline = DateTime.UtcNow.AddMilliseconds(600);
                while (DateTime.UtcNow < deadline)
                {
                    if (pendingFs != fs) { pending = null; pendingFs = fs; }
                    var ar = pending ?? (pending = fs.BeginRead(pendingBuf, 0, 20, null, null));
                    var left = deadline - DateTime.UtcNow;
                    if (!ar.AsyncWaitHandle.WaitOne(left > TimeSpan.Zero ? left : TimeSpan.Zero)) break;     // still pending: kept for the next call (a FileStream read cannot be cancelled)
                    pending = null;
                    var buf = pendingBuf;
                    int n = fs.EndRead(ar);
                    if (n < 7 || buf[1] != idx) continue;
                    if (buf[0] == 0x10 && buf[2] == 0x8F && buf[3] == feature)        // HID++ 1.0 error: 0x09 unknown device / not connected
                    { err = (buf[5] == 0x09 || buf[5] == 0x08 || buf[5] == 0x04) ? -2 : buf[5]; return false; }
                    if (buf[0] == 0x11 && buf[2] == 0xFF && buf[3] == feature && (buf[4] & 0x0F) == SW) { err = buf[5]; return false; }     // HID++ 2.0 error
                    if (buf[0] == 0x11 && buf[2] == feature && buf[3] == req[3]) { resp = new byte[16]; Array.Copy(buf, 4, resp, 0, 16); return true; }
                    // anything else is a notification of the device: ignore
                }
                throw new TimeoutException("没有应答");
            }
            static IAsyncResult pending; static FileStream pendingFs; static readonly byte[] pendingBuf = new byte[20];
        }
    }
}
