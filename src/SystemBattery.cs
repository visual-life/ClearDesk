using System;
using System.Runtime.InteropServices;

namespace ClearDesk
{
    internal static class SystemBattery
    {
        internal enum PlatformRole { Unknown = 0, Desktop = 1, Mobile = 2, Slate = 8 }

        // Windows SDK SYSTEM_POWER_CAPABILITIES layout. Only the two battery fields are read.
        [StructLayout(LayoutKind.Explicit, Size = 76)]
        struct Capabilities
        {
            [FieldOffset(30)] public byte SystemBatteriesPresent;
            [FieldOffset(31)] public byte BatteriesAreShortTerm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PowerStatus
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("powrprof.dll")] static extern PlatformRole PowerDeterminePlatformRoleEx(uint version);
        [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)]
        static extern bool GetPwrCapabilities(out Capabilities capabilities);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);

        internal static bool IsPortable(PlatformRole role, bool systemBattery, bool shortTerm)
        {
            return role == PlatformRole.Mobile || role == PlatformRole.Slate || (systemBattery && !shortTerm);
        }

        internal static bool IsPortable()
        {
            var role = PowerDeterminePlatformRoleEx(2);
            Capabilities capabilities;
            if (!GetPwrCapabilities(out capabilities)) return IsPortable(role, false, false);
            return IsPortable(role, capabilities.SystemBatteriesPresent != 0, capabilities.BatteriesAreShortTerm != 0);
        }

        internal static Reading Read()
        {
            PowerStatus status;
            return GetSystemPowerStatus(out status)
                ? new Reading(status.ACLineStatus, status.BatteryFlag, status.BatteryLifePercent)
                : new Reading(255, 255, 255);
        }

        internal struct Reading
        {
            readonly byte line, flags, percent;

            public Reading(byte line, byte flags, byte percent)
            {
                this.line = line;
                this.flags = flags;
                this.percent = percent;
            }

            bool HasBattery { get { return flags == 255 || (flags & 128) == 0; } }
            bool KnownLevel { get { return HasBattery && percent <= 100; } }
            bool Charging { get { return flags != 255 && HasBattery && (flags & 8) != 0; } }
            bool Full { get { return KnownLevel && percent == 100 && line == 1; } }
            public string Label { get { return KnownLevel ? percent + "%" : HasBattery ? "电量未知" : "无电池"; } }

            public string Description
            {
                get
                {
                    if (!HasBattery) return "电池状态：未检测到电池";
                    string state = Full ? "已充满" : Charging ? "充电中" : line == 1 ? "已接通电源" : line == 0 ? "电池供电" : "";
                    return "电池状态：" + (state.Length == 0 ? "" : state + " ") + Label;
                }
            }

            public string Glyph
            {
                get
                {
                    if (!KnownLevel) return "\uE850";
                    int level = (percent + 5) / 10;
                    if (Full) return "\uEA93"; // BatteryCharging10
                    if (!Charging) return level == 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
                    if (level >= 9) return "\uE83E"; // BatteryCharging9
                    return ((char)(0xE85A + level)).ToString();
                }
            }
        }
    }
}
