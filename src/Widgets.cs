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
        internal const double MinScale = .6, MaxScale = 4;      // 2.2.2: MaxScale follows the largest size (MaxSizeFactor) so content and frame keep growing together

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
                return WidgetLook.ContentScale(CustomW, CustomH, baseW, baseH, MinScale, MaxScale);
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
            if (scaler != null) { ApplyEdges(); OnResponsive(ExtraWidth, ExtraHeight); ApplyCrispText(); }
        }
        bool collapsedApplied;

        // ---- 2.2.3 sharp text in scaled widgets (see CrispText)
        bool crispQueued, crispHooked;
        internal void ApplyCrispText()
        {
            if (scaler == null || Root == null) return;
            CrispText.Apply(Root, scaler.ScaleX);
            if (!crispHooked)
            {
                crispHooked = true;
                // text that is added or restyled later (lists, ticking labels, widgets that set a font size in OnResponsive) is picked up after the next layout pass
                Root.LayoutUpdated += delegate
                {
                    if (crispQueued || scaler == null || !CrispText.Needed(scaler.ScaleX) || CrispText.TestDisable) return;
                    crispQueued = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { crispQueued = false; if (scaler != null) CrispText.Apply(Root, scaler.ScaleX); }));
                };
            }
        }

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
            // 2.2.2: the saved position stays in State; this only moves the window into view (it used to compare DIPs with pixels and a work area that the
            // top taskbar changes). Dragging the widget later saves the new place.
            if (!PositionGuard.Reachable(new Point(Left, Top), PositionGuard.Screens(null)))
            {
                // 2.2.4: not the top-left default: the nearest fully visible place (the saved value stays, and comes back with the monitor)
                var near = PositionGuard.Clamp(new Point(Left, Top), new Size(Math.Max(180, state.Width), Math.Max(100, state.Height)), PositionGuard.WorkAreas(null));
                Left = near.X;
                Top = near.Y;
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
                if (ready && !restoring)
                {
                    if (PositionGuard.IsSystemMove(dragging, Mouse.LeftButton == MouseButtonState.Pressed, DateTime.UtcNow < settleUntil)) ScheduleReassert();
                    else { State.X = Left; State.Y = Top; RecordPixels(); Program.SaveSoon(); }
                }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayChanged = delegate { try { Dispatcher.BeginInvoke(new Action(NoteSystemChange)); } catch { } };
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
            ApplyLook();
            if (AcceptsBackgroundDrop)
            {
                AllowDrop = true;
                DragOver += delegate (object sender, DragEventArgs e) { e.Effects = WidgetLook.Droppable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
                Drop += delegate (object sender, DragEventArgs e) { if (DropBackground(e.Data)) Toast("\uE710", "已设置背景"); e.Handled = true; };
            }
            Loaded += delegate
            {
                ready = true;
                ApplyEdges();
                if (!Program.Preview) { settleUntil = DateTime.UtcNow.AddMilliseconds(PositionGuard.SettleMs); SchedulePixelCheck(); }
                SetCollapsed();
                UpdatePaper();
                var h = new WindowInteropHelper(this).Handle;
                if (!Program.Preview)
                    Native.SetLong(h, -20, new IntPtr(Native.GetLong(h, -20).ToInt64() | 0x80));
                Native.SetWindowPos(h, new IntPtr(Topmost ? -1 : 1), 0, 0, 0, 0, 0x13);
                var source = HwndSource.FromHwnd(h);
                if (source != null) { source.AddHook(PinHook); source.AddHook(SystemChangeHook); }
            };
            Closed += delegate
            {
                if (displayChanged != null) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayChanged;
                if (reassert != null) reassert.Stop();
                if (pixelCheck != null) pixelCheck.Stop();
                WasClosed = true;
                if (Timer != null)
                    Timer.Stop();
            };
        }

        // ---- 2.2.2 look: edge metrics, 无边框, custom background ----
        double edgeDelta; bool framelessApplied;
        internal double EdgeFactorNow { get { return WidgetLook.EdgeFactor(ExtraWidth, ExtraHeight, double.IsNaN(baseW) ? 1 : baseW, double.IsNaN(baseH) ? 1 : baseH); } }
        /// <summary>The outer padding grows with the room the content scale cannot use (tall / wide widgets). Only widgets that have a padding at all take part.</summary>
        void ApplyEdges()
        {
            if (!IsLoaded || Layout == null) return;
            double delta = WidgetLook.Pad(EdgeFactorNow) - WidgetLook.PadBase;
            if (Math.Abs(delta - edgeDelta) < .01) return;
            var m = Layout.Margin; double l = m.Left - edgeDelta, t = m.Top - edgeDelta, r = m.Right - edgeDelta, b = m.Bottom - edgeDelta;
            if (l < 10 && t < 10) { edgeDelta = 0; return; }
            Layout.Margin = new Thickness(l + delta, t + delta, r + delta, b + delta); edgeDelta = delta;
        }
        internal bool SupportsLook { get { return !(this is DockWindow || this is LaunchpadWindow); } }
        internal bool SupportsFrameless { get { return SupportsLook && !(this is NoteWindow); } }
        protected virtual bool AcceptsBackgroundDrop { get { return !(this is AlbumWindow || this is OrganizerWindow || this is NoteWindow || this is LauncherWindow || this is DockWindow || this is LaunchpadWindow); } }
        Grid bgLayer; Border bgPicture; Border bgDimmer; Brush rootBackground; string bgLoaded;
        internal bool IsFrameless { get { return State.Frameless && SupportsFrameless; } }
        internal bool HasBackgroundImage { get { return bgLayer != null && bgPicture != null && PictureBrush.ImageSource != null; } }
        ImageBrush PictureBrush { get { return (ImageBrush)bgPicture.Background; } }
        internal Border TestBackgroundImage { get { return bgPicture; } }
        internal ImageBrush TestBackgroundBrush { get { return bgPicture == null ? null : PictureBrush; } }
        internal Border TestFrame { get { return Frame; } }
        internal Panel TestLayout { get { return Layout; } }
        internal Panel TestRoot { get { return Root; } }

        /// <summary>Applies Frameless / TextShadow / background picture from the saved state. Safe to call at any time.</summary>
        internal void ApplyLook()
        {
            if (!SupportsLook || Frame == null) return;
            bool frameless = IsFrameless;
            if (frameless || framelessApplied)
            {
                if (!framelessApplied) rootBackground = Root.Background;
                if (Surface != null) Surface.Visibility = frameless ? Visibility.Collapsed : Visibility.Visible;
                Frame.BorderThickness = frameless ? new Thickness(0) : new Thickness(Surface != null || State.MaterialSelected ? 1 : 0);
                Root.Background = frameless ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : rootBackground;       // still catches the pointer (drag) with nothing painted
                Layout.Effect = frameless && !State.NoTextShadow ? WidgetLook.TextShadow() : null;
                framelessApplied = frameless;
            }
            // background picture
            string path = ResolveBackground();
            if (path == null) { if (bgLayer != null) { Root.Children.Remove(bgLayer); bgLayer = null; bgPicture = null; bgDimmer = null; } bgLoaded = null; return; }
            if (bgLayer == null)
            {
                bgPicture = new Border { IsHitTestVisible = false, Background = new ImageBrush { AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center, Stretch = Stretch.UniformToFill } };
                RenderOptions.SetBitmapScalingMode(bgPicture, BitmapScalingMode.HighQuality);
                bgDimmer = new Border { IsHitTestVisible = false };
                bgLayer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
                bgLayer.Children.Add(bgPicture); bgLayer.Children.Add(bgDimmer);
                int at = Root.Children.IndexOf(Layout); Root.Children.Insert(at < 0 ? 0 : at, bgLayer);
            }
            if (bgLoaded != path) { PictureBrush.ImageSource = WidgetLook.Load(path); bgLoaded = path; if (PictureBrush.ImageSource == null) { bgLayer.Visibility = Visibility.Collapsed; return; } }
            bgLayer.Visibility = Visibility.Visible;
            PictureBrush.Stretch = WidgetLook.StretchFor(State.BgFit); bgPicture.Opacity = WidgetLook.ClampOpacity(State.BgOpacity);
            bgDimmer.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(WidgetLook.ClampDim(State.BgDim) * 255), 0, 0, 0));
        }
        string ResolveBackground()
        {
            if (string.IsNullOrEmpty(State.BgImage)) return null;
            string p = Path.IsPathRooted(State.BgImage) ? State.BgImage : Path.Combine(WidgetLook.Folder, State.BgImage);
            return File.Exists(p) ? p : null;
        }
        internal void SetFrameless(bool on) { State.Frameless = on; ApplyLook(); Program.Save(); }
        internal void SetTextShadow(bool on) { State.NoTextShadow = !on; ApplyLook(); Program.Save(); }
        internal bool SetBackground(string storedPath)
        {
            if (storedPath == null) return false;
            string old = ResolveBackground();
            State.BgImage = Path.GetFileName(storedPath); if (string.IsNullOrEmpty(State.BgFit)) State.BgFit = "cover";
            if (State.BgOpacity <= 0) State.BgOpacity = 1;
            bgLoaded = null; ApplyLook(); Program.Save();
            WidgetLook.Prune(State.Id, storedPath);
            return HasBackgroundImage;
        }
        internal bool SetBackgroundFile(string file) { return SetBackground(WidgetLook.CopyIn(State.Id, file)); }
        internal bool SetBackgroundBitmap(BitmapSource bmp) { return SetBackground(WidgetLook.CopyIn(State.Id, bmp)); }
        internal void ClearBackground()
        {
            string old = ResolveBackground(); State.BgImage = ""; ApplyLook(); Program.Save();
            WidgetLook.Prune(State.Id, null);
        }
        internal void SetBackgroundFit(string fit) { State.BgFit = WidgetLook.NormalizeFit(fit); ApplyLook(); Program.Save(); }
        internal void SetBackgroundOpacity(double v) { State.BgOpacity = WidgetLook.ClampOpacity(v); ApplyLook(); Program.Save(); }
        internal void SetBackgroundDim(double v) { State.BgDim = WidgetLook.ClampDim(v); ApplyLook(); Program.Save(); }
        internal Func<string> BackgroundDialog;      // test hook
        internal void PickBackground()
        {
            string name;
            if (BackgroundDialog != null) name = BackgroundDialog();
            else using (var host = new DialogHost())
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.jfif|所有文件|*.*" };
                name = d.ShowDialog(host.Owner) == true ? d.FileName : null;
            }
            if (name == null) return;
            if (!SetBackgroundFile(name)) Toast("\uE783", "无法使用这张图片");
        }
        internal bool PasteBackground()
        {
            var bmp = WidgetLook.ClipboardImage();
            if (bmp != null && SetBackgroundBitmap(bmp)) { Toast("\uE710", "已设置背景"); return true; }
            if (WidgetLook.ClipboardFiles().Where(f => AlbumLogic.IsImage(f)).Any(f => SetBackgroundFile(f))) { Toast("\uE710", "已设置背景"); return true; }
            Toast("\uE783", "剪贴板里没有图片"); return false;
        }
        internal bool DropBackground(IDataObject d)
        {
            try
            {
                var files = d.GetDataPresent(DataFormats.FileDrop) ? d.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null) return files.Where(x => AlbumLogic.IsImage(x)).Any(x => SetBackgroundFile(x));
                var bmp = WidgetLook.BitmapFrom(d); return bmp != null && SetBackgroundBitmap(bmp);
            }
            catch (Exception e) { Program.Log(e); return false; }
        }
        void AddLookMenu(ContextMenu c, int at)
        {
            if (!SupportsLook) return;
            var bg = new MenuItem { Header = "自定义背景" };
            var pick = Item("选择图片…", PickBackground);
            var paste = Item("从剪贴板粘贴", delegate { PasteBackground(); });
            var fit = new MenuItem { Header = "填充方式" };
            foreach (var f in WidgetLook.Fits) { string id = f; var it = Item(WidgetLook.FitName(f), delegate { SetBackgroundFit(id); }); it.Tag = id; fit.Items.Add(it); }
            var opacity = new MenuItem { Header = "图片不透明度" };
            foreach (var o in new[] { 100, 80, 60, 40, 20 }) { int v = o; var it = Item(v + "%", delegate { SetBackgroundOpacity(v / 100.0); }); it.Tag = v; opacity.Items.Add(it); }
            var dim = new MenuItem { Header = "压暗（让文字更清楚）" };
            foreach (var o in new[] { 0, 15, 25, 40, 60 }) { int v = o; var it = Item(v == 0 ? "不压暗" : v + "%", delegate { SetBackgroundDim(v / 100.0); }); it.Tag = v; dim.Items.Add(it); }
            var remove = Item("移除背景", ClearBackground);
            bg.Items.Add(pick); bg.Items.Add(paste); bg.Items.Add(new Separator()); bg.Items.Add(fit); bg.Items.Add(opacity); bg.Items.Add(dim); bg.Items.Add(new Separator()); bg.Items.Add(remove);
            bg.SubmenuOpened += delegate
            {
                bool has = !string.IsNullOrEmpty(State.BgImage);
                fit.IsEnabled = opacity.IsEnabled = dim.IsEnabled = remove.IsEnabled = has;
                foreach (MenuItem it in fit.Items.OfType<MenuItem>()) it.IsChecked = (string)it.Tag == WidgetLook.NormalizeFit(State.BgFit);
                foreach (MenuItem it in opacity.Items.OfType<MenuItem>()) it.IsChecked = Math.Abs((int)it.Tag / 100.0 - WidgetLook.ClampOpacity(State.BgOpacity)) < .02;
                foreach (MenuItem it in dim.Items.OfType<MenuItem>()) it.IsChecked = Math.Abs((int)it.Tag / 100.0 - WidgetLook.ClampDim(State.BgDim)) < .02;
            };
            c.Items.Insert(at, bg);
            if (SupportsFrameless)
            {
                var frameless = Item("无边框 / 透明背景", delegate { SetFrameless(!State.Frameless); });
                var shadow = Item("文字阴影（无边框时）", delegate { SetTextShadow(State.NoTextShadow); });
                frameless.ToolTip = "只显示内容，不画外框、面板和模糊；仍可拖动、调整大小和锁定";
                c.Items.Insert(at + 1, frameless); c.Items.Insert(at + 2, shadow);
                c.Opened += delegate { frameless.IsChecked = State.Frameless; shadow.IsChecked = !State.NoTextShadow; shadow.IsEnabled = State.Frameless; };
            }
        }

        // ---- 2.2.2 position stability (see PositionGuard) ----
        DateTime settleUntil; bool restoring; EventHandler displayChanged; System.Windows.Threading.DispatcherTimer reassert;
        internal int PositionRestores;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct PxRect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out PxRect r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        int CurrentDpi() { try { return (int)Math.Round(VisualTreeHelper.GetDpi(this).PixelsPerInchX); } catch { return 0; } }
        /// <summary>2.2.4: remember where the window really is in device pixels (and at which DPI).</summary>
        internal void RecordPixels()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle; PxRect r;
                if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return;
                State.PxX = r.Left; State.PxY = r.Top; State.PxDpi = CurrentDpi();
            }
            catch { }
        }
        System.Windows.Threading.DispatcherTimer pixelCheck;
        /// <summary>After the settle time: if the window is not where the saved device pixels say (different monitors scaled differently put the same DIP numbers elsewhere), put it back.</summary>
        void SchedulePixelCheck()
        {
            if (Program.Preview || !State.Enabled) return;
            pixelCheck = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(PositionGuard.SettleMs + 400) };
            pixelCheck.Tick += delegate
            {
                pixelCheck.Stop();
                if (WasClosed || dragging || Mouse.LeftButton == MouseButtonState.Pressed) return;
                try
                {
                    var h = new WindowInteropHelper(this).Handle; PxRect r;
                    if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return;
                    var screens = System.Windows.Forms.Screen.AllScreens.Select(sc => new Rect(sc.Bounds.Left, sc.Bounds.Top, sc.Bounds.Width, sc.Bounds.Height)).ToList();
                    var at = PositionGuard.PixelFix(State.PxX, State.PxY, State.PxDpi, CurrentDpi(), r.Left, r.Top, screens);
                    if (!at.HasValue) return;
                    restoring = true;
                    try { SetWindowPos(h, IntPtr.Zero, (int)at.Value.X, (int)at.Value.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); PositionRestores++; } finally { restoring = false; }
                }
                catch (Exception e) { Program.Log(e); }
            };
            pixelCheck.Start();
        }
        /// <summary>The system is changing displays / scaling / work area: for a moment location changes that the user did not make are not saved, then undone.</summary>
        internal void NoteSystemChange() { settleUntil = DateTime.UtcNow.AddMilliseconds(PositionGuard.ChangeMs); ScheduleReassert(); }
        void ScheduleReassert()
        {
            if (WasClosed) return;
            if (reassert == null)
            {
                reassert = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
                reassert.Tick += delegate
                {
                    reassert.Stop();
                    if (WasClosed || dragging || Mouse.LeftButton == MouseButtonState.Pressed) return;
                    if (DateTime.UtcNow < settleUntil) { reassert.Interval = settleUntil - DateTime.UtcNow + TimeSpan.FromMilliseconds(50); reassert.Start(); return; }
                    var at = PositionGuard.Reassert(new Point(State.X, State.Y), new Point(Left, Top), PositionGuard.Screens(this));
                    if (!at.HasValue) return;
                    restoring = true;
                    try { Left = at.Value.X; Top = at.Value.Y; PositionRestores++; } finally { restoring = false; }
                };
            }
            reassert.Stop();
            reassert.Interval = TimeSpan.FromMilliseconds(PositionGuard.ReassertMs); reassert.Start();
        }
        IntPtr SystemChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x2E0 || msg == 0x7E || msg == 0x1A) NoteSystemChange();      // WM_DPICHANGED, WM_DISPLAYCHANGE, WM_SETTINGCHANGE
            else if (msg == 0x11 || msg == 0x16) Program.FlushSave();               // WM_QUERYENDSESSION, WM_ENDSESSION: Windows is logging off / shutting down / restarting
            return IntPtr.Zero;
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
            AddLookMenu(c, c.Items.Count);
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
                create.Items.Add(Item(kind.Value, delegate { Program.NewWidget(id, PlaceMode.LastRightClick); }));
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
        /// <summary>Puts a widget that is not shown yet at this position (and remembers it).</summary>
        internal void PlaceAt(Point p) { Left = p.X; Top = p.Y; State.X = p.X; State.Y = p.Y; }
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
            ApplyLook();
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
        readonly TextBlock source = Program.Text("", 10, "#A8B7C9");
        int failures;
        System.Windows.Threading.DispatcherTimer netTimer;
        DateTime lastNetworkRefresh = DateTime.MinValue;
        static readonly Random Jitter = new Random();
        /// <summary>Delay before the next automatic refresh: ~20 minutes after a success (±10%); 1, 2, 4, 8, then 15 minutes (±20%) after failures.</summary>
        internal static TimeSpan NextDelay(int failureCount, double random)
        {
            if (failureCount <= 0) return TimeSpan.FromMinutes(20 * (0.9 + 0.2 * random));
            double[] steps = { 1, 2, 4, 8, 15 };
            return TimeSpan.FromMinutes(steps[Math.Min(failureCount, steps.Length) - 1] * (0.8 + 0.4 * random));
        }
        internal WeatherWindow(IWeatherService service) : base(Program.Widget("weather", SystemParameters.WorkArea.Right - 370, 420, 350, 250), "天气")
        {
            weather=service;
            var p=new StackPanel(); p.Children.Add(temp); p.Children.Add(description);
            detail.Margin=new Thickness(0,10,0,8); p.Children.Add(detail);
            forecast.Margin=new Thickness(0,0,0,10); forecast.Visibility=Visibility.Collapsed; p.Children.Add(forecast);
            p.Children.Add(source); Body.Children.Add(p);
            ContextMenu.Items.Insert(0,Item("重新定位并刷新",delegate { weather.NetworkChanged(); _ = FetchAsync(true); }));
            Tick(60000,delegate { _ = FetchAsync(false); });
            var cachedReading=weather.Cached;
            if(cachedReading!=null && Program.S.WeatherOnline) Show(cachedReading);   // last good data right away, marked with its age
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged+=OnNetworkEvent;
            Microsoft.Win32.SystemEvents.PowerModeChanged+=OnPowerEvent;
            Closed+=delegate
            {
                cancel.Cancel();
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged-=OnNetworkEvent;
                Microsoft.Win32.SystemEvents.PowerModeChanged-=OnPowerEvent;
                if(netTimer!=null) netTimer.Stop();
            };
        }
        void OnNetworkEvent(object sender,EventArgs e) { try { Dispatcher.BeginInvoke(new Action(delegate { ScheduleRefresh(6); })); } catch { } }
        void OnPowerEvent(object sender,Microsoft.Win32.PowerModeChangedEventArgs e) { if(e.Mode==Microsoft.Win32.PowerModes.Resume) { try { Dispatcher.BeginInvoke(new Action(delegate { ScheduleRefresh(12); })); } catch { } } }
        /// <summary>Network changed or the PC woke up: wait for the network to settle, then re-locate and refresh (at most once a minute).</summary>
        void ScheduleRefresh(int seconds)
        {
            if(WasClosed) return;
            if(netTimer==null) { netTimer=new System.Windows.Threading.DispatcherTimer(); netTimer.Tick+=delegate
            {
                netTimer.Stop(); if(WasClosed || !Program.S.WeatherOnline) return;
                if(DateTime.UtcNow-lastNetworkRefresh<TimeSpan.FromSeconds(60)) return;
                lastNetworkRefresh=DateTime.UtcNow; weather.NetworkChanged(); retryAfter=DateTime.MinValue; _ = FetchAsync(false);
            }; }
            netTimer.Stop(); netTimer.Interval=TimeSpan.FromSeconds(seconds); netTimer.Start();
        }
        static string Age(WeatherReading r) { return r.Updated==default(DateTime) ? "" : "更新于 "+r.Updated.ToString("HH:mm"); }
        void Show(WeatherReading reading)
        {
            lastReading=reading; temp.FontSize=40; temp.Text=reading.Temperature.ToString("0°");
            description.Text=reading.City+" · "+Description(reading.Code);
            detail.Text="湿度 "+reading.Humidity.ToString("0")+"%  ·  风速 "+reading.Wind.ToString("0.#")+" km/h\n"+(reading.Stale?Age(reading)+"（离线，显示上次数据）":Age(reading)+(string.IsNullOrEmpty(reading.Time)?"":" · 天气时间 "+reading.Time));
            string kind=reading.Place==null?"":reading.Place.Kind, loc=string.IsNullOrEmpty(reading.PlaceSource)?"自动":reading.PlaceSource;
            source.Text="天气："+(string.IsNullOrEmpty(reading.Source)?"—":reading.Source)+" · 定位："+loc;
            detail.ToolTip=kind=="ip"?"城市由网络 IP 推断，只能到城市级别，代理或 VPN 可能影响结果。可在 设置 → 外观与材质 手动选择区县。"
                :kind=="system"?"位置来自 Windows 定位服务（在 Windows 设置 → 隐私和安全性 → 位置 中管理）。"
                :kind=="manual"?"位置为你在设置里手动选择的区县。":null;
            RenderForecast();
        }
        internal async Task FetchAsync(bool force)
        {
            if(busy || WasClosed) return;
            if(!Program.S.WeatherOnline)
            {
                temp.Text="未联网"; temp.FontSize=28; description.Text="在设置中开启自动天气联网";
                detail.Text="可手动选择区县，或使用 Windows 定位 / 网络 IP 自动定位。"; source.Text=""; return;
            }
            if(!force && DateTime.UtcNow<retryAfter) return;
            busy=true; if(lastReading==null) description.Text="正在自动定位并获取天气…";
            try
            {
                var reading=await weather.ReadAsync(force,cancel.Token);
                if(WasClosed || !Program.S.WeatherOnline) return;
                failures=0; Show(reading); retryAfter=DateTime.UtcNow+NextDelay(0,Jitter.NextDouble());
            }
            catch(Exception error)
            {
                if(WasClosed || cancel.IsCancellationRequested) return;
                failures++; var wait=NextDelay(failures,Jitter.NextDouble()); retryAfter=DateTime.UtcNow+wait;
                string why=error is WeatherException?string.Join("；",((WeatherException)error).Reasons.Where(x=>x.Contains("失败")).Take(3)):"";
                if(lastReading==null)
                {
                    temp.Text="—"; temp.FontSize=40; description.Text="自动天气暂不可用";
                    detail.Text="已尝试多个天气来源都没有成功，约 "+Math.Max(1,(int)Math.Round(wait.TotalMinutes))+" 分钟后自动重试。\n也可右键重新定位并刷新。";
                    source.Text=error is WeatherException && !string.IsNullOrEmpty(error.Message)?error.Message:"";
                }
                else
                {
                    // keep showing the last good reading, just say how old it is — no error card
                    lastReading.Stale=true; temp.FontSize=40; temp.Text=lastReading.Temperature.ToString("0°");
                    description.Text=lastReading.City+" · "+Description(lastReading.Code);
                    detail.Text="湿度 "+lastReading.Humidity.ToString("0")+"%  ·  风速 "+lastReading.Wind.ToString("0.#")+" km/h\n"+Age(lastReading)+"（网络异常，约 "+Math.Max(1,(int)Math.Round(wait.TotalMinutes))+" 分钟后重试）";
                    RenderForecast();
                }
                detail.ToolTip=(error is OperationCanceledException?"请求超时":"定位或天气服务暂不可用")+(why.Length>0?"：\n"+why.Replace("；","\n"):"");
            }
            finally { busy=false; }
        }
        internal string TestDescription { get { return description.Text; } }
        internal string TestDetail { get { return detail.Text; } }
        internal string TestSource { get { return source.Text; } }
        internal string TestTemperature { get { return temp.Text; } }

        public static string Description(int code)
        {
            if (code == 101)
                return "沙尘";
            if (code == 102)
                return "霾";
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
