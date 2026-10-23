using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClearDesk
{
    public class GlassWindow : Window
    {
        protected Grid Root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) }, Body = new Grid();
        protected Border Frame;
        protected BoxData State;
        internal BoxData TestState { get { return State; } }
        protected DockPanel Layout = new DockPanel { LastChildFill = true };
        protected DockPanel Header;
        protected TextBlock Heading;
        protected DispatcherTimer Timer;
        protected GlassPane Surface;
        public bool WasClosed;
        bool ready;
        Point? dragStart;
        bool dragging;
        Vector dragOffset;
        Border toast;
        TextBlock lockBadge;
        MenuItem lockItem;
        double baseW = double.NaN, baseH = double.NaN;
        ScaleTransform scaler;
        Border scaleGrip;
        internal const double MinScale = .6, MaxScale = 2.5;

        Border cornerGrip, rightGrip, bottomGrip;
        bool resizing;

        // 2.0.7: widgets get free width/height. The content scale is the smaller of the two
        // ratios (so text grows evenly and never overflows), and the remaining space is given to
        // the layout so each widget can re-flow (more lyrics, more forecast, wider tiles…).
        // 收纳格 and 便签 resize their real layout themselves.
        protected virtual bool ScalesContent { get { return !(this is OrganizerWindow || this is NoteWindow || this is DockWindow || this is LaunchpadWindow); } }
        internal bool Scalable { get { return ScalesContent; } }
        internal bool HasCustomSize { get { return ScalesContent && (State.CustomW > 0 || State.CustomH > 0); } }
        internal double ContentScale
        {
            get
            {
                if (!ScalesContent || !HasCustomSize || double.IsNaN(baseW) || double.IsNaN(baseH) || baseW <= 0 || baseH <= 0) return 1;
                return Clamp(Math.Min(CustomW / baseW, CustomH / baseH));
            }
        }
        double CustomW { get { return State.CustomW > 0 ? State.CustomW : baseW; } }
        double CustomH { get { return State.CustomH > 0 ? State.CustomH : baseH; } }
        static double Clamp(double v) { return double.IsNaN(v) || v <= 0 ? 1 : Math.Max(MinScale, Math.Min(MaxScale, v)); }
        internal const double MaxSizeFactor = 4;
        /// <summary>Extra layout room (in unscaled units) beyond the default design size.</summary>
        internal double ExtraWidth { get; private set; }
        internal double ExtraHeight { get; private set; }

        // Design (default) size in unscaled units; the real window size is the custom size if any.
        public new double Width
        {
            get { return ScalesContent && !double.IsNaN(baseW) ? baseW : base.Width; }
            set { if (ScalesContent) { baseW = value; ApplySize(); } else base.Width = value; }
        }
        public new double Height
        {
            get { return ScalesContent && !double.IsNaN(baseH) ? baseH : base.Height; }
            set { if (ScalesContent) { baseH = value; ApplySize(); } else base.Height = value; }
        }
        internal double DefaultWidth { get { return baseW; } }
        internal double DefaultHeight { get { return baseH; } }

        void ApplySize()
        {
            if (!ScalesContent) return;
            if (double.IsNaN(baseW) || double.IsNaN(baseH)) { if (!double.IsNaN(baseW)) base.Width = baseW; if (!double.IsNaN(baseH)) base.Height = baseH; return; }
            // migrate 2.0.5/2.0.6 proportional scale into a custom size
            if (Math.Abs((State.Scale <= 0 ? 1 : State.Scale) - 1) > .001)
            {
                if (!HasCustomSize) { double f = Clamp(State.Scale); State.CustomW = Math.Round(baseW * f); State.CustomH = Math.Round(baseH * f); }
                State.Scale = 1;
            }
            double s = ContentScale;
            if (scaler != null) { scaler.ScaleX = s; scaler.ScaleY = s; }
            TextOptions.SetTextFormattingMode(this, Math.Abs(s - 1) < .001 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
            double w = HasCustomSize ? CustomW : baseW, h = HasCustomSize ? CustomH : baseH;
            base.Width = w;
            base.Height = State.Collapsed && collapsedApplied ? 64 * s : h;
            ExtraWidth = Math.Max(0, w / s - baseW); ExtraHeight = Math.Max(0, h / s - baseH);
            if (scaler != null) OnResponsive(ExtraWidth, ExtraHeight);
        }
        bool collapsedApplied;

        /// <summary>Called whenever the layout gains or loses room; widgets re-flow here.</summary>
        protected virtual void OnResponsive(double extraW, double extraH) { }
        /// <summary>Centre the body content in the extra room instead of leaving a gap below.</summary>
        protected void CenterBody(double extraH)
        {
            foreach (UIElement e in Body.Children) { var f = e as FrameworkElement; if (f != null) f.VerticalAlignment = extraH > 1 ? VerticalAlignment.Center : VerticalAlignment.Stretch; }
        }

        internal void SetCustomSize(double w, double h, bool save = true)
        {
            if (!ScalesContent || double.IsNaN(baseW) || double.IsNaN(baseH)) return;
            State.Scale = 1;
            State.CustomW = Math.Round(Math.Max(baseW * MinScale, Math.Min(baseW * MaxSizeFactor, w)), 1);
            State.CustomH = Math.Round(Math.Max(baseH * MinScale, Math.Min(baseH * MaxSizeFactor, h)), 1);
            if (Math.Abs(State.CustomW - baseW) < .5 && Math.Abs(State.CustomH - baseH) < .5) { State.CustomW = 0; State.CustomH = 0; }
            ApplySize();
            if (save) Program.Save();
        }

        /// <summary>Kept for compatibility: a uniform factor of the default size.</summary>
        internal void SetScale(double scale, bool save = true)
        {
            if (!ScalesContent) return;
            if (double.IsNaN(baseW) || double.IsNaN(baseH)) { State.Scale = Clamp(scale); return; }
            if (Math.Abs(scale - 1) < .001) { ResetSize(save); return; }
            double f = Clamp(scale);
            State.Scale = 1; State.CustomW = baseW * f; State.CustomH = baseH * f;
            ApplySize();
            if (save) Program.Save();
        }

        /// <summary>恢复默认大小.</summary>
        internal void ResetSize(bool save = true)
        {
            State.Scale = 1; State.CustomW = 0; State.CustomH = 0;
            ApplySize();
            if (save) Program.Save();
        }

        Border MakeGrip(bool x, bool y)
        {
            var g = new Border { Background = Brushes.Transparent, Opacity = x && y ? 0 : 1 };
            if (x && y)
            {
                g.Width = 20; g.Height = 20; g.HorizontalAlignment = HorizontalAlignment.Right; g.VerticalAlignment = VerticalAlignment.Bottom;
                g.Margin = new Thickness(0, 0, 5, 5); g.Cursor = Cursors.SizeNWSE; g.ToolTip = "拖动自由调整宽高（右键可恢复默认大小）";
                g.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 15,5 L 5,15 M 15,10 L 10,15"), Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            }
            else if (x) { g.Width = 7; g.HorizontalAlignment = HorizontalAlignment.Right; g.Margin = new Thickness(0, 22, 0, 26); g.Cursor = Cursors.SizeWE; }
            else { g.Height = 7; g.VerticalAlignment = VerticalAlignment.Bottom; g.Margin = new Thickness(22, 0, 26, 0); g.Cursor = Cursors.SizeNS; }
            Panel.SetZIndex(g, 95);
            System.Drawing.Point start = System.Drawing.Point.Empty; double w0 = 0, h0 = 0; bool active = false;
            g.MouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (State.Locked || State.Collapsed) return;
                start = System.Windows.Forms.Cursor.Position; w0 = base.Width; h0 = base.Height;
                active = resizing = g.CaptureMouse();
            };
            g.MouseMove += delegate (object sender, MouseEventArgs e)
            {
                if (!active) return;
                if (e.LeftButton != MouseButtonState.Pressed) { active = resizing = false; g.ReleaseMouseCapture(); Program.Save(); GuideOverlay.Hide(); return; }
                var dpi = VisualTreeHelper.GetDpi(this); var now = System.Windows.Forms.Cursor.Position;
                double w = x ? w0 + (now.X - start.X) / dpi.DpiScaleX : w0, h = y ? h0 + (now.Y - start.Y) / dpi.DpiScaleY : h0;
                w = Math.Max(baseW * MinScale, Math.Min(baseW * MaxSizeFactor, w)); h = Math.Max(baseH * MinScale, Math.Min(baseH * MaxSizeFactor, h));
                if (Program.S.Magnet && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                {
                    var r = MagnetSnap.SnapResize(new Rect(Left, Top, w, h), Program.SnapTargets(this), CursorInfo.Areas(this), MagnetSnap.Threshold, x, y);
                    if (x && r.SnappedX) w = r.X - Left;
                    if (y && r.SnappedY) h = r.Y - Top;
                    GuideOverlay.Show(r.Guides);
                }
                else GuideOverlay.Hide();
                SetCustomSize(w, h, false);
                e.Handled = true;
            };
            g.MouseLeftButtonUp += delegate (object sender, MouseButtonEventArgs e) { if (active) { active = resizing = false; g.ReleaseMouseCapture(); Program.Save(); } GuideOverlay.Hide(); e.Handled = true; };
            g.LostMouseCapture += delegate { if (active) { active = resizing = false; Program.Save(); } GuideOverlay.Hide(); };
            Root.Children.Add(g);
            return g;
        }

        void AddScaleGrip()
        {
            rightGrip = MakeGrip(true, false); bottomGrip = MakeGrip(false, true); cornerGrip = MakeGrip(true, true);
            Action sync = delegate
            {
                var v = State.Locked || State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                rightGrip.Visibility = v; bottomGrip.Visibility = v; cornerGrip.Visibility = v;
            };
            MouseEnter += delegate { sync(); if (!State.Locked && !State.Collapsed) Fade(cornerGrip, .85, 160); };
            MouseLeave += delegate { if (!resizing) Fade(cornerGrip, 0, 220); };
        }
        public string ExportName
        {
            get
            {
                return State.Id;
            }
        }

        public GlassWindow(BoxData state, string title, bool plain = false)
        {
            State = state;
            Icon = AppBrand.Icon;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            Title = "ClearDesk · " + title;
            Width = Math.Max(180, state.Width);
            Height = Math.Max(100, state.Height);
            Left = state.X;
            Top = state.Y;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = Program.Preview;
            ShowActivated = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Foreground = Brushes.White;
            var wa = SystemParameters.WorkArea;
            if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)Left + 30, (int)Top + 30)))
            {
                Left = wa.Left + 20;
                Top = wa.Top + 20;
            }

            Frame = new Border
            {
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 235, 242, 255)),
                Child = Root
            };
            Content = Frame;
            if (ScalesContent)
            {
                scaler = new ScaleTransform(1, 1);
                Frame.LayoutTransform = scaler;
                ApplySize();
            }
            if (!plain || state.MaterialSelected)
            {
                Surface = new GlassPane(state.Material);
                Root.Children.Add(Surface);
                MouseMove += delegate (object sender, MouseEventArgs e)
                {
                    if (Root.Children.Contains(Surface))
                        Surface.Pointer(e.GetPosition(Root), Root.ActualWidth, Root.ActualHeight);
                };
                MouseLeave += delegate
                {
                    if (Surface != null)
                        Surface.Leave();
                };
            }
            else
            {
                Frame.BorderThickness = new Thickness(0);
                Root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            }

            Root.Children.Add(Layout);
            Layout.Margin = new Thickness(20);
            Header = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 18),
                Background = Brushes.Transparent
            };
            Heading = Program.Text(title, 13);
            Heading.FontWeight = FontWeights.SemiBold;
            Header.Children.Add(Heading);
            DockPanel.SetDock(Header, Dock.Top);
            Layout.Children.Add(Header);
            Layout.Children.Add(Body);
            Header.ToolTip = "拖动标题栏或组件空白区域移动；锁定后不可移动";
            Root.MouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                if (State.Locked || this is LaunchpadWindow || this is DockWindow || WidgetDrag.IsInteractive(e.OriginalSource as DependencyObject, Root)) return;
                if (e.ClickCount == 2 && HeaderDoubleClickCollapses && Header.IsMouseOver)
                {
                    State.Collapsed = !State.Collapsed; SetCollapsed(); Program.Save(); e.Handled = true; return;
                }
                dragStart = e.GetPosition(this);
            };
            Root.MouseMove += delegate (object sender, MouseEventArgs e)
            {
                if (!dragStart.HasValue || State.Locked || e.LeftButton != MouseButtonState.Pressed) { dragStart = null; return; }
                var point=e.GetPosition(this); var delta=point-dragStart.Value;
                if (System.Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance && System.Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance) return;
                dragStart=null;
                BeginDrag();
                e.Handled=true;
            };
            PreviewMouseMove += delegate (object sender, MouseEventArgs e)
            {
                if (!dragging) return;
                if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
                DragTo(CursorInfo.Dip(this), (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
                e.Handled = true;
            };
            PreviewMouseLeftButtonUp += delegate { dragStart=null; if (dragging) EndDrag(); };
            LostMouseCapture += delegate { if (dragging) EndDrag(); };
            lockBadge = new TextBlock
            {
                Text = "\uE72E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 16, 0),
                Opacity = 0, IsHitTestVisible = false, ToolTip = "位置已锁定"
            };
            Panel.SetZIndex(lockBadge, 90);
            Root.Children.Add(lockBadge);
            if (ScalesContent) AddScaleGrip();
            MouseEnter += delegate { if (State.Locked && !(this is DockWindow) && !(this is LaunchpadWindow)) Fade(lockBadge, .6, 160); };
            MouseLeave += delegate { Fade(lockBadge, 0, 220); };
            LocationChanged += delegate
            {
                UpdatePaper();
                if (ready)
                {
                    State.X = Left;
                    State.Y = Top;
                }
            };
            MouseLeftButtonUp += delegate
            {
                if (ready)
                    Program.Save();
            };
            SizeChanged += delegate
            {
                double cw = ActualWidth / ContentScale, ch = ActualHeight / ContentScale;
                Root.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, cw - 2), Math.Max(0, ch - 2)), 22, 22);
                UpdatePaper();
            };
            ContextMenu = Menu();
            Loaded += delegate
            {
                ready = true;
                SetCollapsed();
                UpdatePaper();
                var h = new WindowInteropHelper(this).Handle;
                if (!Program.Preview)
                    Native.SetLong(h, -20, new IntPtr(Native.GetLong(h, -20).ToInt64() | 0x80));
                Native.SetWindowPos(h, new IntPtr(Topmost ? -1 : 1), 0, 0, 0, 0, 0x13);
                var source = HwndSource.FromHwnd(h);
                if (source != null) source.AddHook(PinHook);
            };
            Closed += delegate
            {
                WasClosed = true;
                if (Timer != null)
                    Timer.Stop();
            };
        }

        void UpdatePaper()
        {
        }

        protected virtual bool HeaderDoubleClickCollapses
        {
            get
            {
                return true;
            }
        }

        protected virtual void SetCollapsed()
        {
            Body.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (ScalesContent) { collapsedApplied = true; if (!State.Collapsed && State.Height > 0) baseH = State.Height; ApplySize(); }
            else Height = State.Collapsed ? 64 : State.Height;
        }

        protected MenuItem Item(string text, Action action, bool check = false)
        {
            var m = new MenuItem
            {
                Header = text,
                IsChecked = check
            };
            m.Click += delegate
            {
                action();
            };
            return m;
        }

        protected virtual ContextMenu Menu()
        {
            var c = new ContextMenu();
            var material = new MenuItem
            {
                Header = "材质设置"
            };
            var clear = Item("玻璃 · 清透高光", delegate
            {
                Material("liquid");
            });
            var frost = Item("雾面 · 背景磨砂", delegate
            {
                Material("frosted");
            });
            material.Items.Add(clear);
            material.Items.Add(frost);
            material.SubmenuOpened += delegate
            {
                clear.IsChecked = State.Material == "liquid";
                frost.IsChecked = State.Material == "frosted";
            };
            c.Items.Add(material);
            c.Items.Add(Item("收缩 / 展开", delegate
            {
                State.Collapsed = !State.Collapsed;
                SetCollapsed();
                Program.Save();
            }));
            if (ScalesContent)
            {
                var size = new MenuItem { Header = "恢复默认大小", Tag = "size" };
                size.Click += delegate { if (!State.Locked) ResetSize(); };
                c.Opened += delegate
                {
                    size.IsEnabled = !State.Locked && HasCustomSize;
                    size.ToolTip = State.Locked ? "已锁定，解锁后可调整大小" : HasCustomSize ? "回到默认宽高" : "拖动边缘或右下角可自由调整宽高";
                };
                c.Items.Add(size);
            }
            lockItem = new MenuItem { Tag = "lock", StaysOpenOnClick = false };
            lockItem.Click += delegate { SetLocked(!State.Locked); };
            UpdateLockItem();
            c.Opened += delegate { UpdateLockItem(); };
            c.Items.Add(lockItem);
            var create = new MenuItem { Header = "新建组件", Tag = "new" };
            foreach (var kind in WidgetCatalog.Kinds)
            {
                string id = kind.Key;
                create.Items.Add(Item(kind.Value, delegate { Program.NewWidget(id); }));
            }
            c.Items.Add(create);
            c.Items.Add(Item("打开设置", Program.ShowSettings));
            c.Items.Add(Item("恢复位置", delegate
            {
                Left = 20;
                Top = 20;
                State.X = Left;
                State.Y = Top;
                Program.Save();
            }));
            c.Items.Add(Item("关闭组件", Disable));
            return c;
        }

        internal static string LockActionText(bool locked) { return locked ? "解锁位置" : "锁定位置"; }
        internal static string LockStateText(bool locked) { return locked ? "位置已锁定" : "位置未锁定"; }
        internal MenuItem TestLockItem { get { UpdateLockItem(); return lockItem; } }
        internal Border TestToast { get { return toast; } }
        internal bool IsLocked { get { return State.Locked; } }

        void UpdateLockItem()
        {
            if (lockItem == null) return;
            bool locked = State.Locked;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = LockActionText(locked) });
            panel.Children.Add(new TextBlock { Text = "当前：" + LockStateText(locked), FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(locked ? MenuTheme.LockedAccent : MenuTheme.Secondary) });
            lockItem.Header = panel;
            lockItem.Icon = MenuTheme.Glyph(locked ? "\uE72E" : "\uE785", locked ? MenuTheme.LockedAccent : MenuTheme.Text);
        }

        // 2.0.8: a locked widget is pinned to the bottom of the z-order (like desktop icons). Clicking it no
        // longer raises it above other apps or other widgets; unlocked widgets can still come forward while
        // being arranged. Dock / 启动台 and topmost widgets are not affected; 定位 (Flash) may raise briefly.
        bool flashing;
        internal bool PinnedToBottom { get { return State.Locked && !Topmost && !flashing && !(this is DockWindow) && !(this is LaunchpadWindow); } }
        protected virtual bool NoActivateWhenLocked { get { return !(this is NoteWindow) && !(this is OrganizerWindow); } }
        IntPtr PinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x46, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            if (!PinnedToBottom) return IntPtr.Zero;
            if (msg == WM_MOUSEACTIVATE && NoActivateWhenLocked) { handled = true; return new IntPtr(MA_NOACTIVATE); }
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var pos = (Native.WINDOWPOS_PIN)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.WINDOWPOS_PIN));
                if ((pos.flags & 0x4) == 0) // SWP_NOZORDER not set: a z-order change is requested
                {
                    pos.hwndInsertAfter = new IntPtr(1); // HWND_BOTTOM
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            return IntPtr.Zero;
        }
        internal void SendToBottom()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero && PinnedToBottom) Native.SetWindowPos(h, new IntPtr(1), 0, 0, 0, 0, 0x13);
        }

        internal void SetLocked(bool locked)
        {
            State.Locked = locked;
            if (locked) SendToBottom();
            Program.Save();
            UpdateLockItem();
            Toast(locked ? "\uE72E" : "\uE785", LockStateText(locked));
            if (lockBadge != null) Fade(lockBadge, locked && IsMouseOver ? .6 : 0, 160);
        }

        static void Fade(UIElement e, double to, int ms)
        {
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() });
        }

        // 定位: bring the widget briefly above other windows and pulse its border.
        internal void Flash()
        {
            if (!IsVisible) Show();
            var h = new WindowInteropHelper(this).Handle;
            flashing = true;
            if (h != IntPtr.Zero) Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            var original = Frame.BorderBrush; var thickness = Frame.BorderThickness;
            var pulse = new SolidColorBrush(Color.FromArgb(0, 22, 119, 255));
            Frame.BorderBrush = pulse; Frame.BorderThickness = new Thickness(3);
            var a = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1800) };
            for (int i = 0; i < 3; i++)
            {
                a.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(255, 64, 150, 255), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 600 + 200))));
                a.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(40, 64, 150, 255), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 600 + 600))));
            }
            a.Completed += delegate
            {
                Frame.BorderBrush = original; Frame.BorderThickness = thickness;
                flashing = false;
                if (h != IntPtr.Zero && !WasClosed) { Native.SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x13); if (!Topmost) Native.SetWindowPos(h, new IntPtr(1), 0, 0, 0, 0, 0x13); }
            };
            pulse.BeginAnimation(SolidColorBrush.ColorProperty, a);
            Toast("\uE81D", "在这里");
        }

        // Brief pill-shaped notice centred near the bottom of the widget.
        internal void Toast(string glyph, string text)
        {
            if (toast != null) Root.Children.Remove(toast);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            toast = new Border
            {
                Child = row, Background = new SolidColorBrush(Color.FromArgb(225, 24, 26, 32)), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14), IsHitTestVisible = false, Opacity = 0
            };
            Panel.SetZIndex(toast, 100);
            Root.Children.Add(toast);
            var mine = toast;
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500) };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
            a.Completed += delegate { if (toast == mine) { Root.Children.Remove(mine); toast = null; } };
            mine.BeginAnimation(OpacityProperty, a);
        }

        void BeginDrag()
        {
            var cursor = CursorInfo.Dip(this);
            dragOffset = cursor - new Point(Left, Top);
            dragging = true;
            CaptureMouse();
        }

        internal void DragTo(Point cursor, bool free)
        {
            double x = cursor.X - dragOffset.X, y = cursor.Y - dragOffset.Y;
            if (Program.S.Magnet && !free)
            {
                var r = MagnetSnap.Snap(new Rect(x, y, ActualWidth, ActualHeight), Program.SnapTargets(this), CursorInfo.Areas(this), MagnetSnap.Threshold);
                x = r.X; y = r.Y;
                GuideOverlay.Show(r.Guides);
            }
            else
                GuideOverlay.Hide();
            Left = x;
            Top = y;
        }

        void EndDrag()
        {
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            GuideOverlay.Hide();
            try { Program.Save(); } catch (Exception e) { Program.Log(e); }
        }

        protected virtual void Disable()
        {
            State.Enabled = false;
            switch (State.Id)
            {
                case "clock":
                    Program.S.Clock = false;
                    break;
                case "performance":
                    Program.S.Performance = false;
                    break;
                case "dock":
                    Program.S.Dock = false;
                    break;
                case "calendar":
                    Program.S.Calendar = false;
                    break;
                case "almanac":
                    Program.S.Almanac = false;
                    break;
                case "weather":
                    Program.S.Weather = false;
                    break;
                case "music":
                    Program.S.Music = false;
                    break;
                case "quick":
                    Program.S.QuickControls = false;
                    break;
            }

            Program.Save();
            Close();
        }

        protected virtual void Material(string m)
        {
            State.Material = m;
            State.MaterialSelected = true;
            if (Surface == null)
            {
                Surface = new GlassPane(m);
                Root.Children.Insert(0, Surface);
                Frame.BorderThickness = new Thickness(1);
            }

            Surface.SetMaterial(m);
            Program.Save();
        }

        internal void TestMaterial(string m)
        {
            Material(m);
        }

        internal GlassPane TestSurface
        {
            get
            {
                return Surface;
            }
        }

        protected void Tick(int ms, Action action)
        {
            Timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ms)
            };
            Timer.Tick += delegate
            {
                if (IsVisible && !State.Collapsed) action();
            };
            Timer.Start();
            action();
        }
    }

    public class ClockWindow : GlassWindow
    {
        public ClockWindow() : base(Program.Widget("clock", SystemParameters.WorkArea.Right - 410, 245, 370, 170), "时钟", true)
        {
            Header.Visibility = Visibility.Collapsed;
            Layout.Margin = new Thickness(0);
            var c = new Grid();
            var day = Program.Text("", 64, "#89FFFFFF");
            day.FontFamily = new FontFamily("Segoe Script");
            day.FontStyle = FontStyles.Italic;
            day.HorizontalAlignment = HorizontalAlignment.Center;
            c.Children.Add(day);
            var s = new StackPanel
            {
                Margin = new Thickness(0, 52, 0, 0)
            };
            var t = Program.Text("", 40);
            t.FontFamily = new FontFamily("Segoe UI Light");
            t.TextAlignment = TextAlignment.Center;
            var d = Program.Text("", 20);
            d.FontFamily = new FontFamily("Segoe UI Light");
            d.TextAlignment = TextAlignment.Center;
            s.Children.Add(t);
            s.Children.Add(d);
            c.Children.Add(s);
            c.HorizontalAlignment = HorizontalAlignment.Center;
            Body.Children.Add(c);
            Tick(10000, delegate
            {
                var now = DateTime.Now;
                string a = now.ToString("dddd", CultureInfo.InvariantCulture), b = now.ToString("HH:mm"), cdate = now.ToString("dd MMMM", CultureInfo.InvariantCulture);
                if (day.Text != a)
                    day.Text = a;
                if (t.Text != b)
                    t.Text = b;
                if (d.Text != cdate)
                    d.Text = cdate;
            });
        }
        protected override void OnResponsive(double extraW, double extraH) { CenterBody(extraH); }
    }

    public class PerformanceWindow : GlassWindow
    {
        TextBlock cpu, gpu, mem, net;
        ProgressBar cp, gp, mp;
        long lastIdle, lastKernel, lastUser, lastRx, lastTx;
        DateTime last = DateTime.UtcNow;
        readonly GpuUsage gpuUsage = new GpuUsage();
        int cycles;
        public PerformanceWindow() : base(Program.Widget("performance", SystemParameters.WorkArea.Right - 370, 20, 350, 210), "性能")
        {
            var grid = new UniformGrid
            {
                Columns = 2,
                Rows = 2
            };
            cpu = Metric(grid, "CPU", out cp, "#72BDFF");
            gpu = Metric(grid, "GPU", out gp, "#FFC49A");
            mem = Metric(grid, "内存", out mp, "#86E3B8");
            ProgressBar unused;
            net = Metric(grid, "网络", out unused, "#9B9DFF");
            unused.Visibility = Visibility.Collapsed;
            Body.Children.Add(grid);
            Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
            ReadNet(out lastRx, out lastTx);
            Closed += delegate { gpuUsage.Dispose(); };
            Tick(2000, Update);
        }

        TextBlock Metric(Panel p, string name, out ProgressBar bar, string color)
        {
            var s = new StackPanel
            {
                Margin = new Thickness(0, 0, 16, 18)
            };
            s.Children.Add(Program.Text(name, 11, "#D0D7E1"));
            var v = Program.Text("—", 18);
            v.Margin = new Thickness(0, 5, 0, 6);
            s.Children.Add(v);
            bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 3,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color),
                Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(0)
            };
            s.Children.Add(bar);
            p.Children.Add(s);
            return v;
        }

        void ReadNet(out long rx, out long tx)
        {
            rx = 0;
            tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    try
                    {
                        var s = n.GetIPv4Statistics();
                        rx += s.BytesReceived;
                        tx += s.BytesSent;
                    }
                    catch
                    {
                    }
                }
        }

        bool sampling;
        async void Update()
        {
            if (sampling || WasClosed || (IsLoaded && (!IsVisible || State.Collapsed)))
                return;
            sampling = true;
            try
            {
                var data = await Task.Run(delegate
                {
                    long i, k, u;
                    Native.GetSystemTimes(out i, out k, out u);
                    long total = k - lastKernel + u - lastUser;
                    double value = total <= 0 ? 0 : Math.Max(0, Math.Min(100, 100.0 * (total - (i - lastIdle)) / total));
                    lastIdle = i;
                    lastKernel = k;
                    lastUser = u;
                    var m = new Native.MEMORY
                    {
                        length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORY))
                    };
                    Native.GlobalMemoryStatusEx(ref m);
                    long rx, tx;
                    ReadNet(out rx, out tx);
                    double sec = Math.Max(.1, (DateTime.UtcNow - last).TotalSeconds);
                    double down = Math.Max(0, rx - lastRx) / sec, up = Math.Max(0, tx - lastTx) / sec;
                    lastRx = rx;
                    lastTx = tx;
                    last = DateTime.UtcNow;
                    double gpuValue = -1;
                    if (cycles++ % 3 == 0)
                        try
                        {
                            gpuValue = gpuUsage.Sample();
                        }
                        catch
                        {
                        }

                    return new double[]
                    {
                        value,
                        m.load,
                        (m.totalPhys - m.availPhys) / 1073741824.0,
                        m.totalPhys / 1073741824.0,
                        down,
                        up,
                        gpuValue
                    };
                });
                if (WasClosed)
                    return;
                cp.Value = data[0];
                cpu.Text = data[0].ToString("0") + "%";
                mp.Value = data[1];
                mem.Text = data[1].ToString("0") + "%";
                mem.ToolTip = data[2].ToString("0.0") + " / " + data[3].ToString("0.0") + " GB";
                if (data[6] >= 0)
                {
                    gp.Value = data[6];
                    gpu.Text = data[6].ToString("0") + "%";
                }
                else if (gpu.Text == "—")
                    gpu.Text = "不可用";
                net.FontSize = 11;
                net.Text = "↓ " + Rate(data[4]) + "\n↑ " + Rate(data[5]);
            }
            catch (Exception e)
            {
                Program.Log(e);
            }
            finally
            {
                sampling = false;
            }
        }

        static string Rate(double n)
        {
            return n > 1048576 ? (n / 1048576).ToString("0.0") + " MB/s" : (n / 1024).ToString("0.0") + " KB/s";
        }
    }

    public class CalendarWindow : GlassWindow
    {
        DateTime date = DateTime.Today;
        StackPanel panel = new StackPanel();
        bool almanac;
        protected override void OnResponsive(double extraW, double extraH) { CenterBody(extraH); }
        public CalendarWindow(bool isAlmanac) : base(Program.Widget(isAlmanac ? "almanac" : "calendar", SystemParameters.WorkArea.Right - 370, isAlmanac ? 550 : 420, 350, isAlmanac ? 350 : 350), isAlmanac ? "黄历" : "日历")
        {
            almanac = isAlmanac;
            Body.Children.Add(panel);
            ContextMenu.Items.Insert(0, Item("回到今天", delegate
            {
                date = DateTime.Today;
                Render();
            }));
            Render();
        }

        public static string Lunar(DateTime dt)
        {
            try
            {
                var l = new Lunar.Solar(dt.Year, dt.Month, dt.Day).Lunar;
                return l.MonthInChinese + "月" + l.DayInChinese + " · " + l.YearInGanZhi + "年";
            }
            catch
            {
                return "日期超出农历支持范围";
            }
        }

        void Render()
        {
            panel.Children.Clear();
            var nav = new DockPanel();
            var prev = Program.Button("‹", delegate
            {
                date = almanac ? date.AddDays(-1) : date.AddMonths(-1);
                Render();
            });
            var next = Program.Button("›", delegate
            {
                date = almanac ? date.AddDays(1) : date.AddMonths(1);
                Render();
            });
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            nav.Children.Add(prev);
            nav.Children.Add(next);
            var h = Program.Text(date.ToString(almanac ? "yyyy年 M月 d日" : "yyyy年 M月"), 16);
            h.VerticalAlignment = VerticalAlignment.Center;
            h.TextAlignment = TextAlignment.Center;
            nav.Children.Add(h);
            panel.Children.Add(nav);
            if (almanac)
            {
                var l = new Lunar.Solar(date.Year, date.Month, date.Day).Lunar;
                var a = Program.Text(Lunar(date), 20);
                a.Margin = new Thickness(0, 14, 0, 8);
                panel.Children.Add(a);
                panel.Children.Add(Program.Text(l.DayInGanZhi + "日 · " + l.DayShengXiao + " · " + l.JieQi, 13, "#BBD0E5"));
                var yi = Program.Text("宜  " + string.Join(" · ", l.DayYi), 13, "#9AE8BE");
                yi.Margin = new Thickness(0, 16, 0, 10);
                panel.Children.Add(yi);
                panel.Children.Add(Program.Text("忌  " + string.Join(" · ", l.DayJi), 13, "#F2B5AF"));
                var note = Program.Text("传统历法参考 · lunar-csharp", 10, "#A2AAB8");
                note.Margin = new Thickness(0, 18, 0, 0);
                panel.Children.Add(note);
            }
            else
            {
                var grid = new UniformGrid
                {
                    Columns = 7,
                    Rows = 7,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                foreach (var day in new[]
                {
                    "一",
                    "二",
                    "三",
                    "四",
                    "五",
                    "六",
                    "日"
                }

                )
                {
                    var t = Program.Text(day, 12, "#AABED1");
                    t.TextAlignment = TextAlignment.Center;
                    grid.Children.Add(t);
                }

                var first = new DateTime(date.Year, date.Month, 1);
                int offset = ((int)first.DayOfWeek + 6) % 7;
                for (int i = 0; i < 42; i++)
                {
                    int day = i - offset + 1;
                    var t = Program.Text(day > 0 && day <= DateTime.DaysInMonth(date.Year, date.Month) ? day.ToString() : "", 13);
                    t.TextAlignment = TextAlignment.Center;
                    t.Margin = new Thickness(0, 4, 0, 4);
                    if (day == DateTime.Today.Day && date.Year == DateTime.Today.Year && date.Month == DateTime.Today.Month)
                    {
                        t.Foreground = new SolidColorBrush(Color.FromRgb(130, 210, 255));
                        t.FontWeight = FontWeights.Bold;
                    }

                    grid.Children.Add(t);
                }

                panel.Children.Add(grid);
                panel.Children.Add(Program.Text(Lunar(DateTime.Today), 12, "#BBD0E5"));
            }
        }
    }

    public class WeatherWindow : GlassWindow
    {
        TextBlock temp = Program.Text("—", 40), description = Program.Text("自动定位中…", 14), detail = Program.Text("", 12, "#C5D3E2");
        readonly IWeatherService weather;
        readonly System.Threading.CancellationTokenSource cancel = new System.Threading.CancellationTokenSource();
        WeatherReading lastReading;
        bool busy;
        DateTime retryAfter;
        readonly System.Windows.Controls.Primitives.UniformGrid forecast=new System.Windows.Controls.Primitives.UniformGrid { Rows=1 };
        int forecastDays;
        /// <summary>How many forecast days fit: none at the default height, up to 7 when wider/taller.</summary>
        internal static int ForecastFor(double extraW,double extraH) { return extraH<70 ? 0 : Math.Max(3,Math.Min(7,(int)((300+extraW)/60))); }
        internal int TestForecastDays { get { return forecastDays; } }
        protected override void OnResponsive(double extraW,double extraH)
        {
            int n=ForecastFor(extraW,extraH); if(n==forecastDays) return; forecastDays=n; RenderForecast();
        }
        void RenderForecast()
        {
            forecast.Children.Clear();
            var days=lastReading==null?null:lastReading.Days;
            int n=days==null?0:Math.Min(forecastDays,days.Count);
            forecast.Visibility=n>0?Visibility.Visible:Visibility.Collapsed; forecast.Columns=Math.Max(1,n);
            for(int i=0;i<n;i++)
            {
                var d=days[i]; var cell=new StackPanel { Margin=new Thickness(2,0,2,0) };
                var name=Program.Text(i==0?"今天":"周"+"日一二三四五六"[(int)d.Date.DayOfWeek],11,"#C8D4E2"); name.HorizontalAlignment=HorizontalAlignment.Center;
                var desc=Program.Text(Description(d.Code),12); desc.HorizontalAlignment=HorizontalAlignment.Center; desc.Margin=new Thickness(0,3,0,2);
                var range=Program.Text(d.Max.ToString("0")+"° / "+d.Min.ToString("0")+"°",11,"#A8B7C9"); range.HorizontalAlignment=HorizontalAlignment.Center;
                cell.Children.Add(name); cell.Children.Add(desc); cell.Children.Add(range);
                forecast.Children.Add(new Border { CornerRadius=new CornerRadius(10),Padding=new Thickness(2,6,2,6),Margin=new Thickness(2,0,2,0),Background=new SolidColorBrush(Color.FromArgb(18,255,255,255)),Child=cell });
            }
        }
        internal void TestShowReading(WeatherReading r) { lastReading=r; RenderForecast(); }
        public WeatherWindow() : this(WeatherService.Shared) { }
        internal WeatherWindow(IWeatherService service) : base(Program.Widget("weather", SystemParameters.WorkArea.Right - 370, 420, 350, 250), "天气")
        {
            weather=service;
            var p=new StackPanel(); p.Children.Add(temp); p.Children.Add(description);
            detail.Margin=new Thickness(0,10,0,8); p.Children.Add(detail);
            forecast.Margin=new Thickness(0,0,0,10); forecast.Visibility=Visibility.Collapsed; p.Children.Add(forecast);
            p.Children.Add(Program.Text("天气：Open-Meteo · 定位：IPwho.is",10,"#A8B7C9")); Body.Children.Add(p);
            ContextMenu.Items.Insert(0,Item("重新定位并刷新",delegate { _ = FetchAsync(true); }));
            Tick(60000,delegate { _ = FetchAsync(false); });
            Closed+=delegate { cancel.Cancel(); };
        }
        internal async Task FetchAsync(bool force)
        {
            if(busy || WasClosed) return;
            if(!Program.S.WeatherOnline)
            {
                temp.Text="未联网"; temp.FontSize=28; description.Text="在设置中开启自动天气联网";
                detail.Text="按网络 IP 获取近似城市，不使用手选城市。"; return;
            }
            if(!force && DateTime.UtcNow<retryAfter) return;
            busy=true; description.Text="正在自动定位并获取天气…";
            try
            {
                var reading=await weather.ReadAsync(force,cancel.Token);
                if(WasClosed || !Program.S.WeatherOnline) return;
                lastReading=reading; temp.FontSize=40; temp.Text=reading.Temperature.ToString("0°");
                description.Text=reading.City+" · "+Description(reading.Code);
                detail.Text="湿度 "+reading.Humidity.ToString("0")+"%  ·  风速 "+reading.Wind.ToString("0.#")+" km/h\n天气时间 "+reading.Time+" · 自动定位（IP）";
                detail.ToolTip="城市由网络 IP 推断，代理或 VPN 可能影响结果。"; retryAfter=DateTime.UtcNow.AddMinutes(20);
                RenderForecast();
            }
            catch(Exception error)
            {
                if(WasClosed || cancel.IsCancellationRequested) return;
                // Avoid recording URLs, IP responses or coordinates in user logs.
                description.Text=lastReading==null?"自动天气暂不可用":lastReading.City+" · 更新失败";
                detail.Text=lastReading==null?"请检查网络，右键重新定位并刷新。":"保留上次天气 · "+lastReading.Time+"\n右键可重新定位并刷新。";
                detail.ToolTip=error is OperationCanceledException?"请求超时":"定位或天气服务暂不可用";
                retryAfter=DateTime.UtcNow.AddMinutes(5);
            }
            finally { busy=false; }
        }
        internal string TestDescription { get { return description.Text; } }
        internal string TestTemperature { get { return temp.Text; } }

        public static string Description(int code)
        {
            if (code == 0)
                return "晴";
            if (code <= 3)
                return "多云";
            if (code <= 48)
                return "雾";
            if (code <= 57)
                return "毛毛雨";
            if (code <= 67)
                return "雨";
            if (code <= 77)
                return "雪";
            if (code <= 82)
                return "阵雨";
            if (code <= 86)
                return "阵雪";
            return "雷雨";
        }

    }
}
