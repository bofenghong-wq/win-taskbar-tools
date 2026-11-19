using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace DockFolder
{
    /// <summary>
    /// Manager window in the warm Claude-desktop idiom: cream sidebar with a "+ 新建文件夹" row,
    /// serif page title, white rounded card holding the icons, clay primary button.
    /// The title bar is its own row with our own caption buttons, so nothing overlaps the toolbar.
    /// </summary>
    internal class ManagerWindow : Window
    {
        const double CaptionH = 40, SidebarW = 248;

        class Tile
        {
            public Border Root;
            public TextBlock Name;
            public GroupItem Item;
        }

        readonly MacPalette P;
        readonly Border _frame = new Border();
        readonly StackPanel _side = new StackPanel();
        readonly TextBlock _title, _subtitle, _status;
        readonly StackPanel _tools = new StackPanel();
        readonly WrapPanel _tiles = new WrapPanel();
        readonly FrameworkElement _empty;
        readonly Rectangle _dropRing = new Rectangle();
        readonly List<Tile> _tileList = new List<Tile>();
        Segmented _seg;
        Border _sortBtn;
        Group _cur;
        Tile _sel;
        Point _dragStart;
        GroupItem _dragItem;

        static readonly string[] ViewKeys = { "fan", "grid", "auto" };
        static readonly string[] ViewLabels = { "扇形", "网格", "自动" };

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public ManagerWindow()
        {
            P = MacPalette.Current();
            Title = "任务栏文件夹";
            Width = 1000; Height = 680; MinWidth = 800; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = MacUI.Font;
            FontSize = 13;
            Foreground = P.Text;
            Background = P.Window;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            WindowChrome wc = new WindowChrome();
            wc.CaptionHeight = CaptionH;
            wc.ResizeBorderThickness = new Thickness(6);
            wc.GlassFrameThickness = new Thickness(0);
            wc.CornerRadius = new CornerRadius(0);
            wc.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, wc);
            SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                try { int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4); } catch { }          // rounded corners (Win11)
                try { int dark = P.Light ? 0 : 1; DwmSetWindowAttribute(h, 20, ref dark, 4); } catch { } // dark border in dark mode
            };
            StateChanged += delegate { _frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); };

            Grid root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarW) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _frame.Child = root;
            Content = _frame;

            root.Children.Add(BuildSidebar());

            // ======================= content column
            DockPanel content = new DockPanel();
            Grid.SetColumn(content, 1);
            root.Children.Add(content);

            Grid caption = new Grid();
            caption.Height = CaptionH;
            caption.Children.Add(MacUI.CaptionButtons(this, P));
            DockPanel.SetDock(caption, Dock.Top);
            content.Children.Add(caption);

            Grid header = new Grid();
            header.Margin = new Thickness(32, 6, 28, 18);
            StackPanel titles = new StackPanel();
            _title = MacUI.Label("", 26, P.Text, false);
            _title.FontFamily = MacUI.Serif;
            _subtitle = MacUI.Label("", 13, P.Sub, false);
            _subtitle.Margin = new Thickness(0, 4, 0, 0);
            titles.Children.Add(_title);
            titles.Children.Add(_subtitle);
            header.Children.Add(titles);
            _tools.Orientation = Orientation.Horizontal;
            _tools.HorizontalAlignment = HorizontalAlignment.Right;
            _tools.VerticalAlignment = VerticalAlignment.Bottom;
            header.Children.Add(_tools);
            DockPanel.SetDock(header, Dock.Top);
            content.Children.Add(header);

            Grid footer = new Grid();
            footer.Height = 68;
            _status = MacUI.Label("", 12.5, P.Sub, false);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(32, 0, 0, 0);
            _status.MaxWidth = 340;
            footer.Children.Add(_status);
            StackPanel fb = new StackPanel();
            fb.Orientation = Orientation.Horizontal;
            fb.HorizontalAlignment = HorizontalAlignment.Right;
            fb.VerticalAlignment = VerticalAlignment.Center;
            fb.Margin = new Thickness(0, 0, 28, 0);
            Border addBtn = null;
            addBtn = MacUI.Button("添加", false, true, P, delegate { AddMenu(addBtn); });
            addBtn.Margin = new Thickness(0, 0, 8, 0);
            fb.Children.Add(addBtn);
            Border openBtn = MacUI.Button("在资源管理器中打开", false, P, delegate { if (_cur != null) Host.OpenInExplorer(_cur.Dir); });
            openBtn.Margin = new Thickness(0, 0, 8, 0);
            fb.Children.Add(openBtn);
            fb.Children.Add(MacUI.Button("固定到任务栏", true, P, CreateShortcut));
            footer.Children.Add(fb);
            DockPanel.SetDock(footer, Dock.Bottom);
            content.Children.Add(footer);

            // the icons live on a white rounded card
            Border card = new Border();
            card.Margin = new Thickness(28, 0, 28, 0);
            card.CornerRadius = new CornerRadius(14);
            card.Background = P.Card;
            card.BorderBrush = P.Border;
            card.BorderThickness = new Thickness(1);
            Grid area = new Grid();
            area.AllowDrop = true;
            area.Background = Brushes.Transparent;
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _tiles.Margin = new Thickness(16);
            sv.Content = _tiles;
            area.Children.Add(sv);
            _empty = EmptyState();
            area.Children.Add(_empty);
            _dropRing.RadiusX = 12; _dropRing.RadiusY = 12;
            _dropRing.Stroke = P.Accent;
            _dropRing.StrokeThickness = 2;
            _dropRing.StrokeDashArray = new DoubleCollection(new double[] { 4, 3 });
            _dropRing.Margin = new Thickness(6);
            _dropRing.IsHitTestVisible = false;
            _dropRing.Opacity = 0;
            area.Children.Add(_dropRing);
            area.DragEnter += OnDragOver;
            area.DragOver += OnDragOver;
            area.DragLeave += delegate { Ring(false); };
            area.Drop += OnDrop;
            area.MouseLeftButtonDown += delegate { SelectTile(null); };
            area.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; GroupMenu(_cur); };
            card.Child = area;
            content.Children.Add(card);

            PreviewKeyDown += OnKey;
            Reload();
        }

        FrameworkElement BuildSidebar()
        {
            Border sidebar = new Border();
            sidebar.Background = P.Sidebar;
            sidebar.BorderBrush = P.Border;
            sidebar.BorderThickness = new Thickness(0, 0, 1, 0);
            DockPanel sd = new DockPanel();

            StackPanel brand = new StackPanel();
            brand.Orientation = Orientation.Horizontal;
            brand.Height = CaptionH + 8;
            brand.Margin = new Thickness(18, 0, 0, 0);
            Image appIcon = new Image();
            appIcon.Source = IconCache.Get(Program.ExePath);
            appIcon.Width = 20; appIcon.Height = 20;
            appIcon.VerticalAlignment = VerticalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(appIcon, BitmapScalingMode.HighQuality);
            brand.Children.Add(appIcon);
            TextBlock bt = MacUI.Label("任务栏文件夹", 15, P.Text, false);
            bt.FontFamily = MacUI.Serif;
            bt.VerticalAlignment = VerticalAlignment.Center;
            bt.Margin = new Thickness(10, 0, 0, 1);
            brand.Children.Add(bt);
            DockPanel.SetDock(brand, Dock.Top);
            sd.Children.Add(brand);

            // "+ 新建文件夹" row (like "+ New chat")
            Border add = new Border();
            add.Height = 38;
            add.Margin = new Thickness(10, 4, 10, 10);
            add.Padding = new Thickness(8, 0, 8, 0);
            add.CornerRadius = new CornerRadius(8);
            add.Background = Brushes.Transparent;
            add.Cursor = Cursors.Hand;
            StackPanel ap = new StackPanel();
            ap.Orientation = Orientation.Horizontal;
            Border plus = new Border();
            plus.Width = 22; plus.Height = 22;
            plus.CornerRadius = new CornerRadius(11);
            plus.Background = P.Accent;
            plus.VerticalAlignment = VerticalAlignment.Center;
            plus.Child = MacUI.Glyph("", 10, Brushes.White);
            ap.Children.Add(plus);
            TextBlock at = MacUI.Label("新建文件夹", 13.5, P.Text, true);
            at.VerticalAlignment = VerticalAlignment.Center;
            at.Margin = new Thickness(10, 0, 0, 1);
            ap.Children.Add(at);
            add.Child = ap;
            add.MouseEnter += delegate { add.Background = P.Hover; };
            add.MouseLeave += delegate { add.Background = Brushes.Transparent; };
            add.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; NewGroup(); };
            DockPanel.SetDock(add, Dock.Top);
            sd.Children.Add(add);

            TextBlock sh = MacUI.Label("文件夹", 12, P.Sub, false);
            sh.Margin = new Thickness(20, 0, 0, 6);
            DockPanel.SetDock(sh, Dock.Top);
            sd.Children.Add(sh);

            // footer: autostart switch
            Border foot = new Border();
            foot.BorderBrush = P.Border;
            foot.BorderThickness = new Thickness(0, 1, 0, 0);
            foot.Padding = new Thickness(20, 14, 18, 16);
            DockPanel fd = new DockPanel();
            FrameworkElement tg = MacUI.Toggle(Integration.AutostartEnabled(), P, delegate (bool on) { Integration.SetAutostart(on); });
            DockPanel.SetDock(tg, Dock.Right);
            fd.Children.Add(tg);
            TextBlock ft = MacUI.Label("开机自动启动", 13, P.Text, false);
            ft.VerticalAlignment = VerticalAlignment.Center;
            fd.Children.Add(ft);
            foot.Child = fd;
            DockPanel.SetDock(foot, Dock.Bottom);
            sd.Children.Add(foot);

            ScrollViewer ssv = new ScrollViewer();
            ssv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ssv.Content = _side;
            sd.Children.Add(ssv);
            sidebar.Child = sd;
            return sidebar;
        }

        FrameworkElement EmptyState()
        {
            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.IsHitTestVisible = false;
            sp.Children.Add(MacUI.Glyph("", 40, P.Tertiary));
            TextBlock t = MacUI.Label("把应用拖到这里", 18, P.Text, false);
            t.FontFamily = MacUI.Serif;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 14, 0, 6);
            sp.Children.Add(t);
            TextBlock s = MacUI.Label("程序、快捷方式、文件夹，或「所有应用」里的应用都可以。\n也可以点下方的「添加」。", 13, P.Sub, false);
            s.TextAlignment = TextAlignment.Center;
            s.LineHeight = 20;
            s.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(s);
            return sp;
        }

        void Status(string s)
        {
            _status.Text = s ?? "";
            if (string.IsNullOrEmpty(s)) return;
            _status.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        void Menu(List<MacMenuItem> items, Native.POINT pt)
        {
            MacMenu.Show(items, pt, false, null, P);
        }

        // ================================================================ sidebar list

        public void Reload()
        {
            string sel = _cur != null ? _cur.Name : null;
            List<string> names = GroupStore.GroupNames();
            if (sel == null || !names.Contains(sel)) sel = names.Count > 0 ? names[0] : null;
            if (sel != null) ShowGroup(sel); else ShowNone();
        }

        void FillSidebar()
        {
            _side.Children.Clear();
            foreach (string name in GroupStore.GroupNames())
            {
                Group g = GroupStore.Load(name);
                bool sel = _cur != null && string.Equals(_cur.Name, name, StringComparison.OrdinalIgnoreCase);
                Border b = new Border();
                b.Height = 36;
                b.Margin = new Thickness(10, 1, 10, 1);
                b.Padding = new Thickness(10, 0, 12, 0);
                b.CornerRadius = new CornerRadius(8);
                b.Background = sel ? P.Selection : Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                DockPanel dp = new DockPanel();
                Image img = new Image();
                img.Width = 20; img.Height = 20;
                img.Margin = new Thickness(0, 0, 10, 0);
                try { img.Source = FolderIcon.RenderPreview(g, 40); } catch { }
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                dp.Children.Add(img);
                TextBlock count = MacUI.Label(g.Items.Count.ToString(), 12, P.Sub, false);
                count.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(count, Dock.Right);
                dp.Children.Add(count);
                TextBlock t = MacUI.Label(name, 13.5, P.Text, sel);
                t.VerticalAlignment = VerticalAlignment.Center;
                dp.Children.Add(t);
                b.Child = dp;
                string n = name;
                b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    // don't rebuild the list when the folder is already selected, so a double-click
                    // lands on the same row and reliably opens rename
                    if (_cur == null || !string.Equals(_cur.Name, n, StringComparison.OrdinalIgnoreCase)) ShowGroup(n);
                    else if (e.ClickCount == 2) RenameGroup();
                };
                b.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    ShowGroup(n);
                    GroupMenu(_cur);
                };
                if (!sel)
                {
                    b.MouseEnter += delegate { b.Background = P.Hover; };
                    b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
                }
                _side.Children.Add(b);
            }
        }

        public void Select(string name) { if (GroupStore.Exists(name)) ShowGroup(name); }

        void ShowNone()
        {
            _cur = null;
            FillSidebar();
            _title.Text = "还没有文件夹";
            _subtitle.Text = "点左上角的「新建文件夹」开始";
            _tools.Children.Clear();
            _tiles.Children.Clear();
            _tileList.Clear();
            _empty.Visibility = Visibility.Collapsed;
        }

        void ShowGroup(string name)
        {
            _cur = GroupStore.Load(name);
            FillSidebar();
            _title.Text = _cur.Name;
            UpdateSubtitle();
            BuildTools();
            _tiles.Children.Clear();
            _tileList.Clear();
            _sel = null;
            foreach (GroupItem it in _cur.Items) _tiles.Children.Add(MakeTile(it));
            _empty.Visibility = _cur.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateSubtitle()
        {
            _subtitle.Text = _cur.Items.Count + " 个项目  ·  " + StackMenus.ViewName(_cur.View) + "  ·  按" + SortName(_cur.Sort) + "排序";
        }

        static string SortName(string s)
        {
            return s == "name" ? "名称" : s == "date" ? "添加日期" : s == "kind" ? "种类" : "手动";
        }

        void BuildTools()
        {
            _tools.Children.Clear();
            _seg = new Segmented(ViewLabels, Array.IndexOf(ViewKeys, _cur.View), P, delegate (int i)
            {
                GroupStore.SetView(_cur, ViewKeys[i]);
                UpdateSubtitle();
                Status("已改为" + StackMenus.ViewName(_cur.View) + "。");
            });
            _seg.ToolTip = "展开样式";
            _tools.Children.Add(_seg);
            _sortBtn = null;
            _sortBtn = MacUI.Button("排序：" + SortName(_cur.Sort), false, true, P, delegate { SortMenu(_sortBtn); });
            _sortBtn.Margin = new Thickness(10, 0, 0, 0);
            _tools.Children.Add(_sortBtn);
            Border prev = MacUI.IconButton("", 13, P, "预览展开效果", Preview);
            prev.Margin = new Thickness(6, 0, 0, 0);
            _tools.Children.Add(prev);
        }

        static Native.POINT BelowElement(FrameworkElement el)
        {
            Point p = el.PointToScreen(new Point(0, el.ActualHeight + 4));
            Native.POINT pt = new Native.POINT();
            pt.X = (int)p.X; pt.Y = (int)p.Y;
            return pt;
        }

        void SortMenu(FrameworkElement anchor)
        {
            if (_cur == null) return;
            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Header("排序方式"));
            foreach (string k in new string[] { "name", "date", "kind", "manual" })
            {
                string key = k;
                m.Add(MacMenuItem.Check(key == "manual" ? "手动排列" : SortName(key), _cur.Sort == key, delegate { SetSort(key); }));
            }
            Menu(m, BelowElement(anchor));
        }

        void SetSort(string key)
        {
            if (_cur == null) return;
            Host.Current.SetSort(_cur.Name, key);
            ShowGroup(_cur.Name);
        }

        void GroupMenu(Group g)
        {
            if (g == null) return;
            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Header("展开样式"));
            for (int i = 0; i < ViewKeys.Length; i++)
            {
                int idx = i;
                m.Add(MacMenuItem.Check(ViewLabels[i], g.View == ViewKeys[i], delegate { _seg.Select(idx, true, true); }));
            }
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Header("排序方式"));
            foreach (string k in new string[] { "name", "date", "kind", "manual" })
            {
                string key = k;
                m.Add(MacMenuItem.Check(key == "manual" ? "手动排列" : SortName(key), g.Sort == key, delegate { SetSort(key); }));
            }
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("预览展开效果", Preview));
            m.Add(MacMenuItem.Item("固定到任务栏", CreateShortcut));
            m.Add(MacMenuItem.Item("在资源管理器中打开", delegate { Host.OpenInExplorer(g.Dir); }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("重命名…", RenameGroup));
            m.Add(MacMenuItem.Item("删除文件夹…", DeleteGroup));
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            Menu(m, pt);
        }

        // ================================================================ icon grid

        FrameworkElement MakeTile(GroupItem it)
        {
            Tile t = new Tile();
            t.Item = it;
            t.Root = new Border();
            t.Root.Width = 108; t.Root.Height = 120;
            t.Root.Margin = new Thickness(2);
            t.Root.CornerRadius = new CornerRadius(12);
            t.Root.Background = Brushes.Transparent;
            t.Root.Tag = t;
            t.Root.ToolTip = it.Name;

            StackPanel sp = new StackPanel();
            Image img = new Image();
            img.Source = IconCache.Get(it.Path);
            img.Width = 52; img.Height = 52;
            img.Margin = new Thickness(0, 16, 0, 0);
            img.HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            sp.Children.Add(img);
            t.Name = MacUI.Label(it.Name, 12.5, P.Text, false);
            t.Name.TextWrapping = TextWrapping.Wrap;
            t.Name.TextAlignment = TextAlignment.Center;
            t.Name.MaxWidth = 96;
            t.Name.MaxHeight = 36;
            t.Name.HorizontalAlignment = HorizontalAlignment.Center;
            t.Name.Margin = new Thickness(6, 10, 6, 0);
            sp.Children.Add(t.Name);
            t.Root.Child = sp;
            _tileList.Add(t);

            t.Root.MouseEnter += delegate { if (_sel != t) t.Root.Background = P.Hover; };
            t.Root.MouseLeave += delegate { if (_sel != t) t.Root.Background = Brushes.Transparent; };
            t.Root.PreviewMouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                SelectTile(t);
                if (e.ClickCount == 2) { e.Handled = true; Open(it); return; }
                _dragStart = e.GetPosition(this);
                _dragItem = it;
            };
            t.Root.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            t.Root.PreviewMouseMove += delegate (object s, MouseEventArgs e)
            {
                if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
                Vector d = e.GetPosition(this) - _dragStart;
                if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
                GroupItem item = _dragItem;
                _dragItem = null;
                t.Root.Opacity = 0.45;
                try { DragDrop.DoDragDrop(t.Root, new DataObject("DockFolderItem", item.FileName), DragDropEffects.Move); }
                finally { t.Root.Opacity = 1; }
            };
            t.Root.PreviewMouseLeftButtonUp += delegate { _dragItem = null; };
            t.Root.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                SelectTile(t);
                ItemMenu(it);
            };
            return t.Root;
        }

        void SelectTile(Tile t)
        {
            if (_sel != null)
            {
                _sel.Root.Background = Brushes.Transparent;
                _sel.Root.BorderThickness = new Thickness(0);
                _sel.Name.FontWeight = FontWeights.Normal;
            }
            _sel = t;
            if (_sel != null)
            {
                _sel.Root.Background = P.Selection;
                _sel.Name.FontWeight = FontWeights.SemiBold;
            }
        }

        void ItemMenu(GroupItem it)
        {
            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Item("打开", delegate { Open(it); }));
            m.Add(MacMenuItem.Item("在资源管理器中显示", delegate
            {
                string target, args, p = it.Path;
                if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Shortcut.Read(p, out target, out args) && !string.IsNullOrEmpty(target)) p = target;
                Host.SelectInExplorer(p);
            }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("重命名…", delegate { RenameItem(it); }));
            m.Add(MacMenuItem.Item("向前移", delegate { Move(it, -1); }));
            m.Add(MacMenuItem.Item("向后移", delegate { Move(it, +1); }));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("从文件夹中移除", delegate { RemoveItem(it); }));
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            Menu(m, pt);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (_sel == null) return;
            if (e.Key == Key.Delete) { e.Handled = true; RemoveItem(_sel.Item); }
            else if (e.Key == Key.Enter) { e.Handled = true; Open(_sel.Item); }
        }

        void Open(GroupItem it)
        {
            try { ProcessStartInfo psi = new ProcessStartInfo(it.Path); psi.UseShellExecute = true; Process.Start(psi); }
            catch (Exception ex) { Status("无法打开：" + ex.Message); }
        }

        void RemoveItem(GroupItem it)
        {
            GroupStore.Remove(it);
            AfterChange("已把「" + it.Name + "」移到回收站。");
        }

        void Move(GroupItem it, int delta)
        {
            Group g = GroupStore.Load(_cur.Name);
            int i = g.Items.FindIndex(delegate (GroupItem x) { return x.FileName == it.FileName; });
            int j = i + delta;
            if (i < 0 || j < 0 || j >= g.Items.Count) return;
            GroupItem tmp = g.Items[i]; g.Items[i] = g.Items[j]; g.Items[j] = tmp;
            GroupStore.SaveOrder(g);
            if (g.Sort != "manual") GroupStore.SetSort(g, "manual");
            AfterChange(null);
        }

        void RenameItem(GroupItem it)
        {
            string n = MacAlert.Prompt(this, "重命名", "给「" + it.Name + "」起个新名字。", it.Name, "重命名");
            if (string.IsNullOrEmpty(n) || n == it.Name) return;
            string ext = it.IsFolder ? "" : System.IO.Path.GetExtension(it.FileName);
            string keepExt = (ext.Length > 0 && System.IO.Path.GetFileNameWithoutExtension(it.FileName) == it.Name) ? ext : "";
            string safe = GroupStore.SafeFileName(n);
            bool caseOnly = string.Equals(safe + keepExt, it.FileName, StringComparison.OrdinalIgnoreCase);
            string dest = caseOnly ? System.IO.Path.Combine(_cur.Dir, safe + keepExt) : GroupStore.UniquePath(_cur.Dir, safe, keepExt);
            Group g = GroupStore.Load(_cur.Name, false); // keep the hand-arranged order
            try
            {
                string src = it.Path;
                if (caseOnly)
                {
                    string tmp = src + ".~rename";
                    if (it.IsFolder) Directory.Move(src, tmp); else File.Move(src, tmp);
                    src = tmp;
                }
                if (it.IsFolder) Directory.Move(src, dest); else File.Move(src, dest);
            }
            catch (Exception ex)
            {
                MacAlert.Info(this, "无法重命名", ex.Message);
                AfterChange(null);
                return;
            }
            foreach (GroupItem x in g.Items) if (x.FileName == it.FileName) x.FileName = System.IO.Path.GetFileName(dest);
            GroupStore.SaveOrder(g);
            AfterChange(null);
        }

        void AfterChange(string status)
        {
            if (status != null) Status(status);
            Reload();
            try { if (_cur != null) Integration.RefreshIcon(GroupStore.Load(_cur.Name)); } catch { }
        }

        // ================================================================ drag & drop

        void Ring(bool on)
        {
            _dropRing.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 120 : 180)));
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            bool inner = e.Data.GetDataPresent("DockFolderItem");
            bool outer = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array");
            if (_cur == null) e.Effects = DragDropEffects.None;
            else if (inner) e.Effects = DragDropEffects.Move;
            else if (outer) e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
            else e.Effects = DragDropEffects.None;
            if (outer && _cur != null && _dropRing.Opacity < 0.5) Ring(true);
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            Ring(false);
            if (_cur == null) return;
            e.Handled = true;
            if (e.Data.GetDataPresent("DockFolderItem"))
            {
                string file = (string)e.Data.GetData("DockFolderItem");
                GroupItem target = null;
                DependencyObject o = e.OriginalSource as DependencyObject;
                while (o != null)
                {
                    FrameworkElement fe = o as FrameworkElement;
                    if (fe != null && fe.Tag is Tile) { target = ((Tile)fe.Tag).Item; break; }
                    o = VisualTreeHelper.GetParent(o);
                }
                Group g = GroupStore.Load(_cur.Name);
                int from = g.Items.FindIndex(delegate (GroupItem x) { return x.FileName == file; });
                if (from < 0) return;
                GroupItem moving = g.Items[from];
                int targetIdx = target == null ? -1 : g.Items.FindIndex(delegate (GroupItem x) { return x.FileName == target.FileName; });
                if (targetIdx == from) return;
                g.Items.RemoveAt(from);
                int to = target == null ? g.Items.Count : g.Items.FindIndex(delegate (GroupItem x) { return x.FileName == target.FileName; });
                if (to < 0) to = g.Items.Count;
                else if (from < targetIdx) to++; // dragged forward: land after the target, not back where it was
                g.Items.Insert(to, moving);
                GroupStore.SaveOrder(g);
                if (g.Sort != "manual") GroupStore.SetSort(g, "manual");
                AfterChange(null);
                return;
            }

            List<string> paths = new List<string>();
            int added = 0;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) paths.AddRange(files);
            }
            else if (e.Data.GetDataPresent("Shell IDList Array"))
            {
                MemoryStream ms = e.Data.GetData("Shell IDList Array") as MemoryStream;
                if (ms != null)
                {
                    List<string> created = Shortcut.FromIdListArray(ms.ToArray(), _cur.Dir, paths);
                    foreach (string c in created) { GroupStore.AppendOrder(_cur, System.IO.Path.GetFileName(c)); added++; }
                }
            }
            foreach (string p in paths) if (GroupStore.AddPath(_cur, p) != null) added++;
            AfterChange(added > 0 ? "已添加 " + added + " 项。" : "没有添加新项目（可能已经在这个文件夹里了）。");
        }

        // ================================================================ commands

        void AddMenu(FrameworkElement anchor)
        {
            if (_cur == null) return;
            List<MacMenuItem> m = new List<MacMenuItem>();
            m.Add(MacMenuItem.Item("添加应用程序…", AddPrograms));
            m.Add(MacMenuItem.Item("添加文件夹…", AddFolder));
            m.Add(MacMenuItem.Sep());
            m.Add(MacMenuItem.Item("从「所有应用」拖入…", AddFromApps));
            Point top = anchor.PointToScreen(new Point(0, 0));
            Native.POINT pt = new Native.POINT();
            pt.X = (int)top.X;
            pt.Y = (int)top.Y - 6; // the footer is at the bottom: open upwards
            MacMenu.Show(m, pt, MacMenu.PlaceAboveLeft, null, P);
        }

        void NewGroup()
        {
            string n = MacAlert.Prompt(this, "新建文件夹", "给它起个名字，比如「设计」「开发」「游戏」。", "新文件夹", "创建");
            if (string.IsNullOrEmpty(n)) return;
            Group g = GroupStore.CreateGroup(n);
            ShowGroup(g.Name);
            Status("已创建「" + g.Name + "」。把应用拖进来，然后点「固定到任务栏」。");
        }

        void RenameGroup()
        {
            if (_cur == null) return;
            string old = _cur.Name;
            string n = MacAlert.Prompt(this, "重命名文件夹", null, old, "重命名");
            if (string.IsNullOrEmpty(n) || n == old) return;
            bool hadShortcuts = Integration.HasShortcuts(old);
            n = GroupStore.SafeFileName(n);
            try
            {
                if (!GroupStore.RenameGroup(old, n)) { MacAlert.Info(this, "名字重复了", "已经有叫「" + n + "」的文件夹，换一个试试。"); return; }
            }
            catch (Exception ex)
            {
                MacAlert.Info(this, "无法重命名", "可能有文件正被占用（比如在资源管理器里打开着）。\n" + ex.Message);
                return;
            }
            _cur = GroupStore.Load(n);
            if (hadShortcuts)
            {
                Integration.RemoveShortcuts(old);
                try { Integration.CreateShortcuts(_cur); }
                catch (Exception ex) { MacAlert.Info(this, "快捷方式没有更新", ex.Message); ShowGroup(n); return; }
                MacAlert.Info(this, "需要重新固定到任务栏",
                    "桌面和开始菜单里的快捷方式已更新。任务栏上旧的「" + old + "」按钮已失效，请取消固定后，把新的「" + n + "」固定上去。");
            }
            ShowGroup(n);
        }

        void DeleteGroup()
        {
            if (_cur == null) return;
            if (!MacAlert.Confirm(this, "删除「" + _cur.Name + "」？", "里面的快捷方式会移到回收站，不会卸载任何程序。", "删除", true)) return;
            string name = _cur.Name;
            try { GroupStore.DeleteGroup(name); } // the folder first: if this fails, the shortcuts still work
            catch (Exception ex)
            {
                if (GroupStore.Exists(name))
                {
                    MacAlert.Info(this, "没有删除", "可能有文件正被占用（比如在资源管理器里打开着）。\n" + ex.Message);
                    return;
                }
            }
            Integration.RemoveShortcuts(name);
            _cur = null;
            Reload();
            Status("已删除。如果它固定在任务栏上，按住 Shift 右键那个按钮 → 从任务栏取消固定。");
        }

        void AddPrograms()
        {
            if (_cur == null) return;
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择要放进「" + _cur.Name + "」的应用";
            dlg.Multiselect = true;
            dlg.DereferenceLinks = false;
            dlg.Filter = "应用和快捷方式|*.exe;*.lnk;*.url;*.bat;*.cmd;*.appref-ms|所有文件|*.*";
            dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (dlg.ShowDialog(this) != true) return;
            int n = 0;
            foreach (string f in dlg.FileNames) if (GroupStore.AddPath(_cur, f) != null) n++;
            AfterChange("已添加 " + n + " 项。");
        }

        void AddFolder()
        {
            if (_cur == null) return;
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择要放进「" + _cur.Name + "」的文件夹";
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (GroupStore.AddPath(_cur, dlg.SelectedPath) != null) AfterChange("已添加文件夹。");
            }
        }

        void AddFromApps()
        {
            try { Process.Start("explorer.exe", "shell:AppsFolder"); } catch { }
            Status("已打开「应用程序」窗口，把需要的应用拖到这里即可（商店应用也可以）。");
        }

        void Preview()
        {
            if (_cur == null) return;
            // PointToScreen is right even when maximised (Left/Top then hold the restore position)
            Point c = PointToScreen(new Point(SidebarW + (ActualWidth - SidebarW) / 2, ActualHeight / 2));
            Native.POINT pt = new Native.POINT();
            pt.X = (int)c.X;
            pt.Y = (int)c.Y;
            Host.Current.Preview(_cur, pt);
        }

        void CreateShortcut()
        {
            if (_cur == null) return;
            string desk;
            try { desk = Integration.CreateShortcuts(_cur); }
            catch (Exception ex) { MacAlert.Info(this, "没能创建快捷方式", ex.Message); return; }
            string name = System.IO.Path.GetFileNameWithoutExtension(desk);
            MacAlert.Info(this, "已创建「" + name + "」",
                "右键桌面上的「" + name + "」→ 固定到任务栏（Windows 11 若没有这一项，先点「显示更多选项」）。固定好后，桌面上的这个快捷方式可以删除。");
            Host.SelectInExplorer(desk);
        }
    }
}
