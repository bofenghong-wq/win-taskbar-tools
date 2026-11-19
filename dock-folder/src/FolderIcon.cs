using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockFolder
{
    /// <summary>iOS-style folder icon: a frosted rounded square with a 3×3 grid of the apps inside.</summary>
    internal static class FolderIcon
    {
        public static ImageSource RenderPreview(Group g, int size)
        {
            RenderTargetBitmap bmp = Render(Icons(g), size);
            bmp.Freeze();
            return bmp;
        }

        static List<ImageSource> Icons(Group g)
        {
            List<ImageSource> l = new List<ImageSource>();
            for (int i = 0; i < g.Items.Count && i < 9; i++) l.Add(IconCache.Get(g.Items[i].Path));
            return l;
        }

        static RenderTargetBitmap Render(List<ImageSource> icons, int size)
        {
            DrawingVisual dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (DrawingContext dc = dv.RenderOpen())
            {
                double s = size;
                double inset = Math.Max(0.5, s * 0.035);
                Rect r = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
                double rad = s * 0.23;
                LinearGradientBrush bg = new LinearGradientBrush(
                    Color.FromArgb(246, 252, 252, 254), Color.FromArgb(246, 206, 211, 221), 90);
                Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), Math.Max(1.0, s / 64.0));
                dc.DrawRoundedRectangle(bg, pen, r, rad, rad);

                int grid = size <= 24 ? 2 : 3;
                int count = Math.Min(icons.Count, grid * grid);
                double pad = s * (grid == 3 ? 0.15 : 0.17);
                double gap = s * (grid == 3 ? 0.045 : 0.07);
                double cell = (s - 2 * pad - (grid - 1) * gap) / grid;
                for (int i = 0; i < count; i++)
                {
                    int row = i / grid, col = i % grid;
                    dc.DrawImage(icons[i], new Rect(pad + col * (cell + gap), pad + row * (cell + gap), cell, cell));
                }
                if (count == 0)
                {
                    Pen dash = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), Math.Max(1.0, s / 40.0));
                    double c = s / 2, l = s * 0.16;
                    dc.DrawLine(dash, new Point(c - l, c), new Point(c + l, c));
                    dc.DrawLine(dash, new Point(c, c - l), new Point(c, c + l));
                }
            }
            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        static string Signature(Group g)
        {
            StringBuilder sb = new StringBuilder("v1|");
            for (int i = 0; i < g.Items.Count && i < 9; i++)
            {
                sb.Append(g.Items[i].FileName).Append('|');
                try { sb.Append(File.GetLastWriteTimeUtc(g.Items[i].Path).Ticks); } catch { }
                sb.Append(';');
            }
            return Program.Fnv(sb.ToString()).ToString("x8");
        }

        /// <summary>Writes group-&lt;sig&gt;.ico (a new name whenever contents change, which defeats the shell icon cache).</summary>
        public static string Generate(Group g)
        {
            string path = System.IO.Path.Combine(g.Dir, "group-" + Signature(g) + ".ico");
            if (File.Exists(path)) return path;

            List<ImageSource> icons = Icons(g);
            int[] sizes = { 256, 64, 48, 40, 32, 24, 20, 16 };
            List<byte[]> blobs = new List<byte[]>();
            foreach (int sz in sizes)
            {
                RenderTargetBitmap bmp = Render(icons, sz);
                blobs.Add(sz >= 256 ? Png(bmp) : Dib(bmp, sz));
            }
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    bw.Write(d); bw.Write(d); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)blobs[i].Length); bw.Write((uint)offset);
                    offset += blobs[i].Length;
                }
                foreach (byte[] b in blobs) bw.Write(b);
            }
            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }

            foreach (string old in Directory.GetFiles(g.Dir, "group-*.ico"))
            {
                if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); } catch { }
            }
            return path;
        }

        static byte[] Png(BitmapSource bmp)
        {
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (MemoryStream ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        // classic 32-bit DIB icon entry (straight alpha, bottom-up, plus AND mask)
        static byte[] Dib(BitmapSource bmp, int size)
        {
            int stride = size * 4;
            byte[] px = new byte[stride * size];
            bmp.CopyPixels(px, stride, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a > 0 && a < 255)
                {
                    px[i] = (byte)Math.Min(255, px[i] * 255 / a);
                    px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                    px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
                }
            }
            int maskStride = ((size + 31) / 32) * 4;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40); bw.Write(size); bw.Write(size * 2);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(0); bw.Write(stride * size + maskStride * size);
                bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                for (int y = size - 1; y >= 0; y--) bw.Write(px, y * stride, stride);
                for (int y = size - 1; y >= 0; y--)
                {
                    byte[] row = new byte[maskStride];
                    for (int x = 0; x < size; x++)
                        if (px[y * stride + x * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                    bw.Write(row);
                }
                bw.Flush();
                return ms.ToArray();
            }
        }
    }

    /// <summary>Desktop / Start-menu shortcuts that get pinned to the taskbar, plus autostart.</summary>
    internal static class Integration
    {
        public const string StartMenuFolderName = "任务栏文件夹";

        public static string AppIdFor(string group)
        {
            return "DDtools.DockFolder.G" + Program.Fnv(group.ToLowerInvariant()).ToString("x8");
        }

        static string Args(string group) { return "--group \"" + group + "\""; }

        static string StartMenuDir()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuFolderName);
        }

        static string PinnedDir()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        }

        static string Desktop() { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }

        /// <summary>Creates the desktop + Start menu shortcuts for a group. Returns the desktop shortcut path.</summary>
        public static string CreateShortcuts(Group g)
        {
            string ico = FolderIcon.Generate(g);
            string desc = "任务栏文件夹：" + g.Name;
            Directory.CreateDirectory(StartMenuDir());
            string sm = System.IO.Path.Combine(StartMenuDir(), g.Name + ".lnk");
            Shortcut.Create(sm, Program.ExePath, Args(g.Name), Program.ExeDir, ico, desc, AppIdFor(g.Name));

            string desk = System.IO.Path.Combine(Desktop(), g.Name + ".lnk");
            if (File.Exists(desk) && FindGroup(desk) == null)
                desk = System.IO.Path.Combine(Desktop(), g.Name + " (文件夹).lnk");
            Shortcut.Create(desk, Program.ExePath, Args(g.Name), Program.ExeDir, ico, desc, AppIdFor(g.Name));
            JumpLists.Update(g.Name);
            Notify();
            if (Host.Current != null) Host.Current.RefreshTaskbarNames();
            return desk;
        }

        /// <summary>Returns the group name a shortcut opens, or null if it isn't one of ours.</summary>
        public static string FindGroup(string lnk)
        {
            string target, args;
            if (!Shortcut.Read(lnk, out target, out args)) return null;
            if (!string.Equals(target, Program.ExePath, StringComparison.OrdinalIgnoreCase)) return null;
            if (args == null) return null;
            int k = args.IndexOf("--group", StringComparison.OrdinalIgnoreCase);
            if (k < 0) return null;
            string rest = args.Substring(k + 7).Trim();
            if (rest.StartsWith("\""))
            {
                int e = rest.IndexOf('"', 1);
                return e > 0 ? rest.Substring(1, e - 1) : rest.Substring(1);
            }
            int sp = rest.IndexOf(' ');
            return sp > 0 ? rest.Substring(0, sp) : rest;
        }

        static IEnumerable<string> OurShortcuts(string group)
        {
            List<string> found = new List<string>();
            foreach (string dir in new string[] { Desktop(), StartMenuDir(), PinnedDir() })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.lnk"))
                    {
                        string gname = FindGroup(f);
                        if (gname != null && string.Equals(gname, group, StringComparison.OrdinalIgnoreCase)) found.Add(f);
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>Every shortcut of ours (desktop, Start menu, pinned): its name → the group it opens.</summary>
        public static Dictionary<string, string> AllShortcutNames()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in new string[] { Desktop(), StartMenuDir(), PinnedDir() })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.lnk"))
                    {
                        string gname = FindGroup(f);
                        if (gname != null) d[System.IO.Path.GetFileNameWithoutExtension(f)] = gname;
                    }
                }
                catch { }
            }
            return d;
        }

        public static bool HasShortcuts(string group)
        {
            foreach (string s in OurShortcuts(group)) return true;
            return false;
        }

        /// <summary>Regenerates the folder icon and points every existing shortcut (incl. the pinned one) at it.</summary>
        public static void RefreshIcon(Group g)
        {
            List<string> lnks = new List<string>(OurShortcuts(g.Name));
            if (lnks.Count == 0) return;
            string ico = FolderIcon.Generate(g);
            foreach (string l in lnks)
            {
                try { Shortcut.SetIcon(l, ico); } catch (Exception ex) { Program.Log("SetIcon: " + ex.Message); }
            }
            Notify();
        }

        public static void RemoveShortcuts(string group)
        {
            foreach (string l in OurShortcuts(group))
            {
                if (l.StartsWith(PinnedDir(), StringComparison.OrdinalIgnoreCase)) continue; // leave the pin to the user
                try { File.Delete(l); } catch { }
            }
            JumpLists.Delete(group);
            Notify();
            if (Host.Current != null) Host.Current.RefreshTaskbarNames();
        }

        static void Notify()
        {
            try { Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } // SHCNE_ASSOCCHANGED
        }

        // ---- autostart
        static string StartupLnk()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "任务栏文件夹.lnk");
        }

        public static bool AutostartEnabled() { return File.Exists(StartupLnk()); }

        public static void SetAutostart(bool on)
        {
            try
            {
                if (on) Shortcut.Create(StartupLnk(), Program.ExePath, "--background", Program.ExeDir, null, "任务栏文件夹（后台）", null);
                else if (File.Exists(StartupLnk())) File.Delete(StartupLnk());
            }
            catch (Exception ex) { Program.Log("autostart: " + ex.Message); }
        }
    }
}
