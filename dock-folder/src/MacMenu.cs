using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DockFolder
{
    internal class MacMenuItem
    {
        public string Text;
        public bool Checked;
        public bool Enabled = true;
        public Action Click;
        public int Kind; // 0 item, 1 header, 2 separator, 3 hint

        public static MacMenuItem Item(string text, Action click) { MacMenuItem m = new MacMenuItem(); m.Text = text; m.Click = click; return m; }
        public static MacMenuItem Check(string text, bool on, Action click) { MacMenuItem m = Item(text, click); m.Checked = on; return m; }
        public static MacMenuItem Header(string text) { MacMenuItem m = new MacMenuItem(); m.Text = text; m.Kind = 1; m.Enabled = false; return m; }
        public static MacMenuItem Hint(string text) { MacMenuItem m = new MacMenuItem(); m.Text = text; m.Kind = 3; m.Enabled = false; return m; }
        public static MacMenuItem Sep() { MacMenuItem m = new MacMenuItem(); m.Kind = 2; m.Enabled = false; return m; }
    }

    /// <summary>
    /// macOS-style context menu: translucent rounded panel, grey section headers, ✓ on the left,
    /// rounded accent highlight, the selection "blinks" once on click and the menu fades out.
    /// It never takes focus, so the stack underneath stays open; keyboard (↑↓ Enter Esc) works through a hook.
    /// </summary>
    internal class MacMenu : Window
    {
        public static MacMenu Current;
        public static bool IsOpen { get { return Current != null; } }

        const double Pad = 5, RowH = 24, Shadow = 34;
        readonly List<MacMenuItem> _items;
        readonly List<Border> _rows = new List<Border>();
        readonly List<TextBlock> _texts = new List<TextBlock>();
        readonly List<TextBlock> _checks = new List<TextBlock>();
        readonly Theme _theme;
        readonly Brush _bg, _edge, _inner, _text, _sub, _sepBrush, _accent, _disabled, _hlText;
        int _hover = -1;
        bool _closing;
        IntPtr _hwnd;
        Native.LowLevelProc _mouseProc, _keyProc;
        IntPtr _mouseHook, _keyHook;
        readonly uint _pid = (uint)Process.GetCurrentProcess().Id;
        Action _onClosed;

        static Brush B(uint argb)
        {
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        /// <param name="anchorPx">screen point in pixels</param>
        /// <param name="aboveCentered">true: centred above the point (taskbar button); false: drops from the cursor</param>
        public static void Show(List<MacMenuItem> items, Native.POINT anchorPx, bool aboveCentered, Action onClosed)
        {
            Show(items, anchorPx, aboveCentered, onClosed, null);
        }

        /// <param name="pal">non-null: use the manager window's warm palette (subtle highlight instead of blue)</param>
        public static void Show(List<MacMenuItem> items, Native.POINT anchorPx, bool aboveCentered, Action onClosed, MacPalette pal)
        {
            Show(items, anchorPx, aboveCentered ? PlaceAboveCentered : PlaceAtCursor, onClosed, pal);
        }

        public const int PlaceAtCursor = 0, PlaceAboveCentered = 1, PlaceAboveLeft = 2;

        public static void Show(List<MacMenuItem> items, Native.POINT anchorPx, int placement, Action onClosed, MacPalette pal)
        {
            if (Current != null) Current.CloseNow();
            MacMenu m = new MacMenu(items, pal);
            m._onClosed = onClosed;
            Current = m;
            m.Place(anchorPx, placement);
            m.Show();
            m.InstallHooks();
            DoubleAnimation a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110));
            m.BeginAnimation(OpacityProperty, a);
        }

        MacMenu(List<MacMenuItem> items, MacPalette pal)
        {
            _items = items;
            _theme = Theme.Current();
            if (pal != null)
            {
                _bg = pal.Card; _edge = pal.Border; _inner = Brushes.Transparent;
                _text = pal.Text; _sub = pal.Sub; _sepBrush = pal.Border; _disabled = pal.Tertiary;
            }
            else if (_theme.Light)
            {
                _bg = B(0xF2F4F4F6); _edge = B(0x30000000); _inner = B(0x00FFFFFF);
                _text = B(0xFF1D1D1F); _sub = B(0x8C000000); _sepBrush = B(0x1A000000); _disabled = B(0x59000000);
            }
            else
            {
                _bg = B(0xF0282829); _edge = B(0x99000000); _inner = B(0x26FFFFFF);
                _text = B(0xFFEDEDED); _sub = B(0x8CFFFFFF); _sepBrush = B(0x1FFFFFFF); _disabled = B(0x59FFFFFF);
            }
            _accent = pal != null ? pal.Selection : B(0xFF0A64D8);
            _hlText = pal != null ? pal.Text : Brushes.White;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;
            Opacity = 0;

            Border outer = new Border();
            outer.Margin = new Thickness(Shadow, Shadow * 0.6, Shadow, Shadow * 1.4);
            outer.CornerRadius = new CornerRadius(10);
            outer.Background = _bg;
            outer.BorderBrush = _edge;
            outer.BorderThickness = new Thickness(1);
            DropShadowEffect sh = new DropShadowEffect();
            sh.BlurRadius = 30; sh.ShadowDepth = 10; sh.Direction = 270; sh.Color = Colors.Black;
            sh.Opacity = _theme.Light ? 0.22 : 0.45;
            outer.Effect = sh;

            Border inner = new Border();
            inner.CornerRadius = new CornerRadius(9);
            inner.BorderBrush = _inner;
            inner.BorderThickness = new Thickness(1);
            inner.Padding = new Thickness(0, Pad - 1, 0, Pad - 1);
            outer.Child = inner;

            StackPanel sp = new StackPanel();
            sp.MinWidth = 210;
            inner.Child = sp;
            for (int i = 0; i < items.Count; i++) sp.Children.Add(BuildRow(i));
            Content = outer;

            SourceInitialized += delegate
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            MouseLeave += delegate { SetHover(-1); };
        }

        FrameworkElement BuildRow(int i)
        {
            MacMenuItem it = _items[i];
            if (it.Kind == 2)
            {
                Border s = new Border();
                s.Height = 1;
                s.Background = _sepBrush;
                s.Margin = new Thickness(14, 5, 14, 5);
                _rows.Add(null); _texts.Add(null); _checks.Add(null);
                return s;
            }
            if (it.Kind == 1 || it.Kind == 3)
            {
                TextBlock h = new TextBlock();
                h.Text = it.Text;
                h.FontSize = it.Kind == 1 ? 11.5 : 11;
                h.FontWeight = it.Kind == 1 ? FontWeights.SemiBold : FontWeights.Normal;
                h.Foreground = it.Kind == 1 ? _sub : _disabled;
                h.Margin = new Thickness(14, it.Kind == 1 ? 4 : 2, 14, 3);
                _rows.Add(null); _texts.Add(null); _checks.Add(null);
                return h;
            }

            Border row = new Border();
            row.Height = RowH;
            row.Margin = new Thickness(Pad, 0, Pad, 0);
            row.CornerRadius = new CornerRadius(5);
            row.Background = Brushes.Transparent;
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock ck = new TextBlock();
            ck.Text = it.Checked ? "✓" : "";
            ck.FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI");
            ck.FontSize = 12.5;
            ck.FontWeight = FontWeights.SemiBold;
            ck.Foreground = it.Enabled ? _text : _disabled;
            ck.HorizontalAlignment = HorizontalAlignment.Center;
            ck.VerticalAlignment = VerticalAlignment.Center;
            ck.Margin = new Thickness(3, 0, 0, 1);
            g.Children.Add(ck);
            TextBlock tx = new TextBlock();
            tx.Text = it.Text;
            tx.FontSize = 13;
            tx.Foreground = it.Enabled ? _text : _disabled;
            tx.VerticalAlignment = VerticalAlignment.Center;
            tx.Margin = new Thickness(0, 0, 18, 1);
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            row.Child = g;
            _rows.Add(row); _texts.Add(tx); _checks.Add(ck);

            int idx = i;
            row.MouseEnter += delegate { if (!_closing) SetHover(it.Enabled ? idx : -1); };
            row.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Choose(idx); };
            row.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Choose(idx); };
            return row;
        }

        void SetHover(int idx)
        {
            if (_hover == idx) return;
            if (_hover >= 0 && _rows[_hover] != null) Paint(_hover, false);
            _hover = idx;
            if (_hover >= 0 && _rows[_hover] != null) Paint(_hover, true);
        }

        void Paint(int i, bool on)
        {
            _rows[i].Background = on ? _accent : Brushes.Transparent;
            Brush fg = on ? _hlText : (_items[i].Enabled ? _text : _disabled);
            _texts[i].Foreground = fg;
            _checks[i].Foreground = fg;
        }

        Rect _panelPx = Rect.Empty; // the visible panel in screen pixels (excludes the shadow margin)

        void Place(Native.POINT px, int placement)
        {
            double dpi = Native.SystemDpiScale();
            ((FrameworkElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size s = ((FrameworkElement)Content).DesiredSize;
            double w = s.Width, h = s.Height;

            IntPtr mon = Native.MonitorFromPoint(px, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            Rect work = new Rect(mi.rcWork.Left / dpi, mi.rcWork.Top / dpi,
                                 (mi.rcWork.Right - mi.rcWork.Left) / dpi, (mi.rcWork.Bottom - mi.rcWork.Top) / dpi);
            double x = px.X / dpi, y = px.Y / dpi;
            double ml = Shadow, mt = Shadow * 0.6, mb = Shadow * 1.4; // panel offset inside the window
            double pw = w - 2 * Shadow, ph = h - mt - mb;

            double left, top;
            if (placement == PlaceAboveCentered)
            {
                left = x - pw / 2;
                top = Math.Min(y, work.Bottom) - 10 - ph;
            }
            else if (placement == PlaceAboveLeft)
            {
                left = x;
                top = y - ph;
                if (top < work.Top + 4) top = y + 40;
            }
            else
            {
                left = x + 2;
                top = y + 2;
                if (left + pw > work.Right - 4) left = x - pw - 2;
                if (top + ph > work.Bottom - 4) top = y - ph - 2;
            }
            left = Math.Max(work.Left + 4, Math.Min(left, work.Right - 4 - pw));
            top = Math.Max(work.Top + 4, top);
            Left = left - ml; Top = top - mt; Width = w; Height = h;
            _panelPx = new Rect(left * dpi, top * dpi, pw * dpi, ph * dpi);
        }

        void Choose(int idx)
        {
            if (_closing || idx < 0 || idx >= _items.Count) return;
            MacMenuItem it = _items[idx];
            if (!it.Enabled || it.Kind != 0) return;
            _closing = true;
            SetHover(idx);
            // macOS confirms a choice by blinking the highlight once before the menu fades
            After(70, delegate { Paint(idx, false); });
            After(140, delegate { Paint(idx, true); });
            After(200, delegate
            {
                FadeOut();
                if (it.Click != null)
                {
                    try { it.Click(); } catch (Exception ex) { Program.Log("menu: " + ex); }
                }
            });
        }

        public void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            FadeOut();
        }

        void FadeOut()
        {
            RemoveHooks();
            DoubleAnimation a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
            a.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            BeginAnimation(OpacityProperty, a);
            After(190, CloseNow);
        }

        public void CloseNow()
        {
            RemoveHooks();
            if (Current == this) Current = null;
            try { Close(); } catch { }
            Action cb = _onClosed;
            _onClosed = null;
            if (cb != null) cb();
        }

        void After(double ms, Action a)
        {
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }

        // ---------------------------------------------------------------- hooks (menu never has focus)

        void InstallHooks()
        {
            IntPtr mod = Native.GetModuleHandle(null);
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        }

        void RemoveHooks()
        {
            if (_keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
            if (_mouseHook == IntPtr.Zero) return;
            if (!_swallowUp) { UnhookMouse(); return; }
            // An outside click dismissed us: keep eating mouse input until its button-up has been
            // swallowed, otherwise the app underneath would open its own context menu on button-up.
            DateTime until = DateTime.Now.AddMilliseconds(1500);
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            t.Interval = TimeSpan.FromMilliseconds(40);
            t.Tick += delegate
            {
                bool anyDown = Native.IsKeyDown(Native.VK_LBUTTON) || Native.IsKeyDown(Native.VK_RBUTTON) || Native.IsKeyDown(0x04);
                if (!_swallowUp || !anyDown || DateTime.Now > until) { t.Stop(); _swallowUp = false; UnhookMouse(); }
            };
            t.Start();
        }

        void UnhookMouse()
        {
            if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }

        bool _swallowUp;

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                {
                    Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    // test against the visible panel, not the window (its transparent shadow margin is ~30px wide)
                    bool inside = !_closing && _panelPx.Contains(info.pt.X, info.pt.Y);
                    if (!inside)
                    {
                        // like macOS: a click outside only dismisses the menu, it doesn't reach the app below
                        _swallowUp = true;
                        Dispatcher.BeginInvoke(new Action(Dismiss));
                        return new IntPtr(1);
                    }
                }
                else if ((msg == Native.WM_LBUTTONUP || msg == Native.WM_RBUTTONUP || msg == Native.WM_MBUTTONUP) && _swallowUp)
                {
                    _swallowUp = false;
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_closing)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    if (vk == 0x1B) { Dispatcher.BeginInvoke(new Action(Dismiss)); return new IntPtr(1); }          // Esc
                    if (vk == 0x26) { Dispatcher.BeginInvoke(new Action<int>(Step), -1); return new IntPtr(1); }      // Up
                    if (vk == 0x28) { Dispatcher.BeginInvoke(new Action<int>(Step), 1); return new IntPtr(1); }       // Down
                    if (vk == 0x0D || vk == 0x20) { Dispatcher.BeginInvoke(new Action<int>(Choose), _hover); return new IntPtr(1); } // Enter / Space
                }
            }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        void Step(int dir)
        {
            int n = _items.Count;
            int i = _hover < 0 ? (dir > 0 ? -1 : n) : _hover;
            for (int k = 0; k < n; k++)
            {
                i = (i + dir + n) % n;
                if (_items[i].Kind == 0 && _items[i].Enabled) { SetHover(i); return; }
            }
        }
    }
}
