using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Microsoft.Win32;

namespace DockFolder
{
    /// <summary>
    /// Warm "Claude desktop" palette: cream surfaces, hairline borders, clay accent.
    /// Light / dark follows the Windows app theme.
    /// </summary>
    internal class MacPalette
    {
        public bool Light;
        public Brush Window, Sidebar, Card, Border, Text, Sub, Tertiary, Accent, AccentPressed, Selection, Hover,
                     ControlBg, ControlPressed, ControlBorder, SegBg, SegThumb, FieldBg, FieldBorder, Panel, Danger, Overlay;
        public Color AccentColor;

        static Brush B(uint argb)
        {
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        public static MacPalette Current()
        {
            bool light = true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) light = (int)v != 0;
                    }
                }
            }
            catch { }
            MacPalette p = new MacPalette();
            p.Light = light;
            p.AccentColor = Color.FromRgb(0xD9, 0x77, 0x57);
            p.Accent = B(0xFFD97757); p.AccentPressed = B(0xFFC6613F); p.Danger = B(0xFFC4422F);
            if (light)
            {
                p.Window = B(0xFFFAF9F5); p.Sidebar = B(0xFFF3F1EA); p.Card = B(0xFFFFFFFF); p.Border = B(0xFFE6E3D9);
                p.Text = B(0xFF141413); p.Sub = B(0xFF73726C); p.Tertiary = B(0xFFB4B2A9);
                p.Selection = B(0xFFE8E5DA); p.Hover = B(0xFFEDEBE3);
                p.ControlBg = B(0xFFFFFFFF); p.ControlPressed = B(0xFFF0EEE6); p.ControlBorder = B(0xFFDEDBD0);
                p.SegBg = B(0xFFEDEBE3); p.SegThumb = B(0xFFFFFFFF);
                p.FieldBg = B(0xFFFFFFFF); p.FieldBorder = B(0xFFDEDBD0);
                p.Panel = B(0xFFFAF9F5); p.Overlay = B(0x33141413);
            }
            else
            {
                p.Window = B(0xFF262624); p.Sidebar = B(0xFF1F1E1D); p.Card = B(0xFF30302E); p.Border = B(0xFF3A3935);
                p.Text = B(0xFFF5F4EE); p.Sub = B(0xFFA6A39A); p.Tertiary = B(0xFF6B6963);
                p.Selection = B(0xFF3A3935); p.Hover = B(0xFF2F2E2B);
                p.ControlBg = B(0xFF30302E); p.ControlPressed = B(0xFF3A3935); p.ControlBorder = B(0xFF46443F);
                p.SegBg = B(0xFF1F1E1D); p.SegThumb = B(0xFF3A3935);
                p.FieldBg = B(0xFF1F1E1D); p.FieldBorder = B(0xFF46443F);
                p.Panel = B(0xFF2B2A28); p.Overlay = B(0x66000000);
            }
            return p;
        }
    }

    internal static class MacUI
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");
        public static readonly FontFamily Serif = new FontFamily("Georgia, Microsoft YaHei UI");
        public static readonly FontFamily Symbols = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static TextBlock Label(string text, double size, Brush fg, bool bold)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.Foreground = fg;
            if (bold) t.FontWeight = FontWeights.SemiBold;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        public static TextBlock Glyph(string g, double size, Brush fg)
        {
            TextBlock t = new TextBlock();
            t.Text = g;
            t.FontFamily = Symbols;
            t.FontSize = size;
            t.Foreground = fg;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }

        public static Border Button(string text, bool primary, MacPalette p, Action click)
        {
            return Button(text, primary, false, p, click);
        }

        /// <summary>Rounded button: primary = clay fill, secondary = card with hairline border.</summary>
        public static Border Button(string text, bool primary, bool chevron, MacPalette p, Action click)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(8);
            b.Height = 32;
            b.MinWidth = 64;
            b.Padding = new Thickness(14, 0, chevron ? 10 : 14, 0);
            Brush normal = primary ? p.Accent : p.ControlBg, pressed = primary ? p.AccentPressed : p.ControlPressed,
                  hover = primary ? p.AccentPressed : p.Hover;
            b.Background = normal;
            b.BorderBrush = primary ? Brushes.Transparent : p.ControlBorder;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.Cursor = Cursors.Hand;
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;
            TextBlock t = Label(text, 13, primary ? Brushes.White : p.Text, primary);
            t.Margin = new Thickness(0, 0, 0, 1);
            sp.Children.Add(t);
            if (chevron)
            {
                TextBlock c = Glyph("", 9, primary ? Brushes.White : p.Sub);
                c.Margin = new Thickness(7, 1, 0, 0);
                sp.Children.Add(c);
            }
            b.Child = sp;
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);

            bool down = false;
            b.MouseEnter += delegate { if (!down) b.Background = hover; };
            b.MouseLeave += delegate { if (!down) b.Background = normal; };
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; down = true; b.Background = pressed; b.CaptureMouse(); };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                b.ReleaseMouseCapture();
                bool fire = down && b.IsMouseOver;
                down = false;
                b.Background = b.IsMouseOver ? hover : normal;
                if (fire && click != null) click();
            };
            b.LostMouseCapture += delegate { down = false; b.Background = b.IsMouseOver ? hover : normal; };
            return b;
        }

        public static Border IconButton(string glyph, double size, MacPalette p, string tip, Action click)
        {
            Border b = new Border();
            b.Width = 32; b.Height = 32;
            b.CornerRadius = new CornerRadius(8);
            b.Background = Brushes.Transparent;
            b.Child = Glyph(glyph, size, p.Sub);
            b.ToolTip = tip;
            b.Cursor = Cursors.Hand;
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            b.MouseEnter += delegate { b.Background = p.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; b.Background = p.Selection; };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; b.Background = p.Hover; if (click != null) click(); };
            return b;
        }

        /// <summary>Windows-style minimise / maximise / close, drawn by us so nothing overlaps the toolbar.</summary>
        public static FrameworkElement CaptionButtons(Window w, MacPalette p)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Right;
            sp.VerticalAlignment = VerticalAlignment.Top;
            string[] glyphs = { "", "", "" };
            TextBlock maxGlyph = null;
            for (int i = 0; i < 3; i++)
            {
                Border b = new Border();
                b.Width = 46; b.Height = 36;
                b.Background = Brushes.Transparent;
                TextBlock g = Glyph(glyphs[i], 10, p.Text);
                b.Child = g;
                if (i == 1) maxGlyph = g;
                int idx = i;
                WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                Brush hoverBg = idx == 2 ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : p.Hover;
                b.MouseEnter += delegate { b.Background = hoverBg; if (idx == 2) g.Foreground = Brushes.White; };
                b.MouseLeave += delegate { b.Background = Brushes.Transparent; g.Foreground = p.Text; };
                b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
                b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    if (idx == 0) w.WindowState = WindowState.Minimized;
                    else if (idx == 1) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    else w.Close();
                };
                sp.Children.Add(b);
            }
            w.StateChanged += delegate { maxGlyph.Text = w.WindowState == WindowState.Maximized ? "" : ""; };
            return sp;
        }

        /// <summary>Small pill toggle switch.</summary>
        public static FrameworkElement Toggle(bool on, MacPalette p, Action<bool> changed)
        {
            Border track = new Border();
            track.Width = 32; track.Height = 18;
            track.CornerRadius = new CornerRadius(9);
            track.Cursor = Cursors.Hand;
            Border knob = new Border();
            knob.Width = 14; knob.Height = 14;
            knob.CornerRadius = new CornerRadius(7);
            knob.Background = Brushes.White;
            knob.HorizontalAlignment = HorizontalAlignment.Left;
            knob.Margin = new Thickness(2, 0, 0, 0);
            DropShadowEffect sh = new DropShadowEffect();
            sh.BlurRadius = 2; sh.ShadowDepth = 0.5; sh.Opacity = 0.25; sh.Direction = 270;
            knob.Effect = sh;
            TranslateTransform tt = new TranslateTransform(on ? 14 : 0, 0);
            knob.RenderTransform = tt;
            track.Child = knob;
            track.Background = on ? p.Accent : p.Tertiary;
            bool state = on;
            track.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                state = !state;
                track.Background = state ? p.Accent : p.Tertiary;
                DoubleAnimation a = new DoubleAnimation(state ? 14 : 0, TimeSpan.FromMilliseconds(180));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, a);
                if (changed != null) changed(state);
            };
            return track;
        }
    }

    /// <summary>Segmented control: the thumb slides to the chosen segment.</summary>
    internal class Segmented : Grid
    {
        const double SegW = 60;
        readonly TranslateTransform _move = new TranslateTransform();
        readonly Action<int> _changed;
        readonly List<TextBlock> _labels = new List<TextBlock>();
        readonly MacPalette _p;
        int _sel = -1;

        public Segmented(string[] labels, int selected, MacPalette p, Action<int> changed)
        {
            _p = p;
            _changed = changed;
            Width = labels.Length * SegW + 6;
            Height = 32;
            VerticalAlignment = VerticalAlignment.Center;

            Border bg = new Border();
            bg.CornerRadius = new CornerRadius(8);
            bg.Background = p.SegBg;
            Children.Add(bg);

            Border thumb = new Border();
            thumb.Width = SegW;
            thumb.Margin = new Thickness(3);
            thumb.HorizontalAlignment = HorizontalAlignment.Left;
            thumb.CornerRadius = new CornerRadius(6);
            thumb.Background = p.SegThumb;
            thumb.BorderBrush = p.Border;
            thumb.BorderThickness = new Thickness(p.Light ? 1 : 0);
            thumb.RenderTransform = _move;
            Children.Add(thumb);

            UniformGrid ug = new UniformGrid();
            ug.Rows = 1; ug.Columns = labels.Length;
            ug.Margin = new Thickness(3);
            for (int i = 0; i < labels.Length; i++)
            {
                Border cell = new Border();
                cell.Background = Brushes.Transparent;
                cell.Cursor = Cursors.Hand;
                TextBlock t = MacUI.Label(labels[i], 12.5, p.Sub, false);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                cell.Child = t;
                _labels.Add(t);
                int idx = i;
                cell.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Select(idx, true, true); };
                cell.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
                ug.Children.Add(cell);
            }
            Children.Add(ug);
            Select(selected, false, false);
        }

        public void Select(int i, bool animate, bool notify)
        {
            if (i < 0) i = 0;
            bool changed = i != _sel;
            _sel = i;
            for (int k = 0; k < _labels.Count; k++)
            {
                _labels[k].Foreground = k == i ? _p.Text : _p.Sub;
                _labels[k].FontWeight = k == i ? FontWeights.SemiBold : FontWeights.Normal;
            }
            double x = i * SegW;
            if (animate)
            {
                DoubleAnimation a = new DoubleAnimation(x, TimeSpan.FromMilliseconds(200));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _move.BeginAnimation(TranslateTransform.XProperty, a);
            }
            else
            {
                _move.BeginAnimation(TranslateTransform.XProperty, null);
                _move.X = x;
            }
            if (notify && changed && _changed != null) _changed(i);
        }
    }

    /// <summary>Modal dialog: serif title, body text, right-aligned buttons; fades and scales in gently.</summary>
    internal class MacAlert : Window
    {
        readonly MacPalette _p;
        readonly Border _panel;
        readonly ScaleTransform _scale = new ScaleTransform(0.96, 0.96);
        TextBox _box;
        bool _ok, _closing;

        public static bool Confirm(Window owner, string title, string message, string okText, bool destructive)
        {
            MacAlert a = new MacAlert(owner, title, message, okText, "取消", destructive, null);
            a.ShowDialog();
            return a._ok;
        }

        public static void Info(Window owner, string title, string message)
        {
            MacAlert a = new MacAlert(owner, title, message, "好", null, false, null);
            a.ShowDialog();
        }

        /// <summary>Returns the entered text, or null when cancelled.</summary>
        public static string Prompt(Window owner, string title, string message, string defaultText, string okText)
        {
            MacAlert a = new MacAlert(owner, title, message, okText, "取消", false, defaultText ?? "");
            a.ShowDialog();
            return a._ok ? a._box.Text.Trim() : null;
        }

        MacAlert(Window owner, string title, string message, string okText, string cancelText, bool destructive, string input)
        {
            _p = MacPalette.Current();
            bool owned = owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized;
            if (owned) Owner = owner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = !owned;
            Topmost = !owned;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = owned ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            Title = title;
            FontFamily = MacUI.Font;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            _panel = new Border();
            _panel.Width = 400;
            _panel.Margin = new Thickness(32);
            _panel.CornerRadius = new CornerRadius(14);
            _panel.Background = _p.Panel;
            _panel.BorderBrush = _p.Border;
            _panel.BorderThickness = new Thickness(1);
            _panel.Padding = new Thickness(24, 22, 24, 20);
            DropShadowEffect sh = new DropShadowEffect();
            sh.BlurRadius = 32; sh.ShadowDepth = 8; sh.Direction = 270; sh.Opacity = _p.Light ? 0.16 : 0.45; sh.Color = Colors.Black;
            _panel.Effect = sh;
            _panel.RenderTransform = _scale;
            _panel.RenderTransformOrigin = new Point(0.5, 0.5);
            _panel.Opacity = 0;

            StackPanel sp = new StackPanel();
            TextBlock t = MacUI.Label(title, 18, _p.Text, false);
            t.FontFamily = MacUI.Serif;
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(message))
            {
                TextBlock m = MacUI.Label(message, 13, _p.Sub, false);
                m.TextWrapping = TextWrapping.Wrap;
                m.LineHeight = 20;
                m.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(m);
            }
            if (input != null)
            {
                Border field = new Border();
                field.CornerRadius = new CornerRadius(8);
                field.Background = _p.FieldBg;
                field.BorderBrush = _p.FieldBorder;
                field.BorderThickness = new Thickness(1);
                field.Margin = new Thickness(0, 16, 0, 0);
                _box = new TextBox();
                _box.Text = input;
                _box.FontSize = 13.5;
                _box.BorderThickness = new Thickness(0);
                _box.Background = Brushes.Transparent;
                _box.Foreground = _p.Text;
                _box.CaretBrush = _p.Text;
                _box.Padding = new Thickness(8, 7, 8, 7);
                _box.SelectionBrush = _p.Accent;
                field.Child = _box;
                _box.GotKeyboardFocus += delegate { field.BorderBrush = _p.Accent; };
                _box.LostKeyboardFocus += delegate { field.BorderBrush = _p.FieldBorder; };
                sp.Children.Add(field);
            }

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 22, 0, 0);
            if (cancelText != null)
            {
                Border cancel = MacUI.Button(cancelText, false, _p, delegate { Finish(false); });
                cancel.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(cancel);
            }
            Border okB = MacUI.Button(okText, true, _p, delegate { Finish(true); });
            if (destructive)
            {
                okB.Background = _p.Danger;
                okB.MouseLeave += delegate { okB.Background = _p.Danger; };
                okB.MouseLeftButtonUp += delegate { okB.Background = _p.Danger; };
                okB.LostMouseCapture += delegate { okB.Background = _p.Danger; };
            }
            buttons.Children.Add(okB);
            sp.Children.Add(buttons);
            _panel.Child = sp;
            Content = _panel;

            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; Finish(cancelText == null); }
                else if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            };
            Loaded += delegate
            {
                DoubleAnimation sc = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200));
                sc.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
                _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                Activate();
                if (_box != null) { _box.Focus(); _box.SelectAll(); }
            };
            Closing += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!_closing) { e.Cancel = true; Finish(false); }
            };
        }

        void Finish(bool ok)
        {
            if (_closing) return;
            if (ok && _box != null && _box.Text.Trim().Length == 0) { _box.Focus(); return; }
            _closing = true;
            _ok = ok;
            DoubleAnimation sc = new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(130));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += delegate { Close(); };
            _panel.BeginAnimation(OpacityProperty, fade);
        }
    }
}
