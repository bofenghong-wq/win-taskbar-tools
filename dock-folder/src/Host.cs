using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace DockFolder
{
    /// <summary>The resident process: pipe server, tray icon, folder watcher, popup and manager windows.</summary>
    internal class Host
    {
        public static Host Current;
        readonly Application _app;
        public Dispatcher Dispatcher;
        PopupWindow _popup;
        ManagerWindow _manager;
        WF.NotifyIcon _tray;
        FileSystemWatcher _watcher;
        DispatcherTimer _debounce;
        readonly HashSet<string> _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _exiting;
        TaskbarRightClick _tbHook;

        public static int Run(string firstCmd)
        {
            try { Native.SetCurrentProcessExplicitAppUserModelID("DDtools.DockFolder.Host"); } catch { }
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Program.Log("UI error: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Program.Log("Fatal: " + e.ExceptionObject);
            };
            Host host = new Host(app);
            Current = host;
            app.Dispatcher.BeginInvoke(new Action(delegate { host.Start(firstCmd); }));
            app.Run();
            return 0;
        }

        Host(Application app)
        {
            _app = app;
            Dispatcher = app.Dispatcher;
        }

        void Start(string firstCmd)
        {
            try { WF.Application.EnableVisualStyles(); } catch { }
            GroupStore.Init();
            bool first = GroupStore.EnsureSample();
            _popup = new PopupWindow();
            StartPipeServer();
            InitTray();
            StartWatcher();
            IconCache.PreloadAsync(GroupStore.AllItemPaths());
            PopupWindow.WarmUpAutomation();
            _tbHook = new TaskbarRightClick(Dispatcher);
            _tbHook.Install();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(JumpLists.UpdateAll));
            if (first && firstCmd == "background") firstCmd = "manage";
            Handle(firstCmd);
        }

        // ------------------------------------------------------------ commands

        public void Handle(string cmd)
        {
            try
            {
                string[] p = cmd.Split(Program.Sep);
                switch (p[0])
                {
                    case "show":
                        {
                            Native.POINT pt = new Native.POINT();
                            if (p.Length >= 4) { int.TryParse(p[2], out pt.X); int.TryParse(p[3], out pt.Y); }
                            else Native.GetCursorPos(out pt);
                            ShowGroup(p[1], pt);
                            break;
                        }
                    case "add":
                        {
                            List<string> paths = new List<string>();
                            for (int i = 2; i < p.Length; i++) paths.Add(p[i]);
                            AddToGroup(p[1], paths);
                            break;
                        }
                    case "manage":
                        ShowManager(p.Length > 1 && GroupStore.Exists(p[1]) ? p[1] : null);
                        break;
                    case "view":
                        SwitchView(p[1], p.Length > 2 ? p[2] : "auto");
                        break;
                    case "exit":
                        Exit();
                        break;
                }
            }
            catch (Exception ex) { Program.Log("Handle " + cmd + ": " + ex); }
        }

        void ShowGroup(string name, Native.POINT pt)
        {
            if (!GroupStore.Exists(name))
            {
                MacAlert.Info(null, "找不到「" + name + "」", "这个文件夹可能已被重命名或删除。请在管理窗口里重新固定到任务栏。");
                ShowManager(null);
                return;
            }
            _popup.Toggle(GroupStore.Load(name), pt, false);
        }

        public void RefreshTaskbarNames()
        {
            if (_tbHook != null) _tbHook.RefreshNames();
        }

        /// <summary>True when a taskbar button caption belongs to this folder (or when we can't tell).</summary>
        public bool IsTaskbarButtonOf(string caption, string group)
        {
            if (_tbHook == null || !_tbHook.HasNames) return true;
            return _tbHook.IsButtonOf(caption, group);
        }

        /// <summary>macOS-style menu above a pinned taskbar folder.</summary>
        public void ShowTaskbarMenu(string name, Rect buttonPx)
        {
            if (!GroupStore.Exists(name)) return;
            if (!buttonPx.IsEmpty) PopupWindow.RememberButton(name, buttonPx);
            _popup.BeginClose();
            _popup.ClearToggle();
            Group g = GroupStore.Load(name);
            Native.POINT anchor = new Native.POINT();
            if (!buttonPx.IsEmpty) { anchor.X = (int)(buttonPx.Left + buttonPx.Width / 2); anchor.Y = (int)buttonPx.Top; }
            else Native.GetCursorPos(out anchor);

            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Item("打开", delegate
            {
                Native.POINT c;
                if (PopupWindow.TryGetButtonCenter(name, out c)) _popup.Toggle(GroupStore.Load(name), c, false);
            }));
            m.Add(MacMenuItem.Sep());
            StackMenus.AddGroupSections(m, g,
                delegate (string s) { SetSort(name, s); },
                delegate (string v) { SwitchView(name, v); },
                delegate { OpenInExplorer(g.Dir); },
                delegate { ShowManager(name); });
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Hint("按住 Shift 右键可打开 Windows 菜单"));
            MacMenu.Show(m, anchor, !buttonPx.IsEmpty, null);
        }

        public void SetSort(string name, string sort)
        {
            if (!GroupStore.Exists(name)) return;
            GroupStore.SetSort(GroupStore.Load(name), sort);
            try { Integration.RefreshIcon(GroupStore.Load(name)); } catch { }
            if (_manager != null) _manager.Reload();
        }

        /// <summary>Chosen from the taskbar button's right-click menu.</summary>
        public void SwitchView(string name, string view)
        {
            if (view != "fan" && view != "grid") view = "auto";
            if (!GroupStore.Exists(name)) { ShowGroup(name, new Native.POINT()); return; }
            GroupStore.SetView(GroupStore.Load(name), view);
            _popup.ClearToggle();
            Native.POINT c;
            if (PopupWindow.TryGetButtonCenter(name, out c))
            {
                _popup.CloseNow();
                _popup.Toggle(GroupStore.Load(name), c, false); // show the new style right away
            }
            else
                Balloon("「" + name + "」已改为" + (view == "fan" ? "扇形展开" : view == "grid" ? "网格展开" : "自动选择样式") + "，点任务栏按钮查看。");
        }

        public void Preview(Group g, Native.POINT pt)
        {
            _popup.Toggle(GroupStore.Load(g.Name), pt, true);
        }

        void AddToGroup(string name, List<string> paths)
        {
            if (!GroupStore.Exists(name)) { ShowGroup(name, new Native.POINT()); return; }
            Group g = GroupStore.Load(name);
            int n = 0;
            foreach (string s in paths) if (GroupStore.AddPath(g, s) != null) n++;
            if (n > 0) Balloon("已添加 " + n + " 项到「" + name + "」");
        }

        public void ShowManager(string selectGroup)
        {
            _popup.CloseNow();
            if (_manager == null)
            {
                ManagerWindow w = new ManagerWindow();
                _manager = w;
                w.Closed += delegate { if (_manager == w) _manager = null; };
                w.Show();
            }
            if (_manager.WindowState == WindowState.Minimized) _manager.WindowState = WindowState.Normal;
            _manager.Activate();
            if (selectGroup != null) _manager.Select(selectGroup);
        }

        public void Balloon(string text)
        {
            try { if (_tray != null) _tray.ShowBalloonTip(2500, "任务栏文件夹", text, WF.ToolTipIcon.Info); } catch { }
        }

        public void Exit()
        {
            if (_exiting) return;
            _exiting = true;
            try { _popup.CloseNow(); } catch { }
            if (_tbHook != null) _tbHook.Uninstall();
            if (MacMenu.Current != null) MacMenu.Current.CloseNow();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_watcher != null) _watcher.Dispose();
            _app.Shutdown();
        }

        // ------------------------------------------------------------ IPC

        void StartPipeServer()
        {
            Thread t = new Thread(PipeLoop);
            t.IsBackground = true;
            t.Name = "pipe";
            t.Start();
        }

        void PipeLoop()
        {
            while (true)
            {
                try
                {
                    using (NamedPipeServerStream s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                               PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        s.WaitForConnection();
                        MemoryStream ms = new MemoryStream();
                        byte[] buf = new byte[4096];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        string text = Encoding.UTF8.GetString(ms.ToArray());
                        if (text.Length > 0)
                            Dispatcher.BeginInvoke(new Action<string>(Handle), text);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("pipe: " + ex.Message);
                    Thread.Sleep(300);
                }
            }
        }

        // ------------------------------------------------------------ tray

        void InitTray()
        {
            _tray = new WF.NotifyIcon();
            try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Program.ExePath); } catch { }
            _tray.Text = "任务栏文件夹";
            _tray.MouseUp += delegate (object s, WF.MouseEventArgs e)
            {
                if (e.Button == WF.MouseButtons.Left) ShowManager(null);
                else if (e.Button == WF.MouseButtons.Right) TrayMenu();
            };
            _tray.Visible = true;
        }

        void TrayMenu()
        {
            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Item("管理文件夹…", delegate { ShowManager(null); }));
            m.Add(MacMenuItem.Item("打开数据目录", delegate { OpenInExplorer(GroupStore.Root); }));
            m.Add(MacMenuItem.Sep());
            bool on = Integration.AutostartEnabled();
            m.Add(MacMenuItem.Check("开机自动启动", on, delegate { Integration.SetAutostart(!on); }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("退出任务栏文件夹", Exit));
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            MacMenu.Show(m, pt, false, null);
        }

        public static void OpenInExplorer(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        public static void SelectInExplorer(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        // ------------------------------------------------------------ folder watching

        void StartWatcher()
        {
            _debounce = new DispatcherTimer();
            _debounce.Interval = TimeSpan.FromMilliseconds(900);
            _debounce.Tick += delegate { _debounce.Stop(); FlushChanges(); };
            try
            {
                _watcher = new FileSystemWatcher(GroupStore.Root);
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                FileSystemEventHandler h = delegate (object s, FileSystemEventArgs e) { OnFs(e.FullPath); };
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Changed += h;
                _watcher.Renamed += delegate (object s, RenamedEventArgs e) { OnFs(e.OldFullPath); OnFs(e.FullPath); };
                // buffer overflow (e.g. a big copy): we lost events, so refresh everything
                _watcher.Error += delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (string n in GroupStore.GroupNames()) _changed.Add(n);
                        _debounce.Stop();
                        _debounce.Start();
                    }));
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Program.Log("watcher: " + ex.Message); }
        }

        void OnFs(string full)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (full == null || !full.StartsWith(GroupStore.Root, StringComparison.OrdinalIgnoreCase)) return;
                string rel = full.Substring(GroupStore.Root.Length).TrimStart('\\');
                string[] parts = rel.Split('\\');
                if (parts.Length >= 2 && GroupStore.IsMeta(parts[1])) return;
                _changed.Add(parts.Length >= 2 ? parts[0] : "");
                _debounce.Stop();
                _debounce.Start();
            }));
        }

        void FlushChanges()
        {
            List<string> changed = new List<string>(_changed);
            _changed.Clear();
            foreach (string name in changed)
            {
                if (name.Length == 0 || !GroupStore.Exists(name)) continue;
                try { Integration.RefreshIcon(GroupStore.Load(name)); }
                catch (Exception ex) { Program.Log("refresh icon: " + ex.Message); }
            }
            if (_manager != null) _manager.Reload();
            RefreshTaskbarNames();
        }
    }
}
