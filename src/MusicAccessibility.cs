using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Accessibility;

namespace ClearDesk
{
    // Some CEF versions expose MSAA rather than UIA playback controls.
    // Read only the selected process window and invoke its own accessible buttons.
    internal sealed class MusicAccessibility : IDisposable
    {
        readonly List<object> owned = new List<object>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        int visited;
        internal sealed class Element
        {
            public IAccessible Accessible;
            public int Id;
            public Element Parent;
            public Element[] CachedChildren;
        }
        [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible result);
        [DllImport("oleacc.dll")] static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
        delegate bool ChildCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, ChildCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        IntPtr handle;
        Element Create(IAccessible accessible, int id, Element parent)
        {
            if (accessible == null) return null;
            owned.Add(accessible);
            return new Element { Accessible = accessible, Id = id, Parent = parent };
        }
        Element FromWindow(IntPtr window)
        {
            IAccessible accessible;
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            return AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out accessible) >= 0 ? Create(accessible, 0, null) : null;
        }
        public Element Window(IntPtr window) { handle = window; return FromWindow(window); }
        public Element FindPlayback(Element root)
        {
            var found = Walk(root, 0);
            if (found != null) return found;
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            var windows = new List<IntPtr>();
            EnumChildWindows(handle, (window, _) => { uint pid; GetWindowThreadProcessId(window, out pid); if (pid == owner) windows.Add(window); return windows.Count < 32; }, IntPtr.Zero);
            foreach (var window in windows)
            {
                found = Walk(FromWindow(window), 0);
                if (found != null) return found;
            }
            return null;
        }
        Element Walk(Element node, int depth)
        {
            if (node == null || depth > 12 || ++visited > 1800 || clock.ElapsedMilliseconds > 1100) return null;
            var children = Children(node);
            foreach (var child in children)
            {
                string name = Name(child);
                if ((name == "pause" || name == "play") && IsButton(child) &&
                    children.Any(e => IsButton(e) && Name(e) == "pre") && children.Any(e => IsButton(e) && Name(e) == "next")) return child;
            }
            foreach (var child in children.Reverse())
            {
                if (child.Id != 0 || IsText(child) || IsButton(child)) continue;
                var found = Walk(child, depth + 1);
                if (found != null) return found;
            }
            return null;
        }
        public Element[] Children(Element root)
        {
            if (root == null || root.Id != 0) return new Element[0];
            if (root.CachedChildren != null) return root.CachedChildren;
            int count = Math.Min(250, root.Accessible.accChildCount);
            var raw = new object[count];
            int obtained;
            if (count == 0 || AccessibleChildren(root.Accessible, 0, count, raw, out obtained) < 0) return new Element[0];
            var result = new List<Element>();
            for (int i = 0; i < obtained; i++)
            {
                var accessible = raw[i] as IAccessible;
                if (accessible != null) result.Add(Create(accessible, 0, root));
                else if (raw[i] is int) result.Add(Create(root.Accessible, (int)raw[i], root));
            }
            return root.CachedChildren = result.ToArray();
        }
        public Element Parent(Element element) { return element.Parent; }
        public IEnumerable<string> Labels(Element element, int depth)
        {
            if (element == null || depth < 0) yield break;
            var children = Children(element);
            if (children.Length == 0) { yield return Name(element); yield break; }
            foreach (var child in children)
            {
                if (IsButton(child)) continue;
                foreach (string label in Labels(child, depth - 1)) yield return label;
            }
        }
        public static string Name(Element e) { try { return e.Accessible.get_accName(e.Id) ?? ""; } catch { return ""; } }
        static int Role(Element e) { try { return Convert.ToInt32(e.Accessible.get_accRole(e.Id)); } catch { return 0; } }
        public static bool IsButton(Element e) { return Role(e) == 43; }
        public static bool IsText(Element e) { return Role(e) == 41; }
        public bool CanInvoke(Element e)
        {
            try { return e != null && (Convert.ToInt32(e.Accessible.get_accState(e.Id)) & 1) == 0 && !string.IsNullOrEmpty(e.Accessible.get_accDefaultAction(e.Id)); }
            catch { return false; }
        }
        public bool Invoke(Element e)
        {
            if (!CanInvoke(e)) return false;
            e.Accessible.accDoDefaultAction(e.Id);
            return true;
        }
        public void Dispose()
        {
            var released = new HashSet<object>();
            for (int i = owned.Count - 1; i >= 0; i--)
                if (released.Add(owned[i]) && Marshal.IsComObject(owned[i]))
                    try { Marshal.FinalReleaseComObject(owned[i]); } catch { }
            owned.Clear();
        }
    }
}
