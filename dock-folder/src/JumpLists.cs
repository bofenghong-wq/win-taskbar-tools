using System;
using System.Runtime.InteropServices;

namespace DockFolder
{
    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    internal class CDestinationList { }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IObjectCollection
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    internal class CEnumerableObjectCollection { }

    /// <summary>
    /// Right-click menu of the pinned taskbar button (Windows "jump list"):
    /// 扇形展开 / 网格展开 / 自动 / 管理此文件夹….
    /// </summary>
    internal static class JumpLists
    {
        public static void Update(string group)
        {
            ICustomDestinationList list = null;
            try
            {
                Group g = GroupStore.Load(group);
                list = (ICustomDestinationList)new CDestinationList();
                list.SetAppID(Integration.AppIdFor(group));
                uint slots;
                object removed;
                Guid iid = typeof(IObjectArray).GUID;
                int hr = list.BeginList(out slots, ref iid, out removed);
                if (hr != 0) return;

                IObjectCollection tasks = (IObjectCollection)new CEnumerableObjectCollection();
                string q = "--group \"" + group + "\"";
                tasks.AddObject(Link(q + " --view fan", Mark("扇形展开", g.View == "fan"), "像 Dock 的扇形，图标沿弧线弹出"));
                tasks.AddObject(Link(q + " --view grid", Mark("网格展开", g.View == "grid"), "像 iOS 文件夹的网格面板"));
                tasks.AddObject(Link(q + " --view auto", Mark("自动（10 个以内扇形）", g.View == "auto"), "数量少用扇形，多用网格"));
                tasks.AddObject(Link(q + " --manage", "管理此文件夹…", "添加、删除、排序里面的程序"));
                list.AddUserTasks((IObjectArray)tasks);
                list.CommitList();
            }
            catch (Exception ex) { Program.Log("jumplist " + group + ": " + ex.Message); }
            finally { if (list != null) Marshal.ReleaseComObject(list); }
        }

        public static void Delete(string group)
        {
            try
            {
                ICustomDestinationList list = (ICustomDestinationList)new CDestinationList();
                list.DeleteList(Integration.AppIdFor(group));
                Marshal.ReleaseComObject(list);
            }
            catch { }
        }

        public static void UpdateAll()
        {
            foreach (string n in GroupStore.GroupNames())
                if (Integration.HasShortcuts(n)) Update(n);
        }

        static string Mark(string title, bool current)
        {
            return current ? title + "  ✓" : title;
        }

        static object Link(string args, string title, string tip)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            link.SetPath(Program.ExePath);
            link.SetArguments(args);
            link.SetIconLocation(Program.ExePath, 0);
            link.SetDescription(tip);
            IPropertyStore store = (IPropertyStore)link;
            PROPERTYKEY key = new PROPERTYKEY();
            key.fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"); // PKEY_Title
            key.pid = 2;
            PROPVARIANT pv = new PROPVARIANT();
            pv.vt = 31; // VT_LPWSTR
            pv.p = Marshal.StringToCoTaskMemUni(title);
            try
            {
                store.SetValue(ref key, ref pv);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(pv.p); }
            return link;
        }
    }
}
