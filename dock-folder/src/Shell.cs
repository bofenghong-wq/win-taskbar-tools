using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockFolder
{
    // ---------------------------------------------------------------- COM interop

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class CShellLink { }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(16)] public IntPtr pad;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    internal static class ShellApi
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(
            string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    }

    // ---------------------------------------------------------------- shortcuts

    internal static class Shortcut
    {
        const int STGM_READWRITE = 2;

        public static void Create(string lnkPath, string target, string args, string workDir,
                                  string iconPath, string description, string appUserModelId)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                if (!string.IsNullOrEmpty(appUserModelId)) SetAppId(link, appUserModelId);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        public static void CreateFromIdList(string lnkPath, IntPtr pidl)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetIDList(pidl);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        static void SetAppId(IShellLinkW link, string id)
        {
            IPropertyStore store = (IPropertyStore)link;
            PROPERTYKEY key = new PROPERTYKEY();
            key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
            key.pid = 5;
            PROPVARIANT pv = new PROPVARIANT();
            pv.vt = 31; // VT_LPWSTR
            pv.p = Marshal.StringToCoTaskMemUni(id);
            try
            {
                store.SetValue(ref key, ref pv);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(pv.p); }
        }

        /// <summary>Reads target path and arguments. Returns false when the file can't be read.</summary>
        public static bool Read(string lnkPath, out string target, out string args)
        {
            target = null; args = null;
            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                StringBuilder sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                target = sb.ToString();
                StringBuilder sa = new StringBuilder(2048);
                link.GetArguments(sa, sa.Capacity);
                args = sa.ToString();
                return true;
            }
            catch { return false; }
            finally { if (link != null) Marshal.ReleaseComObject(link); }
        }

        public static void SetIcon(string lnkPath, string iconPath)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, STGM_READWRITE);
                link.SetIconLocation(iconPath, 0);
                ((IPersistFile)link).Save(null, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        /// <summary>
        /// Turns the "Shell IDList Array" clipboard format (used when dragging items such as
        /// Store apps out of shell:AppsFolder) into shortcuts in <paramref name="destDir"/>.
        /// File-system items are returned in <paramref name="fsPaths"/> instead.
        /// </summary>
        public static List<string> FromIdListArray(byte[] data, string destDir, List<string> fsPaths)
        {
            List<string> created = new List<string>();
            GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = h.AddrOfPinnedObject();
                int count = Marshal.ReadInt32(p);
                IntPtr parent = new IntPtr(p.ToInt64() + Marshal.ReadInt32(p, 4));
                for (int i = 0; i < count; i++)
                {
                    IntPtr child = new IntPtr(p.ToInt64() + Marshal.ReadInt32(p, 8 + 4 * i));
                    IntPtr abs = Native.ILCombine(parent, child);
                    if (abs == IntPtr.Zero) continue;
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        if (Native.SHGetPathFromIDListW(abs, sb) && sb.Length > 0)
                        {
                            fsPaths.Add(sb.ToString());
                            continue;
                        }
                        string name = "应用";
                        IntPtr pName;
                        if (Native.SHGetNameFromIDList(abs, 0, out pName) == 0 && pName != IntPtr.Zero)
                        {
                            name = Marshal.PtrToStringUni(pName);
                            Native.CoTaskMemFree(pName);
                        }
                        string lnk = GroupStore.UniquePath(destDir, GroupStore.SafeFileName(name), ".lnk");
                        CreateFromIdList(lnk, abs);
                        created.Add(lnk);
                    }
                    catch (Exception ex) { Program.Log("idlist: " + ex.Message); }
                    finally { Native.ILFree(abs); }
                }
            }
            finally { h.Free(); }
            return created;
        }
    }

    // ---------------------------------------------------------------- icons

    internal static class IconCache
    {
        static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();
        static ImageSource _fallback;

        static string Key(string path)
        {
            long t = 0;
            try
            {
                if (File.Exists(path)) t = File.GetLastWriteTimeUtc(path).Ticks;
                else if (Directory.Exists(path)) t = 1;
            }
            catch { }
            return path + "|" + t;
        }

        public static ImageSource Get(string path)
        {
            string key = Key(path);
            lock (_lock)
            {
                ImageSource img;
                if (_cache.TryGetValue(key, out img)) return img;
            }
            ImageSource loaded = Load(path, 128);
            lock (_lock) { _cache[key] = loaded; }
            return loaded;
        }

        public static void PreloadAsync(IList<string> paths)
        {
            Thread t = new Thread(delegate ()
            {
                foreach (string p in paths)
                {
                    try { Get(p); } catch { }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static ImageSource Load(string path, int size)
        {
            BitmapSource bs = null;
            try { bs = FromShell(path, size, 0x4 | 0x1); } catch { }      // ICONONLY | BIGGERSIZEOK
            if (bs == null) { try { bs = FromShell(path, size, 0); } catch { } }
            if (bs == null)
            {
                try
                {
                    using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                    }
                }
                catch { }
            }
            if (bs == null) return Fallback();
            return bs;
        }

        static BitmapSource FromShell(string path, int size, int flags)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                ShellApi.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                SIZE sz = new SIZE();
                sz.cx = size; sz.cy = size;
                int hr = f.GetImage(sz, flags, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                return FromHBitmap(hbm);
            }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        static BitmapSource FromHBitmap(IntPtr hbm)
        {
            Native.BITMAP bm = new Native.BITMAP();
            Native.GetObject(hbm, Marshal.SizeOf(typeof(Native.BITMAP)), ref bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
            bi.biSize = 40;
            bi.biWidth = w;
            bi.biHeight = -h; // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            byte[] buf = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try { Native.GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }

            bool hasAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) { if (buf[i] != 0) { hasAlpha = true; break; } }
            if (!hasAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;

            BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, w * 4);
            src.Freeze();
            return src;
        }

        static ImageSource Fallback()
        {
            if (_fallback != null) return _fallback;
            DrawingGroup g = new DrawingGroup();
            using (DrawingContext dc = g.Open())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), null, new Rect(4, 4, 56, 56), 12, 12);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), null, new Rect(18, 18, 28, 28), 6, 6);
            }
            g.Freeze();
            DrawingImage di = new DrawingImage(g);
            di.Freeze();
            _fallback = di;
            return di;
        }
    }
}
