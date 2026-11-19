using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DockFolder
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
            // slack for colour masks / table GDI may write
            public uint c0, c1, c2, c3;
        }

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint GA_ROOT = 2;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint ABM_GETTASKBARPOS = 5;
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_SHIFT = 0x10;
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hHook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("shell32.dll")] public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);
        [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);
        [DllImport("shell32.dll")]
        public static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

        public static double SystemDpiScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
            ReleaseDC(IntPtr.Zero, dc);
            if (dpi <= 0) dpi = 96;
            return dpi / 96.0;
        }

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
    }
}
