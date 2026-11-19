using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DockFolder
{
    /// <summary>Colours that follow the taskbar's light/dark setting.</summary>
    internal class Theme
    {
        public bool Light;
        public Brush PanelBg, PanelStroke, Text, SubText, Hover, Pressed, PillBg, PillStroke, PillHover, PillText;
        public double ShadowOpacity;

        static Brush B(uint argb)
        {
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        public static Theme Current()
        {
            bool light = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("SystemUsesLightTheme");
                        if (v is int) light = (int)v != 0;
                    }
                }
            }
            catch { }
            Theme t = new Theme();
            t.Light = light;
            if (light)
            {
                t.PanelBg = B(0xF4F7F7F9); t.PanelStroke = B(0x2A000000);
                t.Text = B(0xE6141414); t.SubText = B(0x99000000);
                t.Hover = B(0x16000000); t.Pressed = B(0x28000000);
                t.PillBg = B(0xF2FBFBFB); t.PillStroke = B(0x24000000); t.PillHover = B(0xFFFFFFFF); t.PillText = B(0xFF1A1A1A);
                t.ShadowOpacity = 0.26;
            }
            else
            {
                t.PanelBg = B(0xF2242427); t.PanelStroke = B(0x3DFFFFFF);
                t.Text = B(0xF2FFFFFF); t.SubText = B(0xA6FFFFFF);
                t.Hover = B(0x1FFFFFFF); t.Pressed = B(0x36FFFFFF);
                t.PillBg = B(0xE81E1E21); t.PillStroke = B(0x33FFFFFF); t.PillHover = B(0xFF3A3A3F); t.PillText = B(0xFFF5F5F5);
                t.ShadowOpacity = 0.45;
            }
            return t;
        }
    }

    /// <summary>Damped-spring easing (the "boing" of the macOS Dock).</summary>
    internal class SpringEase : EasingFunctionBase
    {
        public static readonly DependencyProperty DampingProperty =
            DependencyProperty.Register("Damping", typeof(double), typeof(SpringEase), new PropertyMetadata(0.7));

        public double Damping
        {
            get { return (double)GetValue(DampingProperty); }
            set { SetValue(DampingProperty, value); }
        }

        public SpringEase() { EasingMode = EasingMode.EaseIn; }
        public SpringEase(double damping) : this() { Damping = damping; }

        static double Raw(double t, double z)
        {
            double w = 4.8 / z;
            double wd = w * Math.Sqrt(1 - z * z);
            return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
        }

        protected override double EaseInCore(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double z = Math.Max(0.2, Math.Min(0.95, Damping));
            return Raw(t, z) + (1 - Raw(1, z)) * t;
        }

        protected override Freezable CreateInstanceCore() { return new SpringEase(); }
    }

    /// <summary>
    /// The pop-up "stack". Fan layout (macOS Dock "Fan"): icons arc up out of the taskbar button with
    /// labels beside them. Grid layout (macOS "Grid" / iOS folder): a rounded panel that zooms out of
    /// the button. Clicking anywhere else, Esc, or clicking the button again folds it back.
    /// </summary>
    internal class PopupWindow : Window
    {
        // fan
        const double FanIcon = 54, FanSpacing = 64, FanLabelMax = 240, FanLabelGap = 12, FanRadius = 1500;
        const int FanMax = 10;
        // grid
        const double CellW = 98, CellH = 102, GridIcon = 54, Pad = 14, TitleH = 42, FootH = 38, ArrowW = 26, ArrowH = 12, Radius = 16;

        enum State { Hidden, Opening, Open, Closing }
        enum Edge { None, Bottom, Top, Left, Right }

        class FanEntry
        {
            public FrameworkElement El, Label;
            public ScaleTransform Scale;
            public RotateTransform Rot;
            public TranslateTransform Move;
            public double Angle;
            public Point Start;
        }

        readonly Canvas _root;
        IntPtr _hwnd;
        State _state = State.Hidden;
        Group _group;
        Theme _theme;
        double _dpi = 1;
        Edge _edge;
        bool _fromTaskbar, _fan, _mirror, _lastPreview;
        Native.POINT _lastPt;
        Rect _area, _taskbarDip, _buttonPx = Rect.Empty;
        Point _origin, _anchor;
        readonly List<FanEntry> _fanEntries = new List<FanEntry>();
        FrameworkElement _gridHost;
        ScaleTransform _gridScale;
        int _gridCols = 1;
        readonly List<FrameworkElement> _gridTiles = new List<FrameworkElement>();
        int _seq;
        string _toggleGroup;
        DateTime _toggleAt;
        Native.LowLevelProc _hookProc;
        IntPtr _hook;
        readonly uint _pid;
        static readonly Dictionary<string, Rect> _buttonCache = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);

        public PopupWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "任务栏文件夹";
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;
            Left = -30000; Top = -30000; Width = 10; Height = 10;
            _root = new Canvas();
            Content = _root;
            _pid = (uint)Process.GetCurrentProcess().Id;

            SourceInitialized += delegate
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
            };
            Deactivated += delegate { OnDeactivated(); };
            PreviewKeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; BeginClose(); } };
            Closing += delegate (object s, CancelEventArgs e) { e.Cancel = true; BeginClose(); };
        }

        // ================================================================ public API

        public void Toggle(Group g, Native.POINT pt, bool preview)
        {
            bool same = _group != null && string.Equals(_group.Name, g.Name, StringComparison.OrdinalIgnoreCase);
            if (_state == State.Opening || _state == State.Open)
            {
                if (same && !preview) { BeginClose(); return; }
                CloseNow();
            }
            // The click on the taskbar button that just folded the stack also launches us again: swallow it.
            if (!preview && _toggleGroup != null && string.Equals(_toggleGroup, g.Name, StringComparison.OrdinalIgnoreCase)
                && (DateTime.Now - _toggleAt).TotalMilliseconds < 1600)
            {
                _toggleGroup = null;
                return;
            }
            _toggleGroup = null;
            Open(g, pt, preview);
        }

        public void CloseNow()
        {
            if (_state == State.Hidden) return;
            _seq++;
            RemoveHook();
            _root.Children.Clear();
            _fanEntries.Clear();
            _gridTiles.Clear();
            _gridHost = null;
            _state = State.Hidden;
            Hide();
        }

        public void BeginClose()
        {
            if (_state != State.Opening && _state != State.Open) return;
            _state = State.Closing;
            RemoveHook();
            int seq = _seq;
            double total = _fan ? AnimateFanClose() : AnimateGridClose();
            After(total + 30, delegate { if (seq == _seq && _state == State.Closing) CloseNow(); });
        }

        /// <summary>Centre (px) of the group's taskbar button, if it has been clicked before.</summary>
        public static bool TryGetButtonCenter(string group, out Native.POINT pt)
        {
            pt = new Native.POINT();
            Rect r;
            if (!_buttonCache.TryGetValue(group, out r)) return false;
            pt.X = (int)(r.Left + r.Width / 2);
            pt.Y = (int)(r.Top + r.Height / 2);
            return true;
        }

        public static void WarmUpAutomation()
        {
            Thread t = new Thread(delegate ()
            {
                try { string n = AutomationElement.RootElement.Current.Name; } catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        // ================================================================ open

        void Open(Group g, Native.POINT pt, bool preview)
        {
            CloseNow();
            _seq++;
            _group = g;
            _lastPt = pt;
            _lastPreview = preview;
            _theme = Theme.Current();
            _dpi = Native.SystemDpiScale();
            Place(pt, preview);

            int n = g.Items.Count;
            bool wantFan = g.View == "fan" || (g.View == "auto" && n <= FanMax);
            _fan = wantFan && n > 0 && _fromTaskbar && _edge == Edge.Bottom && FanFits(n + 1);

            Rect bounds = _fan ? BuildFan() : BuildGrid();
            foreach (UIElement c in _root.Children)
            {
                Canvas.SetLeft(c, Canvas.GetLeft(c) - bounds.X);
                Canvas.SetTop(c, Canvas.GetTop(c) - bounds.Y);
            }
            Left = bounds.X; Top = bounds.Y; Width = bounds.Width; Height = bounds.Height;
            _root.Width = bounds.Width; _root.Height = bounds.Height;

            _state = State.Opening;
            Show();
            BringToFront();
            InstallHook();
            double total = _fan ? AnimateFanOpen() : AnimateGridOpen();
            int seq = _seq;
            After(total, delegate { if (seq == _seq && _state == State.Opening) _state = State.Open; });
        }

        Rect Dip(Native.RECT r)
        {
            return new Rect(r.Left / _dpi, r.Top / _dpi, (r.Right - r.Left) / _dpi, (r.Bottom - r.Top) / _dpi);
        }

        void Place(Native.POINT pt, bool preview)
        {
            IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            Rect monR = Dip(mi.rcMonitor), work = Dip(mi.rcWork);
            Point cur = new Point(pt.X / _dpi, pt.Y / _dpi);

            _edge = Edge.None;
            Rect tb = Rect.Empty;
            if (work.Bottom < monR.Bottom - 1) { _edge = Edge.Bottom; tb = new Rect(monR.Left, work.Bottom, monR.Width, monR.Bottom - work.Bottom); }
            else if (work.Top > monR.Top + 1) { _edge = Edge.Top; tb = new Rect(monR.Left, monR.Top, monR.Width, work.Top - monR.Top); }
            else if (work.Left > monR.Left + 1) { _edge = Edge.Left; tb = new Rect(monR.Left, monR.Top, work.Left - monR.Left, monR.Height); }
            else if (work.Right < monR.Right - 1) { _edge = Edge.Right; tb = new Rect(work.Right, monR.Top, monR.Right - work.Right, monR.Height); }
            else
            {
                // auto-hide taskbar: ask the shell where it is
                Native.APPBARDATA abd = new Native.APPBARDATA();
                abd.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
                if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
                {
                    Rect r = Dip(abd.rc);
                    if (r.IntersectsWith(monR))
                    {
                        tb = Rect.Intersect(r, monR);
                        switch (abd.uEdge)
                        {
                            case 0: _edge = Edge.Left; work = new Rect(tb.Right, work.Top, Math.Max(1, work.Right - tb.Right), work.Height); break;
                            case 1: _edge = Edge.Top; work = new Rect(work.Left, tb.Bottom, work.Width, Math.Max(1, work.Bottom - tb.Bottom)); break;
                            case 2: _edge = Edge.Right; work = new Rect(work.Left, work.Top, Math.Max(1, tb.Left - work.Left), work.Height); break;
                            default: _edge = Edge.Bottom; work = new Rect(work.Left, work.Top, work.Width, Math.Max(1, tb.Top - work.Top)); break;
                        }
                    }
                }
            }
            _area = work;
            _taskbarDip = tb;

            Rect hit = tb;
            if (!hit.IsEmpty) hit.Inflate(2, 2);
            _fromTaskbar = !tb.IsEmpty && (preview || hit.Contains(cur));
            _buttonPx = Rect.Empty;

            if (_fromTaskbar && !preview)
            {
                Rect btn = FindTaskbarButton(_group.Name, pt);
                if (!btn.IsEmpty)
                {
                    _buttonPx = btn;
                    cur = new Point((btn.Left + btn.Width / 2) / _dpi, (btn.Top + btn.Height / 2) / _dpi);
                }
            }
            if (_fromTaskbar && preview)
            {
                if (_edge == Edge.Bottom || _edge == Edge.Top) cur = new Point(cur.X, tb.Top + tb.Height / 2);
                else cur = new Point(tb.Left + tb.Width / 2, cur.Y);
            }

            _origin = cur;
            const double gap = 6;
            if (!_fromTaskbar) _anchor = cur;
            else if (_edge == Edge.Bottom) _anchor = new Point(cur.X, tb.Top - gap);
            else if (_edge == Edge.Top) _anchor = new Point(cur.X, tb.Bottom + gap);
            else if (_edge == Edge.Left) _anchor = new Point(tb.Right + gap, cur.Y);
            else _anchor = new Point(tb.Left - gap, cur.Y);
        }

        class RectBox { public Rect R = Rect.Empty; public string Name; }

        /// <summary>Exact rectangle (px) of the taskbar button under the cursor, via UI Automation.</summary>
        static Rect FindTaskbarButton(string group, Native.POINT pt)
        {
            Rect cached;
            if (_buttonCache.TryGetValue(group, out cached) && cached.Contains(pt.X, pt.Y)) return cached;

            RectBox box = new RectBox();
            Thread t = new Thread(delegate ()
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
                        if (buttonish && !r.IsEmpty && r.Width >= 16 && r.Height >= 16 && r.Width <= 360 && r.Height <= 220 && r.Contains(p))
                        {
                            lock (box) { box.R = r; box.Name = info.Name; }
                            break;
                        }
                        el = TreeWalker.RawViewWalker.GetParent(el);
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(170);
            Rect res;
            string caption;
            lock (box) { res = box.R; caption = box.Name; }
            // only trust it if the button under the cursor really is this folder's (Win+number launches
            // can happen while the mouse rests on some other app's button)
            if (!res.IsEmpty && Host.Current != null && !Host.Current.IsTaskbarButtonOf(caption, group)) return Rect.Empty;
            if (!res.IsEmpty) _buttonCache[group] = res;
            return res;
        }

        // ================================================================ fan

        bool FanFits(int m)
        {
            double top = _anchor.Y - FanIcon / 2 - 4 - (m - 1) * FanSpacing - FanIcon / 2;
            return top >= _area.Top + 8;
        }

        Rect BuildFan()
        {
            _fanEntries.Clear();
            double W = FanLabelMax + FanLabelGap + FanIcon;
            _mirror = _anchor.X - W + FanIcon / 2 < _area.Left + 4;
            int n = _group.Items.Count, m = n + 1;
            Rect bounds = new Rect(_origin, _origin);

            for (int i = 0; i < m; i++)
            {
                double y = i * FanSpacing;
                double dx = FanRadius - Math.Sqrt(FanRadius * FanRadius - y * y);
                double ang = Math.Asin(y / FanRadius) * 180 / Math.PI * 0.85;
                double cx = _anchor.X + (_mirror ? -dx : dx);
                double cy = _anchor.Y - FanIcon / 2 - 4 - y;
                GroupItem it = i < n ? _group.Items[i] : null;

                FrameworkElement label;
                FrameworkElement el = BuildFanItem(it, out label);
                double left = _mirror ? cx - FanIcon / 2 : cx + FanIcon / 2 - W;
                double top = cy - FanIcon / 2;
                el.Width = W; el.Height = FanIcon;
                Canvas.SetLeft(el, left);
                Canvas.SetTop(el, top);
                el.RenderTransformOrigin = new Point(_mirror ? (FanIcon / 2) / W : (W - FanIcon / 2) / W, 0.5);

                FanEntry fe = new FanEntry();
                fe.El = el; fe.Label = label;
                fe.Angle = _mirror ? -ang : ang;
                fe.Start = new Point(_origin.X - cx, _origin.Y - cy);
                fe.Scale = new ScaleTransform(0.3, 0.3);
                fe.Rot = new RotateTransform(0);
                fe.Move = new TranslateTransform(fe.Start.X, fe.Start.Y);
                TransformGroup tg = new TransformGroup();
                tg.Children.Add(fe.Scale); tg.Children.Add(fe.Rot); tg.Children.Add(fe.Move);
                el.RenderTransform = tg;
                el.Opacity = 0;
                label.Opacity = 0;
                _root.Children.Add(el);
                _fanEntries.Add(fe);

                bounds.Union(RotBounds(new Rect(left, top, W, FanIcon), new Point(cx, cy), fe.Angle));
            }
            bounds.Inflate(36, 36);
            return bounds;
        }

        FrameworkElement BuildFanItem(GroupItem it, out FrameworkElement label)
        {
            Grid g = new Grid();

            Border pill = new Border();
            pill.CornerRadius = new CornerRadius(8);
            pill.Background = _theme.PillBg;
            pill.BorderBrush = _theme.PillStroke;
            pill.BorderThickness = new Thickness(1);
            pill.Padding = new Thickness(10, 3, 10, 4);
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.HorizontalAlignment = _mirror ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            pill.Margin = _mirror ? new Thickness(FanIcon + FanLabelGap, 0, 0, 0) : new Thickness(0, 0, FanIcon + FanLabelGap, 0);
            TextBlock tb = new TextBlock();
            tb.Text = it != null ? it.Name : "在资源管理器中打开";
            tb.FontSize = 13;
            tb.Foreground = _theme.PillText;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.MaxWidth = FanLabelMax - 24;
            pill.Child = tb;

            Grid iconBox = new Grid();
            iconBox.Width = FanIcon; iconBox.Height = FanIcon;
            iconBox.HorizontalAlignment = _mirror ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            iconBox.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // whole square clickable
            FrameworkElement icon;
            if (it != null)
            {
                Image img = new Image();
                img.Source = IconCache.Get(it.Path);
                img.Width = FanIcon; img.Height = FanIcon;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                icon = img;
            }
            else icon = Glyph("", FanIcon * 0.7);
            ScaleTransform hover = new ScaleTransform(1, 1);
            icon.RenderTransform = hover;
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            iconBox.Children.Add(icon);

            g.Children.Add(pill);
            g.Children.Add(iconBox);
            label = pill;

            bool pressed = false;
            g.Cursor = Cursors.Hand;
            g.MouseEnter += delegate
            {
                pill.Background = _theme.PillHover;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 1.14, 160, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 1.14, 160, 0, Out());
            };
            g.MouseLeave += delegate
            {
                pressed = false;
                pill.Background = _theme.PillBg;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 1, 200, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 1, 200, 0, Out());
            };
            g.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                pressed = true;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 0.92, 90, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 0.92, 90, 0, Out());
            };
            g.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!pressed) return;
                pressed = false;
                if (it != null) Launch(it); else OpenFolder();
            };
            g.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowMenu(g, it);
            };
            return g;
        }

        FrameworkElement Glyph(string glyph, double size)
        {
            Border c = new Border();
            c.Width = size; c.Height = size;
            c.CornerRadius = new CornerRadius(size / 2);
            c.Background = _theme.PillBg;
            c.BorderBrush = _theme.PillStroke;
            c.BorderThickness = new Thickness(1);
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            t.FontSize = size * 0.42;
            t.Foreground = _theme.PillText;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            c.Child = t;
            return c;
        }

        double AnimateFanOpen()
        {
            double total = 0;
            for (int i = 0; i < _fanEntries.Count; i++)
            {
                FanEntry e = _fanEntries[i];
                double d = i * 24;
                Anim(e.Move, TranslateTransform.XProperty, e.Start.X, 0, 470, d, new SpringEase(0.8));
                Anim(e.Move, TranslateTransform.YProperty, e.Start.Y, 0, 470, d, new SpringEase(0.8));
                Anim(e.Scale, ScaleTransform.ScaleXProperty, 0.3, 1, 470, d, new SpringEase(0.6));
                Anim(e.Scale, ScaleTransform.ScaleYProperty, 0.3, 1, 470, d, new SpringEase(0.6));
                Anim(e.Rot, RotateTransform.AngleProperty, 0, e.Angle, 470, d, new SpringEase(0.8));
                Anim(e.El, UIElement.OpacityProperty, 0, 1, 130, d, null);
                Anim(e.Label, UIElement.OpacityProperty, 0, 1, 180, d + 190, null);
                total = Math.Max(total, d + 470);
            }
            return total;
        }

        double AnimateFanClose()
        {
            double total = 0;
            int m = _fanEntries.Count;
            for (int i = 0; i < m; i++)
            {
                FanEntry e = _fanEntries[i];
                double d = (m - 1 - i) * 12;
                IEasingFunction ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                SnapTo(e.Move, TranslateTransform.XProperty, e.Start.X, 230, d, ease);
                SnapTo(e.Move, TranslateTransform.YProperty, e.Start.Y, 230, d, ease);
                SnapTo(e.Scale, ScaleTransform.ScaleXProperty, 0.3, 230, d, ease);
                SnapTo(e.Scale, ScaleTransform.ScaleYProperty, 0.3, 230, d, ease);
                SnapTo(e.Rot, RotateTransform.AngleProperty, 0, 230, d, ease);
                SnapTo(e.El, UIElement.OpacityProperty, 0, 190, d + 40, new CubicEase { EasingMode = EasingMode.EaseIn });
                SnapTo(e.Label, UIElement.OpacityProperty, 0, 80, 0, null);
                total = Math.Max(total, d + 230);
            }
            return total;
        }

        // ================================================================ grid

        static int ChooseCols(int n)
        {
            if (n <= 4) return Math.Max(n, 1);
            if (n <= 6) return 3;
            if (n <= 8) return 4;
            if (n == 9) return 3;
            if (n <= 12) return 4;
            if (n <= 20) return 5;
            if (n <= 30) return 6;
            return 7;
        }

        Rect BuildGrid()
        {
            _gridTiles.Clear();
            int n = _group.Items.Count;
            int cols = n == 0 ? 3 : ChooseCols(n);
            int maxCols = Math.Max(1, (int)((_area.Width - 24 - 2 * Pad) / CellW));
            if (cols > maxCols) cols = maxCols;
            _gridCols = cols;
            int rows = n == 0 ? 1 : (n + cols - 1) / cols;

            bool arrow = _fromTaskbar && _edge != Edge.None;
            double availH;
            if (arrow && _edge == Edge.Bottom) availH = _anchor.Y - ArrowH - _area.Top - 12;
            else if (arrow && _edge == Edge.Top) availH = _area.Bottom - _anchor.Y - ArrowH - 12;
            else availH = _area.Height - 24;
            int maxRows = Math.Max(1, (int)((availH - TitleH - FootH) / CellH));
            int visRows = Math.Min(rows, maxRows);

            double contentW = cols * CellW, contentH = visRows * CellH;
            double pw = contentW + 2 * Pad, ph = TitleH + contentH + FootH;

            double px, py;
            if (arrow && _edge == Edge.Bottom) { py = _anchor.Y - ArrowH - ph; px = Clamp(_anchor.X - pw / 2, _area.Left + 8, _area.Right - 8 - pw); }
            else if (arrow && _edge == Edge.Top) { py = _anchor.Y + ArrowH; px = Clamp(_anchor.X - pw / 2, _area.Left + 8, _area.Right - 8 - pw); }
            else if (arrow && _edge == Edge.Left) { px = _anchor.X + ArrowH; py = Clamp(_anchor.Y - ph / 2, _area.Top + 8, _area.Bottom - 8 - ph); }
            else if (arrow && _edge == Edge.Right) { px = _anchor.X - ArrowH - pw; py = Clamp(_anchor.Y - ph / 2, _area.Top + 8, _area.Bottom - 8 - ph); }
            else { px = Clamp(_anchor.X - pw / 2, _area.Left + 8, _area.Right - 8 - pw); py = Clamp(_anchor.Y - ph / 2, _area.Top + 8, _area.Bottom - 8 - ph); }

            Rect panel = new Rect(px, py, pw, ph);
            Rect host = panel;
            if (arrow) host.Union(_anchor);
            Rect lp = new Rect(panel.X - host.X, panel.Y - host.Y, pw, ph);

            // ---- shape: rounded rect + arrow, one outline
            Geometry shapeGeo = new RectangleGeometry(lp, Radius, Radius);
            Point tipL = new Point(_anchor.X - host.X, _anchor.Y - host.Y);
            Point origin = tipL;
            if (arrow)
            {
                Vector u, v;
                Point c;
                double inset = Radius + ArrowW / 2;
                if (_edge == Edge.Bottom || _edge == Edge.Top)
                {
                    double bx = Clamp(tipL.X, lp.Left + inset, lp.Right - inset);
                    u = new Vector(0, _edge == Edge.Bottom ? 1 : -1); v = new Vector(1, 0);
                    c = new Point(bx, _edge == Edge.Bottom ? lp.Bottom - 0.5 : lp.Top + 0.5);
                    tipL = new Point(bx, tipL.Y);
                }
                else
                {
                    double by = Clamp(tipL.Y, lp.Top + inset, lp.Bottom - inset);
                    u = new Vector(_edge == Edge.Right ? 1 : -1, 0); v = new Vector(0, 1);
                    c = new Point(_edge == Edge.Right ? lp.Right - 0.5 : lp.Left + 0.5, by);
                    tipL = new Point(tipL.X, by);
                }
                Point T = c + u * (ArrowH + 0.5);
                PathFigure f = new PathFigure();
                f.StartPoint = c - v * (ArrowW / 2) - u * 1;
                f.Segments.Add(new LineSegment(T - u * 2.0 - v * 2.6, true));
                f.Segments.Add(new QuadraticBezierSegment(T + u * 0.4, T - u * 2.0 + v * 2.6, true));
                f.Segments.Add(new LineSegment(c + v * (ArrowW / 2) - u * 1, true));
                f.IsClosed = true;
                PathGeometry tri = new PathGeometry();
                tri.Figures.Add(f);
                shapeGeo = new CombinedGeometry(GeometryCombineMode.Union, shapeGeo, tri);
                origin = T;
            }

            Grid hostEl = new Grid();
            hostEl.Width = host.Width; hostEl.Height = host.Height;
            System.Windows.Shapes.Path shape = new System.Windows.Shapes.Path();
            shape.Data = shapeGeo;
            shape.Fill = _theme.PanelBg;
            shape.Stroke = _theme.PanelStroke;
            shape.StrokeThickness = 1;
            DropShadowEffect sh = new DropShadowEffect();
            sh.BlurRadius = 28; sh.ShadowDepth = 5; sh.Direction = 270; sh.Opacity = _theme.ShadowOpacity; sh.Color = Colors.Black;
            shape.Effect = sh;
            hostEl.Children.Add(shape);
            shape.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(shape, null); };

            // ---- content
            DockPanel content = new DockPanel();
            content.Width = pw; content.Height = ph;
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;
            content.Margin = new Thickness(lp.X, lp.Y, 0, 0);
            content.LastChildFill = true;

            TextBlock title = new TextBlock();
            title.Text = _group.Name;
            title.FontSize = 14.5;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = _theme.Text;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(Pad, 4, Pad, 0);
            Border titleBox = new Border();
            titleBox.Height = TitleH;
            titleBox.Child = title;
            DockPanel.SetDock(titleBox, Dock.Top);
            content.Children.Add(titleBox);

            StackPanel foot = new StackPanel();
            foot.Orientation = Orientation.Horizontal;
            foot.HorizontalAlignment = HorizontalAlignment.Center;
            foot.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(Link("在资源管理器中打开", OpenFolder));
            TextBlock dot = new TextBlock();
            dot.Text = "   ·   "; dot.FontSize = 12; dot.Foreground = _theme.SubText;
            foot.Children.Add(dot);
            foot.Children.Add(Link("管理…", Manage));
            Border footBox = new Border();
            footBox.Height = FootH;
            footBox.Child = foot;
            DockPanel.SetDock(footBox, Dock.Bottom);
            content.Children.Add(footBox);

            if (n == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "这个文件夹还是空的\n点下面的「管理…」添加程序";
                empty.TextAlignment = TextAlignment.Center;
                empty.Foreground = _theme.SubText;
                empty.FontSize = 13;
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(empty);
            }
            else
            {
                WrapPanel wrap = new WrapPanel();
                wrap.Width = contentW;
                wrap.ItemWidth = CellW;
                wrap.ItemHeight = CellH;
                foreach (GroupItem it in _group.Items)
                {
                    FrameworkElement tile = MakeTile(it);
                    wrap.Children.Add(tile);
                    _gridTiles.Add(tile);
                }
                ScrollViewer sv = new ScrollViewer();
                sv.VerticalScrollBarVisibility = rows > visRows ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                sv.Height = contentH;
                sv.Margin = new Thickness(Pad, 0, Pad, 0);
                sv.Content = wrap;
                sv.Focusable = false;
                content.Children.Add(sv);
            }
            hostEl.Children.Add(content);

            _gridScale = new ScaleTransform(0.06, 0.06);
            hostEl.RenderTransform = _gridScale;
            hostEl.RenderTransformOrigin = new Point(origin.X / host.Width, origin.Y / host.Height);
            hostEl.Opacity = 0;
            Canvas.SetLeft(hostEl, host.X);
            Canvas.SetTop(hostEl, host.Y);
            _root.Children.Add(hostEl);
            _gridHost = hostEl;

            Rect bounds = host;
            bounds.Union(new Point(_origin.X, _origin.Y));
            bounds.Inflate(46, 46);
            return bounds;
        }

        FrameworkElement Link(string text, Action click)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.Foreground = _theme.SubText;
            t.Cursor = Cursors.Hand;
            t.Background = Brushes.Transparent;
            t.MouseEnter += delegate { t.Foreground = _theme.Text; t.TextDecorations = TextDecorations.Underline; };
            t.MouseLeave += delegate { t.Foreground = _theme.SubText; t.TextDecorations = null; };
            t.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return t;
        }

        FrameworkElement MakeTile(GroupItem it)
        {
            Border b = new Border();
            b.Margin = new Thickness(3);
            b.CornerRadius = new CornerRadius(10);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;

            StackPanel sp = new StackPanel();
            Image img = new Image();
            img.Source = IconCache.Get(it.Path);
            img.Width = GridIcon; img.Height = GridIcon;
            img.Margin = new Thickness(0, 9, 0, 0);
            img.HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            ScaleTransform hover = new ScaleTransform(1, 1);
            img.RenderTransform = hover;
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            sp.Children.Add(img);

            TextBlock name = new TextBlock();
            name.Text = it.Name;
            name.FontSize = 12;
            name.Foreground = _theme.Text;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxHeight = 34;
            name.Margin = new Thickness(4, 5, 4, 0);
            sp.Children.Add(name);
            b.Child = sp;
            b.ToolTip = it.Name;

            ScaleTransform pop = new ScaleTransform(0.55, 0.55);
            b.RenderTransform = pop;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.Opacity = 0;
            b.Tag = pop;

            bool pressed = false;
            b.MouseEnter += delegate
            {
                b.Background = _theme.Hover;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 1.07, 150, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 1.07, 150, 0, Out());
            };
            b.MouseLeave += delegate
            {
                pressed = false;
                b.Background = Brushes.Transparent;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 1, 180, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 1, 180, 0, Out());
            };
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                pressed = true;
                b.Background = _theme.Pressed;
                Anim(hover, ScaleTransform.ScaleXProperty, null, 0.93, 90, 0, Out());
                Anim(hover, ScaleTransform.ScaleYProperty, null, 0.93, 90, 0, Out());
            };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!pressed) return;
                pressed = false;
                Launch(it);
            };
            b.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(b, it); };
            return b;
        }

        double AnimateGridOpen()
        {
            Anim(_gridScale, ScaleTransform.ScaleXProperty, 0.06, 1, 460, 0, new SpringEase(0.74));
            Anim(_gridScale, ScaleTransform.ScaleYProperty, 0.06, 1, 460, 0, new SpringEase(0.74));
            Anim(_gridHost, UIElement.OpacityProperty, 0, 1, 120, 0, null);
            double total = 460;
            for (int i = 0; i < _gridTiles.Count; i++)
            {
                int r = i / _gridCols, c = i % _gridCols;
                double d = Math.Min(70 + (r + c) * 16, 320);
                FrameworkElement t = _gridTiles[i];
                ScaleTransform pop = (ScaleTransform)t.Tag;
                Anim(pop, ScaleTransform.ScaleXProperty, 0.55, 1, 380, d, new SpringEase(0.62));
                Anim(pop, ScaleTransform.ScaleYProperty, 0.55, 1, 380, d, new SpringEase(0.62));
                Anim(t, UIElement.OpacityProperty, 0, 1, 150, d, null);
                total = Math.Max(total, d + 380);
            }
            return total;
        }

        double AnimateGridClose()
        {
            SnapTo(_gridScale, ScaleTransform.ScaleXProperty, 0.06, 240, 0, new CubicEase { EasingMode = EasingMode.EaseIn });
            SnapTo(_gridScale, ScaleTransform.ScaleYProperty, 0.06, 240, 0, new CubicEase { EasingMode = EasingMode.EaseIn });
            SnapTo(_gridHost, UIElement.OpacityProperty, 0, 240, 0, new PowerEase { Power = 3, EasingMode = EasingMode.EaseIn });
            return 240;
        }

        // ================================================================ actions

        void Launch(GroupItem it)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(it.Path);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) // cancelled UAC
                    Error("无法打开「" + it.Name + "」：" + ex.Message);
            }
            catch (Exception ex) { Error("无法打开「" + it.Name + "」：" + ex.Message); }
            BeginClose();
        }

        void RunAsAdmin(GroupItem it)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(it.Path);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode != 1223) Error(ex.Message); }
            catch (Exception ex) { Error(ex.Message); }
            BeginClose();
        }

        void OpenFolder()
        {
            Host.OpenInExplorer(_group.Dir);
            BeginClose();
        }

        void Manage()
        {
            string name = _group.Name;
            CloseNow();
            Host.Current.ShowManager(name);
        }

        void Error(string msg)
        {
            CloseNow();
            MacAlert.Info(null, "无法打开", msg);
        }

        void ShowMenu(FrameworkElement target, GroupItem it)
        {
            List<MacMenuItem> m = new List<MacMenuItem>();
            if (it != null)
            {
                m.Add(MacMenuItem.Item("打开", delegate { Launch(it); }));
                if (!it.IsFolder) m.Add(MacMenuItem.Item("以管理员身份运行", delegate { RunAsAdmin(it); }));
                m.Add(MacMenuItem.Item("在资源管理器中显示", delegate
                {
                    string target2, args;
                    string p = it.Path;
                    if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Shortcut.Read(p, out target2, out args) && !string.IsNullOrEmpty(target2))
                        p = target2;
                    Host.SelectInExplorer(p);
                    BeginClose();
                }));
                m.Add(MacMenuItem.Sep());
                m.Add(MacMenuItem.Item("从文件夹中移除", delegate
                {
                    GroupStore.Remove(it);
                    Host.Current.Balloon("「" + it.Name + "」已移到回收站");
                    BeginClose();
                }));
                m.Add(MacMenuItem.Sep());
            }
            StackMenus.AddGroupSections(m, _group, SetSort, SetView, OpenFolder, Manage);
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            MacMenu.Show(m, pt, false, null);
        }

        void SetSort(string s)
        {
            Host.Current.SetSort(_group.Name, s);
            Reopen();
        }

        void SetView(string v)
        {
            try { GroupStore.SetView(_group, v); } catch { }
            Reopen();
        }

        void Reopen()
        {
            Group g = GroupStore.Load(_group.Name);
            Native.POINT pt = _lastPt;
            bool pv = _lastPreview;
            CloseNow();
            Open(g, pt, pv); // replay with the new style / order
        }

        public void ClearToggle() { _toggleGroup = null; }

        public static void RememberButton(string group, Rect px)
        {
            _buttonCache[group] = px;
        }

        // ================================================================ closing when clicking elsewhere

        void BringToFront()
        {
            Activate();
            if (_hwnd != IntPtr.Zero && Native.GetForegroundWindow() != _hwnd)
            {
                uint pid;
                IntPtr fg = Native.GetForegroundWindow();
                uint ft = Native.GetWindowThreadProcessId(fg, out pid);
                uint me = Native.GetCurrentThreadId();
                if (ft != 0 && ft != me)
                {
                    Native.AttachThreadInput(me, ft, true);
                    Native.SetForegroundWindow(_hwnd);
                    Native.AttachThreadInput(me, ft, false);
                }
                else Native.SetForegroundWindow(_hwnd);
                Activate();
            }
            Focus();
        }

        void OnDeactivated()
        {
            if (_state != State.Opening && _state != State.Open) return;
            if (MacMenu.IsOpen) return;
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            if (Native.IsKeyDown(Native.VK_LBUTTON) && IsOnOwnButton(pt)) MarkToggle();
            BeginClose();
        }

        void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            _hookProc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Program.Log("mouse hook failed: " + Marshal.GetLastWin32Error());
        }

        void RemoveHook()
        {
            if (_hook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        Native.POINT pt = info.pt;
                        if (!IsOwnWindowAt(pt))
                            Dispatcher.BeginInvoke(new Action<Native.POINT, bool>(OnOutsideClick), pt, msg == Native.WM_LBUTTONDOWN);
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        bool IsOwnWindowAt(Native.POINT pt)
        {
            IntPtr h = Native.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
            uint pid;
            Native.GetWindowThreadProcessId(root != IntPtr.Zero ? root : h, out pid);
            return pid == _pid;
        }

        void OnOutsideClick(Native.POINT pt, bool left)
        {
            if (_state != State.Opening && _state != State.Open) return;
            if (MacMenu.IsOpen) return;
            // only a left click on our own button relaunches us; a right click opens the menu instead
            if (left && IsOnOwnButton(pt)) MarkToggle();
            BeginClose();
        }

        void MarkToggle()
        {
            if (_group == null) return;
            _toggleGroup = _group.Name;
            _toggleAt = DateTime.Now;
        }

        bool IsOnOwnButton(Native.POINT pt)
        {
            if (!_fromTaskbar || _lastPreview) return false;
            if (!_buttonPx.IsEmpty) return _buttonPx.Contains(pt.X, pt.Y);
            Point d = new Point(pt.X / _dpi, pt.Y / _dpi);
            if (_taskbarDip.IsEmpty || !_taskbarDip.Contains(d)) return false;
            if (_edge == Edge.Bottom || _edge == Edge.Top) return Math.Abs(d.X - _origin.X) <= 22;
            return Math.Abs(d.Y - _origin.Y) <= 22;
        }

        // ================================================================ helpers

        static IEasingFunction Out() { return new CubicEase { EasingMode = EasingMode.EaseOut }; }

        static double Clamp(double v, double lo, double hi)
        {
            if (hi < lo) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static Rect RotBounds(Rect r, Point pivot, double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            Point[] pts = { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight };
            Rect b = Rect.Empty;
            foreach (Point p in pts)
            {
                double dx = p.X - pivot.X, dy = p.Y - pivot.Y;
                Point q = new Point(pivot.X + dx * c - dy * s, pivot.Y + dx * s + dy * c);
                if (b.IsEmpty) b = new Rect(q, q); else b.Union(q);
            }
            return b;
        }

        static void Anim(IAnimatable o, DependencyProperty dp, double? from, double to, double ms, double delay, IEasingFunction ease)
        {
            DoubleAnimation a = new DoubleAnimation();
            a.From = from;
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            a.BeginTime = TimeSpan.FromMilliseconds(delay);
            a.EasingFunction = ease;
            o.BeginAnimation(dp, a);
        }

        /// <summary>Freezes the current (possibly mid-flight) value, then animates from it.</summary>
        static void SnapTo(DependencyObject o, DependencyProperty dp, double to, double ms, double delay, IEasingFunction ease)
        {
            double v = (double)o.GetValue(dp);
            IAnimatable an = (IAnimatable)o;
            an.BeginAnimation(dp, null);
            o.SetValue(dp, v);
            Anim(an, dp, null, to, ms, delay, ease);
        }

        void After(double ms, Action a)
        {
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            t.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms));
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }
    }
}
