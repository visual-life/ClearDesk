using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClearDesk
{
    // The low-level hook never calls WPF, UI Automation, the filesystem, or Explorer.
    // Windows waits for this callback before delivering mouse input, so keep it tiny.
    internal sealed class MouseListener : IDisposable
    {
        Thread thread;
        ApplicationContext context;
        Native.MouseProc callback;
        IntPtr hook;
        uint previous, threadId;
        Native.POINT point;
        int pending;
        volatile bool stopped;
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
        internal bool IsAlive
        {
            get
            {
                return thread != null && thread.IsAlive;
            }
        }

        readonly Action toggle;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        public MouseListener(Action action)
        {
            toggle = action;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ClearDesk mouse input"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(2000) || hook == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException("桌面鼠标监听启动失败。");
            }
        }

        void Run()
        {
            threadId = GetCurrentThreadId();
            context = new ApplicationContext();
            callback = OnMouse;
            hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero)
                return;
            try
            {
                Application.Run(context);
            }
            finally
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                context.Dispose();
            }
        }

        IntPtr OnMouse(int code, IntPtr w, IntPtr l)
        {
            if (!stopped && code >= 0 && w.ToInt64() == 0x201)
            {
                var m = (Native.MOUSE)Marshal.PtrToStructure(l, typeof(Native.MOUSE));
                if (previous != 0 && unchecked(m.time - previous) <= Native.GetDoubleClickTime() && Math.Abs(point.X - m.pt.X) <= 4 && Math.Abs(point.Y - m.pt.Y) <= 4)
                {
                    previous = 0;
                    var p = m.pt;
                    if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try
                            {
                                if (!stopped && Native.EmptyAt(p))
                                    Program.App.Dispatcher.BeginInvoke(new Action(delegate
                                    {
                                        if (!stopped)
                                            toggle();
                                    }));
                            }
                            finally
                            {
                                Interlocked.Exchange(ref pending, 0);
                            }
                        });
                }
                else
                {
                    previous = m.time;
                    point = m.pt;
                }
            }

            return Native.CallNextHookEx(hook, code, w, l);
        }

        public void Dispose()
        {
            stopped = true;
            if (threadId != 0)
                PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(500);
        }
    }
}
