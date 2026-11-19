using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace DockFolder
{
    /// <summary>The menus, in the layout of a macOS Dock stack's right-click menu.</summary>
    internal static class StackMenus
    {
        public static string ViewName(string v)
        {
            return v == "fan" ? "扇形展开" : v == "grid" ? "网格展开" : "自动选择样式";
        }

        /// <summary>排序方式 / 内容显示方式 / 打开 / 管理 — shared by the taskbar button and the open stack.</summary>
        public static void AddGroupSections(List<MacMenuItem> m, Group g, Action<string> setSort, Action<string> setView,
                                            Action openFolder, Action manage)
        {
            m.Add(MacMenuItem.Header("排序方式"));
            m.Add(MacMenuItem.Check("名称", g.Sort == "name", delegate { setSort("name"); }));
            m.Add(MacMenuItem.Check("添加日期", g.Sort == "date", delegate { setSort("date"); }));
            m.Add(MacMenuItem.Check("种类", g.Sort == "kind", delegate { setSort("kind"); }));
            m.Add(MacMenuItem.Check("手动排列", g.Sort == "manual", delegate { setSort("manual"); }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Header("内容显示方式"));
            m.Add(MacMenuItem.Check("扇形", g.View == "fan", delegate { setView("fan"); }));
            m.Add(MacMenuItem.Check("网格", g.View == "grid", delegate { setView("grid"); }));
            m.Add(MacMenuItem.Check("自动", g.View == "auto", delegate { setView("auto"); }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("在资源管理器中打开", openFolder));
            m.Add(MacMenuItem.Item("管理此文件夹…", manage));
        }
    }

    /// <summary>
    /// Right-clicking a pinned taskbar folder shows the macOS-style menu instead of the Windows jump list.
    /// Hold Shift while right-clicking to get the normal Windows menu (e.g. to unpin).
    /// </summary>
    internal class TaskbarRightClick
    {
        readonly Dispatcher _dispatcher;
        Native.LowLevelProc _proc;
        IntPtr _hook;
        bool _swallowUp;
        volatile Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static int _busy; // a UI Automation query is still running

        class Hit { public string Name; public Rect R = Rect.Empty; }

        public TaskbarRightClick(Dispatcher d) { _dispatcher = d; }

        Dispatcher _hookDispatcher;

        /// <summary>
        /// The hook lives on its own thread with its own message loop, so a busy UI thread can never
        /// make the system mouse lag (or get the hook silently removed by Windows for being slow).
        /// </summary>
        public void Install()
        {
            RefreshNames();
            Thread t = new Thread(delegate ()
            {
                _hookDispatcher = Dispatcher.CurrentDispatcher;
                _proc = Proc;
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Program.Log("taskbar hook failed: " + Marshal.GetLastWin32Error());
                Dispatcher.Run();
            });
            t.IsBackground = true;
            t.Name = "taskbar-hook";
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public void Uninstall()
        {
            Dispatcher d = _hookDispatcher;
            if (d == null) return;
            d.BeginInvoke(new Action(delegate
            {
                if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }));
        }

        /// <summary>Button caption on the taskbar (= shortcut file name) → group.</summary>
        public void RefreshNames()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in Integration.AllShortcutNames()) d[kv.Key] = kv.Value;
            _names = d;
        }

        IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_RBUTTONDOWN && MacMenu.Current == null && _names.Count > 0 && !Native.IsKeyDown(Native.VK_SHIFT))
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        if (IsTaskbarAt(info.pt))
                        {
                            Rect r;
                            string g = GroupAt(info.pt, out r);
                            if (g != null)
                            {
                                _swallowUp = true;
                                _dispatcher.BeginInvoke(new Action(delegate { Host.Current.ShowTaskbarMenu(g, r); }));
                                return new IntPtr(1);
                            }
                        }
                    }
                    else if (msg == Native.WM_RBUTTONUP && _swallowUp)
                    {
                        _swallowUp = false;
                        return new IntPtr(1);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        static bool IsTaskbarAt(Native.POINT pt)
        {
            IntPtr h = Native.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(root != IntPtr.Zero ? root : h, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        /// <summary>Asks UI Automation which button is under the cursor (bounded wait so the mouse never lags).</summary>
        string GroupAt(Native.POINT pt, out Rect rect)
        {
            rect = Rect.Empty;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null; // previous query hung: don't pile up
            Hit hit = new Hit();
            ManualResetEvent done = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Point p = new Point(pt.X, pt.Y);
                    AutomationElement el = AutomationElement.FromPoint(p);
                    for (int i = 0; el != null && i < 6; i++)
                    {
                        AutomationElement.AutomationElementInformation info = el.Current;
                        Rect r = info.BoundingRectangle;
                        bool buttonish = info.ControlType == ControlType.Button || info.ControlType == ControlType.ListItem
                                         || info.ControlType == ControlType.MenuItem;
                        if (buttonish && !r.IsEmpty && r.Width <= 360 && r.Height <= 220 && r.Contains(p))
                        {
                            lock (hit) { hit.Name = info.Name; hit.R = r; }
                            break;
                        }
                        el = TreeWalker.RawViewWalker.GetParent(el);
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    try { done.Set(); } catch { }
                }
            });
            if (done.WaitOne(120)) done.Close(); // on timeout the worker still owns it
            string name;
            lock (hit) { name = hit.Name; rect = hit.R; }
            string g = GroupForButton(_names, name);
            return g != null && GroupStore.Exists(g) ? g : null;
        }

        /// <summary>Taskbar button caption → group, or null. Captions can carry a " - 2 个窗口" style suffix.</summary>
        public static string GroupForButton(Dictionary<string, string> names, string caption)
        {
            if (string.IsNullOrEmpty(caption) || names == null) return null;
            foreach (KeyValuePair<string, string> kv in names)
            {
                if (string.Equals(caption, kv.Key, StringComparison.OrdinalIgnoreCase)
                    || caption.StartsWith(kv.Key + " - ", StringComparison.OrdinalIgnoreCase)
                    || caption.StartsWith(kv.Key + " – ", StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        public bool IsButtonOf(string caption, string group)
        {
            string g = GroupForButton(_names, caption);
            return g != null && string.Equals(g, group, StringComparison.OrdinalIgnoreCase);
        }

        public bool HasNames { get { return _names.Count > 0; } }
    }
}
