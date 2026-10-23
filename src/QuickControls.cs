using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClearDesk
{
    // ---------------------------------------------------------------- Core Audio (default render endpoint)
    internal static class CoreAudio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }

        [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public int Id; }
        [StructLayout(LayoutKind.Sequential)] internal struct PropVariant { public short vt; short r1, r2, r3; public IntPtr p; public IntPtr p2; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int c);
            [PreserveSig] int GetAt(int i, out PropertyKey k);
            [PreserveSig] int GetValue(ref PropertyKey k, out PropVariant v);
        }
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
            [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
            [PreserveSig] int GetChannelCount(out uint c);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
        [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }
        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
        }
        // Undocumented but long-stable interface used by Windows' own sound panel to switch the default device.
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat(); [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod(); [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        static readonly PropertyKey FriendlyName = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };

        internal static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }

        internal static string Name(IMMDevice d)
        {
            IPropertyStore store;
            if (d == null || d.OpenPropertyStore(0, out store) != 0 || store == null) return null;
            try
            {
                var key = FriendlyName; PropVariant v;
                if (store.GetValue(ref key, out v) != 0) return null;
                try { return v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null; } finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        internal static List<KeyValuePair<string, string>> Outputs(IMMDeviceEnumerator e)
        {
            var list = new List<KeyValuePair<string, string>>();
            IMMDeviceCollection c;
            if (e.EnumAudioEndpoints(0, 1, out c) != 0 || c == null) return list;
            uint n; c.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice d;
                if (c.Item(i, out d) != 0 || d == null) continue;
                string id; d.GetId(out id);
                list.Add(new KeyValuePair<string, string>(id, Name(d) ?? "音频设备"));
                Marshal.ReleaseComObject(d);
            }
            Marshal.ReleaseComObject(c);
            return list;
        }

        internal static bool SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigCom();
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(id, role) == 0;
                Marshal.ReleaseComObject(pc);
                return ok;
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }
    }

    // Live view of the default output device: volume, mute and name, updated from Core Audio callbacks.
    internal sealed class OutputVolume : CoreAudio.IAudioEndpointVolumeCallback, CoreAudio.IMMNotificationClient, IDisposable
    {
        static Guid self = Guid.NewGuid();
        readonly Dispatcher dispatcher;
        CoreAudio.IMMDeviceEnumerator enumerator;
        CoreAudio.IMMDevice device;
        CoreAudio.IAudioEndpointVolume endpoint;
        bool disposed;
        public string DeviceId { get; private set; }
        public string DeviceName { get; private set; }
        public float Level { get; private set; }
        public bool Muted { get; private set; }
        public bool Available { get { return endpoint != null; } }
        public event Action Changed;

        public OutputVolume(Dispatcher d)
        {
            dispatcher = d;
            try { enumerator = CoreAudio.Enumerator(); enumerator.RegisterEndpointNotificationCallback(this); } catch (Exception e) { Program.Log(e); }
            Attach();
        }

        void Attach()
        {
            Detach();
            if (enumerator == null) return;
            try
            {
                CoreAudio.IMMDevice d;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out d) != 0 || d == null) { DeviceName = null; return; }
                device = d;
                string id; d.GetId(out id); DeviceId = id; DeviceName = CoreAudio.Name(d) ?? "默认输出设备";
                var iid = typeof(CoreAudio.IAudioEndpointVolume).GUID; object o;
                if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0)
                {
                    endpoint = (CoreAudio.IAudioEndpointVolume)o;
                    endpoint.RegisterControlChangeNotify(this);
                    Read();
                }
            }
            catch (Exception e) { Program.Log(e); }
        }

        void Detach()
        {
            if (endpoint != null) { try { endpoint.UnregisterControlChangeNotify(this); } catch { } Marshal.ReleaseComObject(endpoint); endpoint = null; }
            if (device != null) { Marshal.ReleaseComObject(device); device = null; }
        }

        void Read()
        {
            if (endpoint == null) return;
            float l; bool m;
            if (endpoint.GetMasterVolumeLevelScalar(out l) == 0) Level = l;
            if (endpoint.GetMute(out m) == 0) Muted = m;
        }

        public void SetLevel(float level)
        {
            if (endpoint == null) return;
            level = Math.Max(0, Math.Min(1, level));
            endpoint.SetMasterVolumeLevelScalar(level, ref self);
            if (Muted && level > 0) endpoint.SetMute(false, ref self);
            Read();
        }

        public void SetMuted(bool muted)
        {
            if (endpoint == null) return;
            endpoint.SetMute(muted, ref self); Read();
        }

        public List<KeyValuePair<string, string>> Outputs() { return enumerator == null ? new List<KeyValuePair<string, string>>() : CoreAudio.Outputs(enumerator); }

        void Raise(bool reattach)
        {
            dispatcher.BeginInvoke(new Action(delegate
            {
                if (disposed) return;
                if (reattach) Attach(); else Read();
                var c = Changed; if (c != null) c();
            }));
        }

        int CoreAudio.IAudioEndpointVolumeCallback.OnNotify(IntPtr data) { if (!disposed) Raise(false); return 0; }
        int CoreAudio.IMMNotificationClient.OnDeviceStateChanged(string id, int state) { return 0; }
        int CoreAudio.IMMNotificationClient.OnDeviceAdded(string id) { return 0; }
        int CoreAudio.IMMNotificationClient.OnDeviceRemoved(string id) { return 0; }
        int CoreAudio.IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, string id) { if (!disposed && flow == 0 && role == 1) Raise(true); return 0; }
        int CoreAudio.IMMNotificationClient.OnPropertyValueChanged(string id, CoreAudio.PropertyKey key) { return 0; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Detach();
            if (enumerator != null) { try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { } Marshal.ReleaseComObject(enumerator); enumerator = null; }
        }
    }

    // ---------------------------------------------------------------- pure presentation rules (tested)
    internal static class QuickLogic
    {
        internal static string VolumeGlyph(double level, bool muted)
        {
            if (muted) return "\uE74F";
            return level <= 0.001 ? "\uE992" : "\uE767";
        }
        internal static string Percent(double level) { return (int)Math.Round(Math.Max(0, Math.Min(1, level)) * 100) + "%"; }
        internal static string WifiGlyph(bool on, int? bars)
        {
            if (!on) return "\uE701";
            if (!bars.HasValue) return "\uE701";
            return bars.Value <= 1 ? "\uE872" : bars.Value == 2 ? "\uE873" : bars.Value <= 4 ? "\uE874" : "\uE701";
        }
        internal static string SignalText(int? bars) { return bars.HasValue ? "信号 " + bars.Value + "/5" : ""; }
        internal static string ShortSignal(int? bars) { return bars.HasValue ? bars.Value + "/5" : ""; }
        internal static string WifiText(RadioView r, bool connected, string ssid, int? bars)
        {
            if (r != RadioView.On) return RadioText(r, "WLAN");
            if (!connected) return "未连接";
            string name = string.IsNullOrEmpty(ssid) ? "已连接" : ssid;
            string signal = ShortSignal(bars);
            return signal.Length == 0 ? name : name + " · " + signal;
        }
        internal static string BluetoothText(RadioView r, int connected)
        {
            if (r != RadioView.On) return RadioText(r, "蓝牙");
            return connected == 0 ? "已开启 · 无设备连接" : "已连接 " + connected + " 个设备";
        }
        internal static string RadioText(RadioView r, string name)
        {
            switch (r)
            {
                case RadioView.Off: return "已关闭";
                case RadioView.Denied: return "无权限控制" + name + "，点此打开隐私设置";
                case RadioView.Missing: return "未找到" + name + "适配器";
                case RadioView.Disabled: return name + "已被系统禁用";
                case RadioView.Loading: return "读取中…";
            }
            return "已开启";
        }
    }

    internal enum RadioView { Loading, On, Off, Denied, Missing, Disabled }

    // Wraps Windows.Devices.Radios for one radio kind; events only, no polling.
    internal sealed class RadioSwitch : IDisposable
    {
        readonly Windows.Devices.Radios.RadioKind kind;
        readonly Dispatcher dispatcher;
        Windows.Devices.Radios.Radio radio;
        bool disposed;
        public RadioView View { get; private set; }
        public event Action Changed;

        public RadioSwitch(Windows.Devices.Radios.RadioKind k, Dispatcher d) { kind = k; dispatcher = d; View = RadioView.Loading; }

        public async Task Start()
        {
            try
            {
                var access = await Windows.Devices.Radios.Radio.RequestAccessAsync().AsTask();
                if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) { View = RadioView.Denied; Raise(); return; }
                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
                radio = radios.FirstOrDefault(r => r.Kind == kind);
                if (radio == null) { View = RadioView.Missing; Raise(); return; }
                radio.StateChanged += OnState;
                Update();
            }
            catch (Exception e) { Program.Log(e); View = RadioView.Missing; Raise(); }
        }

        void OnState(Windows.Devices.Radios.Radio sender, object args) { dispatcher.BeginInvoke(new Action(Update)); }

        void Update()
        {
            if (disposed || radio == null) return;
            var s = radio.State;
            View = s == Windows.Devices.Radios.RadioState.On ? RadioView.On : s == Windows.Devices.Radios.RadioState.Off ? RadioView.Off : RadioView.Disabled;
            Raise();
        }

        void Raise() { if (disposed) return; var c = Changed; if (c != null) c(); }

        // Returns null on success, otherwise a user-facing message.
        public async Task<string> Toggle()
        {
            if (radio == null) return View == RadioView.Denied ? "ClearDesk 没有控制无线电的权限。" : "未找到可控制的适配器。";
            try
            {
                var target = radio.State == Windows.Devices.Radios.RadioState.On ? Windows.Devices.Radios.RadioState.Off : Windows.Devices.Radios.RadioState.On;
                var r = await radio.SetStateAsync(target).AsTask();
                if (r == Windows.Devices.Radios.RadioAccessStatus.DeniedByUser || r == Windows.Devices.Radios.RadioAccessStatus.DeniedBySystem)
                { View = RadioView.Denied; Raise(); return "Windows 拒绝了切换请求（权限或飞行模式）。"; }
                Update();
                return null;
            }
            catch (Exception e) { Program.Log(e); return "切换失败：" + e.Message; }
        }

        public void Dispose()
        {
            disposed = true;
            if (radio != null) { try { radio.StateChanged -= OnState; } catch { } radio = null; }
        }
    }

    // ---------------------------------------------------------------- widget
    public class QuickControlWindow : GlassWindow
    {
        OutputVolume volume;
        RadioSwitch wifi, bluetooth;
        Windows.Devices.Enumeration.DeviceWatcher[] btWatchers;
        readonly HashSet<string> btConnected = new HashSet<string>();
        readonly Dictionary<string, string> btNames = new Dictionary<string, string>();
        Path speaker;
        TextBlock volumeIcon, volumePercent, deviceName, wifiIcon, wifiTitle, wifiStatus, btIcon, btStatus;
        Border wifiTile, btTile, fill, thumb;
        Grid bar;
        Button muteButton;
        bool wifiConnected; string ssid; int? bars;
        DispatcherTimer signal;
        bool netHooked;
        static readonly Brush OnBrush = new SolidColorBrush(Color.FromArgb(150, 64, 140, 255));
        static readonly Brush OffBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

        public QuickControlWindow() : base(Program.Widget("quick", SystemParameters.WorkArea.Right - 420, 450, 400, 285), "快捷控制")
        {
            var root = new StackPanel();
            // volume
            var vrow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            vrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vrow.ColumnDefinitions.Add(new ColumnDefinition());
            vrow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            muteButton = IconButton("\uE995", "静音 / 取消静音", delegate { if (volume != null && volume.Available) volume.SetMuted(!volume.Muted); ShowVolume(); });
            volumeIcon = (TextBlock)muteButton.Content;
            speaker = new Path { Stroke = Brushes.White, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 18, Height = 16, Stretch = Stretch.None };
            muteButton.Content = speaker;
            vrow.Children.Add(muteButton);
            bar = new Grid { Height = 26, Margin = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            AutomationName(bar, "音量");
            bar.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
            fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(120, 180, 255)), HorizontalAlignment = HorizontalAlignment.Left };
            thumb = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill); bar.Children.Add(thumb);
            bar.MouseLeftButtonDown += (s, e) => { bar.CaptureMouse(); SeekVolume(e.GetPosition(bar).X); e.Handled = true; };
            bar.MouseMove += (s, e) => { if (bar.IsMouseCaptured) { SeekVolume(e.GetPosition(bar).X); e.Handled = true; } };
            bar.MouseLeftButtonUp += (s, e) => { bar.ReleaseMouseCapture(); e.Handled = true; };
            bar.MouseWheel += (s, e) => { if (volume != null && volume.Available) { volume.SetLevel(volume.Level + (e.Delta > 0 ? 0.02f : -0.02f)); ShowVolume(); } e.Handled = true; };
            bar.SizeChanged += delegate { ShowVolume(); };
            Grid.SetColumn(bar, 1); vrow.Children.Add(bar);
            volumePercent = Program.Text("—", 13, "#E8EEF8"); volumePercent.HorizontalAlignment = HorizontalAlignment.Right; volumePercent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(volumePercent, 2); vrow.Children.Add(volumePercent);
            root.Children.Add(vrow);
            deviceName = Program.Text("正在读取输出设备…", 11.5, "#C9D2E0");
            deviceName.Margin = new Thickness(46, 0, 0, 14); deviceName.TextTrimming = TextTrimming.CharacterEllipsis; deviceName.Cursor = Cursors.Hand;
            deviceName.ToolTip = "点击选择输出设备";
            deviceName.MouseLeftButtonUp += (s, e) => { DeviceMenu(); e.Handled = true; };
            root.Children.Add(deviceName);
            // radios
            var tiles = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            tiles.ColumnDefinitions.Add(new ColumnDefinition()); tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); tiles.ColumnDefinitions.Add(new ColumnDefinition());
            wifiTile = Tile("\uE701", "WLAN", out wifiIcon, out wifiTitle, out wifiStatus,
                async delegate { await ToggleRadio(wifi, "WLAN"); },
                delegate { if (wifi != null && wifi.View == RadioView.Denied) Open("ms-settings:privacy-radios"); else OpenWifiFlyout(); },
                null);
            TextBlock btTitle;
            btTile = Tile("\uE702", "蓝牙", out btIcon, out btTitle, out btStatus,
                async delegate { await ToggleRadio(bluetooth, "蓝牙"); },
                delegate { Open(bluetooth != null && bluetooth.View == RadioView.Denied ? "ms-settings:privacy-radios" : "ms-settings:bluetooth"); }, null);
            tiles.Children.Add(wifiTile); Grid.SetColumn(btTile, 2); tiles.Children.Add(btTile);
            root.Children.Add(tiles);
            // shortcuts
            var shortcuts = new UniformGrid3();
            shortcuts.Add(Shortcut("\uE713", "设置", "ms-settings:"));
            shortcuts.Add(Shortcut("\uE7F4", "显示", "ms-settings:display"));
            shortcuts.Add(Shortcut("\uE767", "声音", "ms-settings:sound"));
            shortcuts.Add(Shortcut("\uE774", "可用网络", "ms-availablenetworks:"));
            root.Children.Add(shortcuts.Panel);
            Body.Children.Add(root);

            {
                volume = new OutputVolume(Dispatcher);
                volume.Changed += ShowVolume;
                ShowVolume();
                wifi = new RadioSwitch(Windows.Devices.Radios.RadioKind.WiFi, Dispatcher);
                bluetooth = new RadioSwitch(Windows.Devices.Radios.RadioKind.Bluetooth, Dispatcher);
                wifi.Changed += ShowWifi; bluetooth.Changed += ShowBluetooth;
                var unused1 = wifi.Start(); var unused2 = bluetooth.Start();
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetwork; netHooked = true;
                ReadWifi();
                // signal bars have no change event; refresh slowly, only while visible
                signal = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
                signal.Tick += delegate { if (IsVisible && !State.Collapsed) ReadWifi(); };
                signal.Start();
                WatchBluetooth();
            }
            ShowWifi(); ShowBluetooth();
            Closed += delegate
            {
                if (signal != null) signal.Stop();
                if (netHooked) try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetwork; } catch { }
                if (volume != null) volume.Dispose();
                if (wifi != null) wifi.Dispose();
                if (bluetooth != null) bluetooth.Dispose();
                if (btWatchers != null) foreach (var w in btWatchers) try { if (w.Status == Windows.Devices.Enumeration.DeviceWatcherStatus.Started || w.Status == Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
            };
        }

        sealed class UniformGrid3
        {
            public readonly Grid Panel = new Grid();
            public void Add(UIElement e)
            {
                if (Panel.ColumnDefinitions.Count > 0) Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(e, Panel.ColumnDefinitions.Count - 1); Panel.Children.Add(e);
            }
        }

        static void AutomationName(UIElement e, string name) { System.Windows.Automation.AutomationProperties.SetName(e, name); }

        static TextBlock Glyph(string g, double size) { return new TextBlock { Text = g, FontFamily = new FontFamily(MenuTheme.IconFont), FontSize = size, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; }

        static ControlTemplate flat;
        static ControlTemplate Flat()
        {
            if (flat != null) return flat;
            var t = new ControlTemplate(typeof(Button));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var c = new FrameworkElementFactory(typeof(ContentPresenter));
            c.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            c.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(c); t.VisualTree = b;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, .78));
            t.Triggers.Add(hover);
            return flat = t;
        }

        static Button IconButton(string glyph, string name, Action click, double size = 16)
        {
            var b = new Button { Content = Glyph(glyph, size), Template = Flat(), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Width = 36, Height = 36, Padding = new Thickness(0), ToolTip = name, Cursor = Cursors.Hand, Focusable = false };
            AutomationName(b, name);
            b.Click += (s, e) => { click(); e.Handled = true; };
            return b;
        }

        Border Tile(string glyph, string title, out TextBlock icon, out TextBlock heading, out TextBlock status, Action toggle, Action open, Button extra)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var toggleButton = IconButton(glyph, title + " 开关", toggle, 17);
            icon = (TextBlock)toggleButton.Content;
            g.Children.Add(toggleButton);
            var text = new StackPanel { Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "打开" + title + "设置" };
            heading = Program.Text(title, 13); heading.FontWeight = FontWeights.SemiBold;
            status = Program.Text("读取中…", 11, "#D3DAE6"); status.TextTrimming = TextTrimming.CharacterEllipsis; status.TextWrapping = TextWrapping.NoWrap; status.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(heading); text.Children.Add(status);
            text.MouseLeftButtonUp += (s, e) => { open(); e.Handled = true; };
            Grid.SetColumn(text, 1); g.Children.Add(text);
            if (extra != null) { extra.Width = 26; extra.Height = 26; extra.Background = Brushes.Transparent; Grid.SetColumn(extra, 2); g.Children.Add(extra); }
            return new Border { Child = g, CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Background = OffBrush, BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1) };
        }

        Button Shortcut(string glyph, string name, string uri)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(Glyph(glyph, 13));
            var t = Program.Text(name, 11.5); t.Margin = new Thickness(6, 0, 0, 0); p.Children.Add(t);
            var b = new Button { Content = p, Template = Flat(), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Height = 32, Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "打开 " + name, Focusable = false };
            AutomationName(b, name);
            b.Click += (s, e) => { Open(uri); e.Handled = true; };
            return b;
        }

        // Win11 quick-settings WLAN list (the flyout with 断开连接 / 更多 Wi-Fi 设置). Launched through
        // explorer so the shell owns the activation; a direct ShellExecute from our process is
        // silently ignored on 24H2+. Falls back to the WLAN settings page.
        internal static void OpenWifiFlyout()
        {
            if (Program.Rendering) return;
            try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "ms-availablenetworks:") { UseShellExecute = false }); }
            catch (Exception e) { Program.Log(e); Open("ms-settings:network-wifi"); }
        }

        internal static void Open(string uri)
        {
            if (uri == "ms-availablenetworks:") { OpenWifiFlyout(); return; }
            if (Program.Rendering) return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception e) { Program.Log(e); }
        }

        void SeekVolume(double x)
        {
            if (volume == null || !volume.Available || bar.ActualWidth <= 0) return;
            volume.SetLevel((float)(x / bar.ActualWidth));
            ShowVolume();
        }

        void ShowVolume()
        {
            bool ok = volume != null && volume.Available;
            double level = ok ? volume.Level : 0;
            bool muted = ok && volume.Muted;
            volumeIcon.Text = QuickLogic.VolumeGlyph(level, muted);
            speaker.Data = Geometry.Parse("M2,5.5 H5 L9,2 V14 L5,10.5 H2 Z" + (muted ? " M11.5,5.5 L16,10 M16,5.5 L11.5,10" : level <= 0.001 ? "" : level < 0.5 ? " M11.5,5.5 Q13,8 11.5,10.5" : " M11.5,5.5 Q13,8 11.5,10.5 M13.8,3.5 Q16.6,8 13.8,12.5"));
            volumePercent.Text = ok ? (muted ? "静音" : QuickLogic.Percent(level)) : "—";
            deviceName.Text = ok ? volume.DeviceName + "  \u25BE" : (volume == null ? "正在读取输出设备…" : "没有可用的输出设备");
            double w = Math.Max(0, bar.ActualWidth);
            fill.Width = w * level; fill.Opacity = muted ? .35 : 1;
            thumb.Margin = new Thickness(Math.Max(0, Math.Min(w - 14, w * level - 7)), 0, 0, 0);
            muteButton.Background = muted ? OnBrush : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        }

        void DeviceMenu()
        {
            if (volume == null) return;
            var menu = new ContextMenu();
            foreach (var d in volume.Outputs())
            {
                string id = d.Key;
                var item = new MenuItem { Header = d.Value, IsChecked = id == volume.DeviceId };
                item.Click += delegate
                {
                    if (!CoreAudio.SetDefault(id)) { Toast("\uE783", "无法切换，已打开声音设置"); Open("ms-settings:sound"); }
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "声音设置…" };
            settings.Click += delegate { Open("ms-settings:sound"); };
            menu.Items.Add(settings);
            menu.PlacementTarget = deviceName; menu.IsOpen = true;
        }

        async Task ToggleRadio(RadioSwitch r, string name)
        {
            if (r == null) return;
            if (r.View == RadioView.Denied) { Open("ms-settings:privacy-radios"); Toast("\uE72E", "请允许 ClearDesk 控制无线电"); return; }
            string error = await r.Toggle();
            if (error != null) Toast("\uE783", error);
        }

        void OnNetwork(object sender) { Dispatcher.BeginInvoke(new Action(ReadWifi)); }

        void ReadWifi()
        {
            try
            {
                bool connected = false; string name = null; int? signalBars = null;
                foreach (var p in Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
                {
                    if (!p.IsWlanConnectionProfile || p.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.None) continue;
                    connected = true;
                    try { name = p.WlanConnectionProfileDetails.GetConnectedSsid(); } catch { }
                    if (string.IsNullOrEmpty(name)) name = p.ProfileName;
                    var b = p.GetSignalBars(); if (b.HasValue) signalBars = b.Value;
                    break;
                }
                wifiConnected = connected; ssid = name; bars = signalBars;
            }
            catch (Exception e) { Program.Log(e); }
            ShowWifi();
        }

        void ShowWifi()
        {
            var view = wifi == null ? RadioView.Loading : wifi.View;
            wifiIcon.Text = QuickLogic.WifiGlyph(view == RadioView.On, wifiConnected ? bars : null);
            wifiStatus.Text = QuickLogic.WifiText(view, wifiConnected, ssid, bars);
            wifiStatus.ToolTip = wifiConnected && bars.HasValue ? (ssid ?? "") + " · " + QuickLogic.SignalText(bars) : wifiStatus.Text;
            wifiTile.Background = view == RadioView.On ? OnBrush : OffBrush; wifiIcon.Opacity = view == RadioView.On ? 1 : .6;
        }

        void WatchBluetooth()
        {
            try
            {
                var classic = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
                var le = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
                btWatchers = new[] { classic, le }.Select(sel =>
                {
                    var w = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(sel);
                    w.Added += (new Windows.Foundation.TypedEventHandler<Windows.Devices.Enumeration.DeviceWatcher, Windows.Devices.Enumeration.DeviceInformation>((s, d) => Dispatcher.BeginInvoke(new Action(delegate { btConnected.Add(d.Id); btNames[d.Id] = d.Name; ShowBluetooth(); }))));
                    w.Removed += (new Windows.Foundation.TypedEventHandler<Windows.Devices.Enumeration.DeviceWatcher, Windows.Devices.Enumeration.DeviceInformationUpdate>((s, d) => Dispatcher.BeginInvoke(new Action(delegate { btConnected.Remove(d.Id); ShowBluetooth(); }))));
                    w.Updated += (new Windows.Foundation.TypedEventHandler<Windows.Devices.Enumeration.DeviceWatcher, Windows.Devices.Enumeration.DeviceInformationUpdate>((s, d) => { }));
                    w.Start();
                    return w;
                }).ToArray();
            }
            catch (Exception e) { Program.Log(e); }
        }

        void ShowBluetooth()
        {
            var view = bluetooth == null ? RadioView.Loading : bluetooth.View;
            int count = view == RadioView.On ? btConnected.Count : 0;
            btIcon.Text = "\uE702"; btIcon.Opacity = view == RadioView.On ? 1 : .6;
            btStatus.Text = QuickLogic.BluetoothText(view, count);
            btStatus.ToolTip = count > 0 ? string.Join("\n", btConnected.Select(id => btNames.ContainsKey(id) ? btNames[id] : id).Distinct()) : btStatus.Text;
            btTile.Background = view == RadioView.On ? OnBrush : OffBrush;
        }

        internal string TestVolumeText { get { return volumePercent.Text; } }
        internal string TestDeviceText { get { return deviceName.Text; } }
        internal string TestWifiText { get { return wifiStatus.Text; } }
        internal string TestBluetoothText { get { return btStatus.Text; } }
        internal OutputVolume TestVolume { get { return volume; } }
    }
}
