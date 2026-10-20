using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClearDesk
{
    // Input is observed, never consumed. Hook callback only queues a point; all
    // window checks and WPF work run later on the application's dispatcher.
    internal sealed class OutsideClickWatcher : IDisposable
    {
        readonly Action<Native.POINT> action;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Thread thread;
        Native.MouseProc callback;
        IntPtr hook;
        uint threadId;
        volatile bool disposed;
        public static int ActiveCount;
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        public OutsideClickWatcher(Action<Native.POINT> callbackAction)
        {
            action = callbackAction;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ClearDesk launchpad outside clicks"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(2000) || hook == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException("启动台外部点击监听不可用。");
            }

            ActiveCount++;
        }

        void Run()
        {
            threadId = GetCurrentThreadId();
            callback = Mouse;
            hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero)
                return;
            try
            {
                Application.Run();
            }
            finally
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }

        IntPtr Mouse(int code, IntPtr w, IntPtr l)
        {
            long message = w.ToInt64();
            if (!disposed && code >= 0 && (message == 0x201 || message == 0x204 || message == 0x207))
            {
                var point = ((Native.MOUSE)Marshal.PtrToStructure(l, typeof(Native.MOUSE))).pt;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (disposed || Program.App == null)
                        return;
                    try
                    {
                        Program.App.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!disposed)
                                action(point);
                        }));
                    }
                    catch
                    {
                    }
                });
            }

            return Native.CallNextHookEx(hook, code, w, l);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (hook != IntPtr.Zero)
                ActiveCount = Math.Max(0, ActiveCount - 1);
            if (threadId != 0)
                PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(500);
        }
    }
}
