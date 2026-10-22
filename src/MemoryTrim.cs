using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClearDesk
{
    // Memory hygiene: release the GC heap (incl. LOH) back to the OS after bursts
    // and trim the working set while idle. Real reductions come from bounded caches
    // and lighter sampling elsewhere; this only returns freed pages promptly.
    internal static class MemoryTrim
    {
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        static DispatcherTimer timer;
        static DispatcherTimer pending;

        public static void Start()
        {
            if (timer != null) return;
            timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(3) };
            timer.Tick += delegate { Trim(); };
            timer.Start();
            Soon(TimeSpan.FromSeconds(12));
        }

        public static void Soon(TimeSpan delay)
        {
            if (Program.App == null) return;
            if (pending != null) pending.Stop();
            pending = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = delay };
            pending.Tick += delegate { pending.Stop(); pending = null; Trim(); };
            pending.Start();
        }

        public static void Trim()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }

    // GPU 3D utilisation via a single PDH wildcard query. Replaces
    // PerformanceCounterCategory.ReadCategory("GPU Engine"), which copied every
    // counter of every engine instance (MBs per sample, LOH churn) every 6 seconds.
    internal sealed class GpuUsage : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        IntPtr query, counter;
        IntPtr buffer; uint capacity;
        bool primed, failed;

        public double Sample()
        {
            lock (this) return SampleCore();
        }

        double SampleCore()
        {
            if (failed) return -1;
            try
            {
                if (query == IntPtr.Zero)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 || PdhAddEnglishCounter(query, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                    { failed = true; Release(); return -1; }
                }
                if (PdhCollectQueryData(query) != 0) return -1;
                if (!primed) { primed = true; return -1; }
                uint size = capacity, count;
                int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
                if (r == PDH_MORE_DATA)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    capacity = size + 1024; buffer = Marshal.AllocHGlobal((int)capacity); size = capacity;
                    r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
                }
                if (r != 0) return -1;
                // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR name; { DWORD status; double value } } = 24 bytes on x64
                double sum = 0; int stride = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * stride;
                    if (Marshal.ReadInt32(item, IntPtr.Size) != 0) continue;
                    sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                }
                return Math.Max(0, Math.Min(100, sum));
            }
            catch { return -1; }
        }

        public void Dispose()
        {
            lock (this) { failed = true; Release(); }
        }

        void Release()
        {
            if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; capacity = 0; }
        }
    }
}