using System.Reflection;
using System.Runtime.InteropServices;

// 2.2.5: full version resource (company / product / copyright / description / version). 2.2.4 and earlier had none, which security products treat as "unknown origin".
[assembly: AssemblyTitle("ClearDesk")]
[assembly: AssemblyDescription("ClearDesk 桌面整理与桌面组件 (Windows desktop organizer and widgets)")]
[assembly: AssemblyCompany("visual-life")]
[assembly: AssemblyProduct("ClearDesk")]
[assembly: AssemblyCopyright("Copyright © 2026 visual-life. MIT License.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("7c1f3e6a-5b2d-4c8e-9a41-2d6f0b8c5e13")]
[assembly: AssemblyVersion(ClearDesk.BuildInfo.Version4)]
[assembly: AssemblyFileVersion(ClearDesk.BuildInfo.Version4)]
[assembly: AssemblyInformationalVersion(ClearDesk.BuildInfo.Version)]

namespace ClearDesk
{
    internal static class BuildInfo
    {
        internal const string Version = SettingsWindow.Version;      // the one place the version is written (scripts\test-update.ps1 rewrites it there)
        internal const string Version4 = Version + ".0";
    }
}
