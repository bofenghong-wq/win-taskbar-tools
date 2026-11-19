using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DockFolder
{
    internal class GroupItem
    {
        public string Path;
        public string FileName;
        public string Name;
        public bool IsFolder;
    }

    internal class Group
    {
        public string Name;
        public string Dir;
        public string View = "auto";   // auto | fan | grid
        public string Sort = "manual"; // manual | name | date | kind
        public List<GroupItem> Items = new List<GroupItem>();
    }

    /// <summary>
    /// Every "taskbar folder" is a real folder under Groups\. Its items are the shortcuts/files in it,
    /// so it can also be edited straight from Explorer.
    /// </summary>
    internal static class GroupStore
    {
        public static string Root;
        public const string OrderFile = ".order";
        public const string IniFile = "group.ini";

        public static void Init()
        {
            string r = System.IO.Path.Combine(Program.ExeDir, "Groups");
            if (!CanWrite(r))
                r = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockFolder", "Groups");
            Directory.CreateDirectory(r);
            Root = r;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = System.IO.Path.Combine(dir, ".probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static bool IsMeta(string fileName)
        {
            string n = fileName.ToLowerInvariant();
            if (n.StartsWith(".") || n.StartsWith("~$")) return true;
            if (n == IniFile || n == "desktop.ini" || n == "thumbs.db") return true;
            if (n.StartsWith("group-") && n.EndsWith(".ico")) return true;
            return false;
        }

        public static List<string> GroupNames()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(Root))
                {
                    string n = System.IO.Path.GetFileName(d);
                    if (n.StartsWith(".")) continue;
                    list.Add(n);
                }
            }
            catch { }
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            return list;
        }

        public static bool Exists(string name)
        {
            return !string.IsNullOrEmpty(name) && Directory.Exists(System.IO.Path.Combine(Root, name));
        }

        public static Group Load(string name) { return Load(name, true); }

        /// <summary>sorted = false keeps the hand-arranged order (.order) even when a sort is active,
        /// which is what must be written back when adding or renaming.</summary>
        public static Group Load(string name, bool sorted)
        {
            Group g = new Group();
            g.Name = name;
            g.Dir = System.IO.Path.Combine(Root, name);
            if (!Directory.Exists(g.Dir)) return g;

            string ini = System.IO.Path.Combine(g.Dir, IniFile);
            try
            {
                if (File.Exists(ini))
                {
                    foreach (string line in File.ReadAllLines(ini, Encoding.UTF8))
                    {
                        int k = line.IndexOf('=');
                        if (k <= 0) continue;
                        string key = line.Substring(0, k).Trim().ToLowerInvariant();
                        string v = line.Substring(k + 1).Trim().ToLowerInvariant();
                        if (key == "view" && (v == "auto" || v == "fan" || v == "grid")) g.View = v;
                        if (key == "sort" && (v == "manual" || v == "name" || v == "date" || v == "kind")) g.Sort = v;
                    }
                }
            }
            catch { }

            List<string> entries = new List<string>();
            try
            {
                foreach (string f in Directory.GetFileSystemEntries(g.Dir))
                {
                    string n = System.IO.Path.GetFileName(f);
                    if (IsMeta(n)) continue;
                    FileAttributes a = File.GetAttributes(f);
                    if ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0) continue;
                    entries.Add(n);
                }
            }
            catch { }
            entries.Sort(StringComparer.CurrentCultureIgnoreCase);

            List<string> ordered = new List<string>();
            foreach (string o in ReadOrder(g.Dir))
            {
                int idx = entries.FindIndex(delegate (string e) { return string.Equals(e, o, StringComparison.OrdinalIgnoreCase); });
                if (idx >= 0) { ordered.Add(entries[idx]); entries.RemoveAt(idx); }
            }
            ordered.AddRange(entries);

            foreach (string n in ordered)
            {
                GroupItem it = new GroupItem();
                it.FileName = n;
                it.Path = System.IO.Path.Combine(g.Dir, n);
                it.IsFolder = Directory.Exists(it.Path);
                it.Name = DisplayName(n, it.IsFolder);
                g.Items.Add(it);
            }
            if (sorted) ApplySort(g);
            return g;
        }

        static void ApplySort(Group g)
        {
            if (g.Sort == "manual") return;
            Comparison<GroupItem> byName = delegate (GroupItem a, GroupItem b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); };
            if (g.Sort == "name") g.Items.Sort(byName);
            else if (g.Sort == "date")
            {
                // newest first, like "Date Added" in a macOS stack
                Dictionary<string, long> t = new Dictionary<string, long>();
                foreach (GroupItem it in g.Items) { long v = 0; try { v = File.GetCreationTimeUtc(it.Path).Ticks; } catch { } t[it.FileName] = v; }
                g.Items.Sort(delegate (GroupItem a, GroupItem b) { int c = t[b.FileName].CompareTo(t[a.FileName]); return c != 0 ? c : byName(a, b); });
            }
            else if (g.Sort == "kind")
                g.Items.Sort(delegate (GroupItem a, GroupItem b)
                {
                    int c = string.Compare(Kind(a), Kind(b), StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : byName(a, b);
                });
        }

        static string Kind(GroupItem it)
        {
            if (it.IsFolder) return "0";
            string ext = System.IO.Path.GetExtension(it.FileName).ToLowerInvariant();
            if (ext == ".lnk" || ext == ".exe" || ext == ".appref-ms") return "1";
            if (ext == ".url") return "2";
            return "3" + ext;
        }

        static string DisplayName(string fileName, bool isFolder)
        {
            if (isFolder) return fileName;
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".lnk" || ext == ".url" || ext == ".exe" || ext == ".appref-ms" || ext == ".bat" || ext == ".cmd")
                return System.IO.Path.GetFileNameWithoutExtension(fileName);
            return fileName;
        }

        static List<string> ReadOrder(string dir)
        {
            List<string> l = new List<string>();
            string f = System.IO.Path.Combine(dir, OrderFile);
            try
            {
                if (File.Exists(f))
                    foreach (string s in File.ReadAllLines(f, Encoding.UTF8))
                        if (s.Trim().Length > 0) l.Add(s.Trim());
            }
            catch { }
            return l;
        }

        public static void SaveOrder(Group g)
        {
            List<string> names = new List<string>();
            foreach (GroupItem it in g.Items) names.Add(it.FileName);
            WriteHidden(System.IO.Path.Combine(g.Dir, OrderFile), string.Join("\r\n", names.ToArray()));
        }

        public static void SetView(Group g, string view)
        {
            g.View = view;
            WriteIni(g);
            JumpLists.Update(g.Name);
        }

        public static void SetSort(Group g, string sort)
        {
            g.Sort = sort;
            WriteIni(g);
        }

        static void WriteIni(Group g)
        {
            File.WriteAllText(System.IO.Path.Combine(g.Dir, IniFile), "view=" + g.View + "\r\nsort=" + g.Sort + "\r\n", new UTF8Encoding(false));
        }

        static void WriteHidden(string path, string text)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetAttributes(path, FileAttributes.Hidden);
        }

        public static string SafeFileName(string name)
        {
            if (name == null) name = "";
            StringBuilder sb = new StringBuilder();
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().Trim('.').Trim();   // a leading "." would hide the folder
            if (s.Length == 0) return "未命名";
            string u = s.ToUpperInvariant();
            int dot = u.IndexOf('.');
            string stem = dot >= 0 ? u.Substring(0, dot) : u;
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (Array.IndexOf(reserved, stem) >= 0) s = s + "_";
            return s;
        }

        public static string UniquePath(string dir, string baseName, string ext)
        {
            string p = System.IO.Path.Combine(dir, baseName + ext);
            int i = 2;
            while (File.Exists(p) || Directory.Exists(p))
            {
                p = System.IO.Path.Combine(dir, baseName + " (" + i + ")" + ext);
                i++;
            }
            return p;
        }

        /// <summary>Adds a file / folder / shortcut to the group. Returns the created item file or null.</summary>
        public static string AddPath(Group g, string src)
        {
            if (string.IsNullOrEmpty(src)) return null;
            src = src.Trim().Trim('"');
            try
            {
                string full = System.IO.Path.GetFullPath(src);
                string parent = System.IO.Path.GetDirectoryName(full.TrimEnd('\\'));
                if (parent != null && string.Equals(parent.TrimEnd('\\'), g.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return null; // already inside

                string dest = null;
                if (Directory.Exists(full))
                {
                    string name = new DirectoryInfo(full).Name;
                    if (name.EndsWith("\\") || name.EndsWith(":")) name = "本地磁盘 (" + full.Substring(0, 1).ToUpperInvariant() + ")";
                    dest = UniquePath(g.Dir, SafeFileName(name), ".lnk");
                    Shortcut.Create(dest, full, null, null, null, null, null);
                }
                else if (File.Exists(full))
                {
                    string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
                    string baseName = SafeFileName(System.IO.Path.GetFileNameWithoutExtension(full));
                    if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms")
                    {
                        dest = UniquePath(g.Dir, baseName, ext);
                        File.Copy(full, dest);
                        File.SetAttributes(dest, FileAttributes.Normal);
                    }
                    else
                    {
                        dest = UniquePath(g.Dir, baseName, ".lnk");
                        Shortcut.Create(dest, full, null, System.IO.Path.GetDirectoryName(full), null, null, null);
                    }
                }
                if (dest != null) AppendOrder(g, System.IO.Path.GetFileName(dest));
                return dest;
            }
            catch (Exception ex)
            {
                Program.Log("AddPath " + src + ": " + ex.Message);
                return null;
            }
        }

        public static void AppendOrder(Group g, string fileName)
        {
            Group cur = Load(g.Name, false);
            // put the new item last
            cur.Items.RemoveAll(delegate (GroupItem i) { return string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase); });
            GroupItem it = new GroupItem();
            it.FileName = fileName;
            cur.Items.Add(it);
            SaveOrder(cur);
        }

        public static void Remove(GroupItem item)
        {
            try
            {
                if (item.IsFolder)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) { Program.Log("Remove: " + ex.Message); }
        }

        public static Group CreateGroup(string name)
        {
            name = SafeFileName(name);
            string dir = System.IO.Path.Combine(Root, name);
            int i = 2;
            string baseName = name;
            while (Directory.Exists(dir)) { name = baseName + " " + i; dir = System.IO.Path.Combine(Root, name); i++; }
            Directory.CreateDirectory(dir);
            return Load(name);
        }

        public static bool RenameGroup(string oldName, string newName)
        {
            newName = SafeFileName(newName);
            string a = System.IO.Path.Combine(Root, oldName), b = System.IO.Path.Combine(Root, newName);
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                // only the letter case changes: Windows needs a detour through a temporary name
                string tmp = System.IO.Path.Combine(Root, newName + ".~rename");
                Directory.Move(a, tmp);
                Directory.Move(tmp, b);
                return true;
            }
            if (Directory.Exists(b)) return false;
            Directory.Move(a, b);
            return true;
        }

        public static void DeleteGroup(string name)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(System.IO.Path.Combine(Root, name),
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static List<string> AllItemPaths()
        {
            List<string> l = new List<string>();
            foreach (string n in GroupNames())
                foreach (GroupItem it in Load(n).Items) l.Add(it.Path);
            return l;
        }

        /// <summary>First run: a sample folder so there's something to click.</summary>
        public static bool EnsureSample()
        {
            if (GroupNames().Count > 0) return false;
            Group g = CreateGroup("常用");
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string[][] apps = new string[][]
            {
                new string[] { "文件资源管理器", System.IO.Path.Combine(win, "explorer.exe") },
                new string[] { "记事本", System.IO.Path.Combine(sys, "notepad.exe") },
                new string[] { "计算器", System.IO.Path.Combine(sys, "calc.exe") },
                new string[] { "画图", System.IO.Path.Combine(sys, "mspaint.exe") },
                new string[] { "任务管理器", System.IO.Path.Combine(sys, "Taskmgr.exe") },
                new string[] { "控制面板", System.IO.Path.Combine(sys, "control.exe") },
            };
            List<string> order = new List<string>();
            foreach (string[] a in apps)
            {
                if (!File.Exists(a[1])) continue;
                string lnk = System.IO.Path.Combine(g.Dir, a[0] + ".lnk");
                try
                {
                    Shortcut.Create(lnk, a[1], null, null, null, null, null);
                    order.Add(a[0] + ".lnk");
                }
                catch (Exception ex) { Program.Log("sample: " + ex.Message); }
            }
            WriteHidden(System.IO.Path.Combine(g.Dir, OrderFile), string.Join("\r\n", order.ToArray()));
            return true;
        }
    }
}
