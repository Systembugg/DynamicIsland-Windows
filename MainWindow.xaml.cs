using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using DynamicIsland.Media;
using DynamicIsland.Timer;
using DynamicIsland.Dock;
using DynamicIsland.Network;
using DynamicIsland.AirDrop;
using DynamicIsland.Call;

namespace DynamicIsland
{
    public enum ShapeDisplayMode
    {
        Notch,  // DynamicNotch exact flared ears flush with bezel
        Island  // Symmetrical floating pill
    }

    public enum AudioDeviceCategory
    {
        TwsEarbuds,
        WirelessHeadphones,
        WiredHeadphones,
        InternalSpeakers
    }

    public enum ExpandedActivityTab
    {
        Home,
        Shelf,
        Music,
        Timer,
        Bluetooth,
        Network,
        ScreenMirroring,
        Clipboard,
        Call
    }

    public enum ClipboardSubTab
    {
        Notes,
        Clipboard,
        Screenshots,
        Snippets
    }

    public partial class MainWindow : Window
    {
        private ShapeDisplayMode currentMode = ShapeDisplayMode.Notch;
        private bool isExpanded = false;
        private ExpandedActivityTab currentExpandedTab = ExpandedActivityTab.Shelf;
        private bool isVolumeHudActive = false;
        private bool isBrightnessHudActive = false;
        private bool isDndHudActive = false;
        private bool isBluetoothHudActive = false;
        private bool isClipboardHudActive = false;
        private bool isScreenMirroringHudActive = false;
        private bool isIncomingCallActive = false;
        private bool isDndOn = false;
        private bool isInitialDndLoaded = false;
        private bool isLyricsViewActive = false;
        private bool isManualLyricsScrolling = false;
        private DateTime lastManualLyricsScrollTime = DateTime.MinValue;

        private class LyricCardItem
        {
            public Border Card { get; set; } = null!;
            public TextBlock TextBlock { get; set; } = null!;
            public ScaleTransform ScaleTransform { get; set; } = null!;
            public LinearGradientBrush FlowGradientBrush { get; set; } = null!;
            public GradientStop ActiveStop { get; set; } = null!;
            public GradientStop TransitionStop { get; set; } = null!;
            public bool IsActive { get; set; }
        }

        private readonly List<LyricCardItem> _lyricItems = new();
        private int _currentActiveLyricIndex = -1;
        private double _currentLyricsScrollOffset = 0;
        private double _targetLyricsScrollOffset = 0;
        
        // Privacy Indicator States
        private bool isCameraActive = false;
        private bool isMicActive = false;
        private bool isRecordingActive = false;
        private bool isScreenSharingActive = false;

        // Exact DynamicNotch base geometry dimensions
        private double notchBaseWidth = 162;
        private double notchHeight = 32;
        private double topEarRadius = 6.0;      // DynamicNotch NotchShape.swift
        private double bottomRadius = 14.0;     // DynamicNotch NotchShape.swift

        private double islandBaseWidth = 150;
        private double islandHeight = 32;
        private double islandRadius = 16.0;     // DynamicNotch DynamicIslandShape.swift

        // Timers
        private DispatcherTimer volumePollTimer = new();
        private DispatcherTimer brightnessPollTimer = new();
        private DispatcherTimer audioEndpointWatcherTimer = new();
        private DispatcherTimer volumeAutoHideTimer = new();
        private DispatcherTimer brightnessAutoHideTimer = new();
        private DispatcherTimer dndAutoHideTimer = new();
        private DispatcherTimer bluetoothAutoHideTimer = new();
        private DispatcherTimer capsLockAutoHideTimer = new();
        private bool isCapsLockHudActive = false;
        private readonly DispatcherTimer btConnectingArcTimer = new DispatcherTimer(DispatcherPriority.Render);
        private double btConnectingProgress = 0.0;
        private DispatcherTimer backgroundWatcherTimer = new();
        private DispatcherTimer timelineTickerTimer = new();
        private Storyboard? privacyPulseStoryboard;
        private bool isPrivacyPulsePlaying = false;

        private float lastKnownVolume = -1f;
        private bool lastKnownMute = false;
        private bool isInitialVolumeLoaded = false;
        private int displayedVolumeLevel = -1;

        private int lastKnownBrightness = -1;
        private int displayedBrightnessLevel = -1;
        private bool isInitialBrightnessLoaded = false;

        private string lastKnownAudioEndpoint = "";
        private bool isInitialEndpointLoaded = false;

        // CoreAudio COM Objects
        private IAudioEndpointVolume? audioEndpointVolume;
        private IMMDeviceEnumerator? deviceEnumerator;

        private static BitmapImage? imgAirPods;
        private static BitmapImage? imgWirelessHeadphones;
        private static BitmapImage? imgWiredHeadphones;
        private static BitmapImage? imgSpeaker;

        private static MainWindow? activeInstance;
        private static IntPtr mouseHookId = IntPtr.Zero;
        private static LowLevelMouseProc mouseProc = HookCallback;

        private static IntPtr keyboardHookId = IntPtr.Zero;
        private static LowLevelKeyboardProc keyboardProc = KeyboardHookCallback;

        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CAPITAL = 0x14;
        private const int VK_VOLUME_MUTE = 0xAD;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_M = 0x4D; // 'M'

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public static void TrimProcessMemory()
        {
            Task.Run(() =>
            {
                try
                {
                    GC.Collect(1, GCCollectionMode.Optimized, false);
                    using var proc = Process.GetCurrentProcess();
                    EmptyWorkingSet(proc.Handle);
                }
                catch { }
            });
        }

        private readonly DispatcherTimer memoryTrimTimer = new DispatcherTimer();

        private bool isShelfPinned = false;
        private bool isDraggingDockItem = false;
        private Point dockDragStartPoint;

        public MainWindow()
        {
            activeInstance = this;
            InitializeComponent();
            LoadDeviceBitmaps();
            ShapeRoot.SizeChanged += (s, e) => RedrawGeometry(e.NewSize.Width, e.NewSize.Height);
            
            // Watch Windows Privacy & Windows 11 DND State (3000ms — reduced from 1500ms for CPU savings)
            backgroundWatcherTimer.Interval = TimeSpan.FromMilliseconds(3000);
            backgroundWatcherTimer.Tick += BackgroundWatcherTimer_Tick;
            backgroundWatcherTimer.Start();

            // Auto-Hide Timers
            volumeAutoHideTimer.Interval = TimeSpan.FromMilliseconds(1800);
            volumeAutoHideTimer.Tick += VolumeAutoHideTimer_Tick;

            brightnessAutoHideTimer.Interval = TimeSpan.FromMilliseconds(1800);
            brightnessAutoHideTimer.Tick += BrightnessAutoHideTimer_Tick;

            dndAutoHideTimer.Interval = TimeSpan.FromMilliseconds(2000);
            dndAutoHideTimer.Tick += DndAutoHideTimer_Tick;

            bluetoothAutoHideTimer.Interval = TimeSpan.FromMilliseconds(3500);
            bluetoothAutoHideTimer.Tick += BluetoothAutoHideTimer_Tick;

            btConnectingArcTimer.Interval = TimeSpan.FromMilliseconds(40);
            btConnectingArcTimer.Tick += (s, e) =>
            {
                btConnectingProgress += 0.055;
                if (btConnectingProgress >= 1.0)
                {
                    btConnectingProgress = 1.0;
                    btConnectingArcTimer.Stop();
                    BtConnectingArc.Data = null;
                    BtCheckmarkIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    DrawBtConnectingArc(btConnectingProgress);
                }
            };

            // Audio Device Watcher (8000ms safety fallback — WM_DEVICECHANGE handles connections instantly)
            audioEndpointWatcherTimer.Interval = TimeSpan.FromMilliseconds(8000);
            audioEndpointWatcherTimer.Tick += AudioEndpointWatcherTimer_Tick;
            audioEndpointWatcherTimer.Start();

            // Volume Fast Poller (45ms — only runs on-demand during active volume changes, NOT always-on)
            volumePollTimer.Interval = TimeSpan.FromMilliseconds(45);
            volumePollTimer.Tick += VolumePollTimer_Tick;
            // NOT started here — started on-demand in keyboard hook / mouse wheel

            // Real-Time Hardware Brightness — WMI event-driven (push), with 5s fallback poller for external DDC/CI monitors
            InitBrightnessWatcher();
            brightnessPollTimer.Interval = TimeSpan.FromMilliseconds(5000); // Slow fallback only
            brightnessPollTimer.Tick += BrightnessPollTimer_Tick;
            // NOT started here — started only if WMI watcher fails in InitBrightnessWatcher()

            // Timeline continuous scrubber ticker (35ms high-frequency sync — only runs during active music playback)
            timelineTickerTimer.Interval = TimeSpan.FromMilliseconds(35);
            timelineTickerTimer.Tick += TimelineTickerTimer_Tick;
            // NOT started here — started on-demand in Media_OnPlaybackStateChanged / Media_OnTrackChanged

            // Real-time Bluetooth Connection Arrival Listener
            BluetoothBatteryManager.Instance.DeviceConnected += (name, category, battery) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TriggerAudioDeviceHUD(category, name, battery);
                });
            };

            capsLockAutoHideTimer.Interval = TimeSpan.FromMilliseconds(1600);
            capsLockAutoHideTimer.Tick += CapsLockAutoHideTimer_Tick;

            // Memory Auto-Trimmer (Every 20 seconds, returns unused heap memory directly back to Windows OS)
            memoryTrimTimer.Interval = TimeSpan.FromSeconds(20);
            memoryTrimTimer.Tick += (s, e) =>
            {
                if (!isExpanded && !isLyricsViewActive)
                {
                    TrimProcessMemory();
                }
            };
            memoryTrimTimer.Start();
            TrimProcessMemory();

            SetupPrivacyPulseAnimation();
            InitWindowsCoreAudio();
            InitAppleTimer();
            InitDockShelf();
            InitAirDrop();

            try
            {
                using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(curModule.ModuleName), 0);
                    keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private bool isExplicitExit = false;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!isExplicitExit)
            {
                e.Cancel = true; // Shield against Alt+F4 / accidental close!
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHookId);
                mouseHookId = IntPtr.Zero;
            }
            if (keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHookId);
                keyboardHookId = IntPtr.Zero;
            }
            base.OnClosed(e);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
                source?.AddHook(WndProc);
                AddClipboardFormatListener(helper.Handle);
            }
            catch { }

            ClipboardHistoryManager.Instance.OnDataChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (currentExpandedTab == ExpandedActivityTab.Clipboard)
                    {
                        RenderClipboardHistoryList();
                    }
                });
            };

            ClipboardHistoryManager.Instance.OnItemCaptured += (title, subtitle, isScreenshot) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TriggerClipboardHUD(title, subtitle, isScreenshot);
                });
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DEVICECHANGE = 0x0219;
            const int WM_CLIPBOARDUPDATE = 0x031D;
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;
            const int WM_CLOSE = 0x0010;

            // Shield: Never let Alt+F4 or OS Close command terminate Dynamic Island
            if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_CLOSE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_CLOSE && !isExplicitExit)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_DEVICECHANGE)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(300);
                    CheckForActiveAudioEndpointChanges();
                    await BluetoothBatteryManager.Instance.RefreshDevicesAsync();
                });
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                ClipboardHistoryManager.Instance.HandleClipboardUpdate();
            }
            return IntPtr.Zero;
        }

        private void LoadDeviceBitmaps()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                imgAirPods = LoadBmp(System.IO.Path.Combine(baseDir, "Assets", "dev_airpods.png"));
                imgWirelessHeadphones = LoadBmp(System.IO.Path.Combine(baseDir, "Assets", "dev_wireless_headphones.png"));
                imgWiredHeadphones = LoadBmp(System.IO.Path.Combine(baseDir, "Assets", "dev_wired_headphones.png"));
                imgSpeaker = LoadBmp(System.IO.Path.Combine(baseDir, "Assets", "dev_speaker.png"));
            }
            catch { }
        }

        private BitmapImage? LoadBmp(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 100;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        private TrayIconManager? trayManager;
        private SettingsWindow? settingsWindow;
        private DispatcherTimer? faceBlinkTimer;
        private readonly Random rand = new Random();

        public bool IsIdleFaceEnabled { get; set; } = true;
        public ShapeDisplayMode CurrentDisplayMode => currentMode;

        public void SetMode(ShapeDisplayMode mode)
        {
            currentMode = mode;
            ApplyMode();
        }

        public void ToggleMode()
        {
            currentMode = currentMode == ShapeDisplayMode.Notch ? ShapeDisplayMode.Island : ShapeDisplayMode.Notch;
            ApplyMode();
        }

        public void OpenSettingsWindow()
        {
            if (settingsWindow == null || !settingsWindow.IsLoaded)
            {
                settingsWindow = new SettingsWindow(this);
                settingsWindow.Closed += (s, e) => settingsWindow = null;
                settingsWindow.Show();
            }
            else
            {
                settingsWindow.Activate();
            }
        }

        public void OptimizeMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        public void CloseApp()
        {
            isExplicitExit = true;
            try
            {
                trayManager?.Dispose();
                trayManager = null;
            }
            catch { }
            Application.Current.Shutdown();
        }

        private void SetupFaceBlinkTimer()
        {
            faceBlinkTimer = new DispatcherTimer();
            faceBlinkTimer.Interval = TimeSpan.FromSeconds(4);
            faceBlinkTimer.Tick += (s, e) =>
            {
                faceBlinkTimer.Interval = TimeSpan.FromSeconds(rand.Next(3, 7));
                if (IdleFaceContainer.Visibility == Visibility.Visible && IsIdleFaceEnabled)
                {
                    TriggerFaceBlink();
                }
            };
            faceBlinkTimer.Start();
        }

        private void TriggerFaceBlink()
        {
            var blinkAnim = new DoubleAnimationUsingKeyFrames();
            blinkAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blinkAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75))));
            blinkAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));

            ScaleEyeLeft.BeginAnimation(ScaleTransform.ScaleYProperty, blinkAnim);
            ScaleEyeRight.BeginAnimation(ScaleTransform.ScaleYProperty, blinkAnim);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Hide from Alt+Tab / Task Switcher (WS_EX_TOOLWINDOW)
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            }
            catch { }

            // 2. Setup System Tray Icon
            trayManager = new TrayIconManager(this);
            trayManager.Initialize();

            // 3. Setup Idle Mascot Face Blinking
            SetupFaceBlinkTimer();

            ApplyMode();
            
            // Read initial real-time Windows 11 DND state silently
            isDndOn = WindowsDndNative.GetWindows11DndActive();
            isInitialDndLoaded = true;
            UpdateIndicatorVisuals();

            CheckWindowsPrivacyState();
            // volumePollTimer — NOT started here (on-demand only, triggered by volume key/wheel)
            // brightnessPollTimer — NOT started here (WMI event-driven, fallback started only if watcher fails)
            audioEndpointWatcherTimer.Start();

            // Real-Time Bluetooth Battery & Device Listener
            BluetoothBatteryManager.Instance.DevicesUpdated += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (isExpanded && currentExpandedTab == ExpandedActivityTab.Bluetooth)
                    {
                        UpdateIndicatorVisuals();
                    }
                });
            };
            Task.Run(async () => await BluetoothBatteryManager.Instance.RefreshDevicesAsync());

            // Real-Time Network Speed Listener
            NetworkSpeedManager.Instance.OnSpeedUpdated += () =>
            {
                Dispatcher.Invoke(UpdateNetworkVisuals);
            };

            // Real-Time WhatsApp Call Listener
            WhatsAppCallManager.Instance.OnIncomingCall += call =>
            {
                Dispatcher.Invoke(() =>
                {
                    isIncomingCallActive = true;
                    TxtIncomingCallSubtitle.Text = call.Subtitle;
                    TxtIncomingCallName.Text = call.CallerName;
                    TxtIncomingCallInitial.Text = !string.IsNullOrWhiteSpace(call.CallerName) ? call.CallerName.Substring(0, 1).ToUpperInvariant() : "👤";

                    UpdateIndicatorVisuals();
                });
            };

            WhatsAppCallManager.Instance.OnCallAnswered += call =>
            {
                Dispatcher.Invoke(() =>
                {
                    isIncomingCallActive = false;
                    currentExpandedTab = ExpandedActivityTab.Call;
                    isExpanded = false;
                    UpdateIndicatorVisuals();
                });
            };

            WhatsAppCallManager.Instance.OnCallEnded += call =>
            {
                Dispatcher.Invoke(() =>
                {
                    isIncomingCallActive = false;
                    if (currentExpandedTab == ExpandedActivityTab.Call)
                    {
                        currentExpandedTab = ExpandedActivityTab.Shelf;
                        isExpanded = false;
                    }
                    UpdateIndicatorVisuals();
                });
            };

            WhatsAppCallManager.Instance.OnDurationTick += duration =>
            {
                Dispatcher.Invoke(() =>
                {
                    string durText = duration.TotalHours >= 1 
                        ? duration.ToString(@"hh\:mm\:ss") 
                        : duration.ToString(@"mm\:ss");
                    TxtCallCompactDuration.Text = durText;
                    TxtCallExpandedSubtitle.Text = $"{durText} • {(WhatsAppCallManager.Instance.CurrentCall?.Type == CallType.Video ? "WhatsApp Video" : "WhatsApp Audio")}";
                });
            };

            InitMediaSession();

            // Register global low-level mouse & keyboard hook for automatic collapse on outside clicks and global mute
            try
            {
                using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(curModule.ModuleName), 0);
                    keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            try
            {
                trayManager?.Dispose();
                trayManager = null;
            }
            catch { }

            if (mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHookId);
                mouseHookId = IntPtr.Zero;
            }

            if (keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHookId);
                keyboardHookId = IntPtr.Zero;
            }
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (isDraggingDockItem) return;
            if (isExpanded)
            {
                if (isShelfPinned) return;
                isExpanded = false;
                if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                {
                    UpdateIndicatorVisuals();
                }
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (isDraggingDockItem) return;
            if (isExpanded)
            {
                if (isShelfPinned) return;
                Point pt = e.GetPosition(ShapeRoot);
                if (pt.X < 0 || pt.Y < 0 || pt.X > ShapeRoot.ActualWidth || pt.Y > ShapeRoot.ActualHeight)
                {
                    isExpanded = false;
                    if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                    {
                        UpdateIndicatorVisuals();
                    }
                    e.Handled = true;
                }
            }
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!isExpanded && !isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive && !isAirDropTransferActive)
            {
                // Subtle smooth hover cushion
                double currentW = ShapeRoot.Width;
                AnimateSize(currentW + 8, ShapeRoot.Height);
            }
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!isExpanded && !isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive && !isAirDropTransferActive)
            {
                UpdateIndicatorVisuals();
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN || wParam == (IntPtr)WM_NCLBUTTONDOWN))
            {
                if (activeInstance != null && activeInstance.isExpanded && !activeInstance.isDraggingDockItem)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    activeInstance.Dispatcher.InvokeAsync(() =>
                    {
                        activeInstance.CheckOutsideClick(hookStruct.pt.x, hookStruct.pt.y);
                    });
                }
            }
            return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        }

        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // 1. Hardware / Laptop Fn + Mute Key (VK_VOLUME_MUTE)
                if (kbd.vkCode == VK_VOLUME_MUTE)
                {
                    activeInstance?.Dispatcher.InvokeAsync(() =>
                    {
                        if (!activeInstance.volumePollTimer.IsEnabled) activeInstance.volumePollTimer.Start();
                        activeInstance.ToggleMasterMute();
                    });
                }
                // 2. Global Alt + M (Direct System-Wide Modifier Mute)
                else if (kbd.vkCode == VK_M)
                {
                    bool isAlt = (GetKeyState(VK_MENU) & 0x8000) != 0 || wParam == (IntPtr)WM_SYSKEYDOWN;
                    if (isAlt)
                    {
                        activeInstance?.Dispatcher.InvokeAsync(() =>
                        {
                            activeInstance.ToggleMasterMute();
                        });
                        return (IntPtr)1; // Consume event
                    }
                }
                // 3. Caps Lock Key Toggle HUD (media_1788018659285.png)
                else if (kbd.vkCode == VK_CAPITAL)
                {
                    activeInstance?.Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(25);
                        bool isCapsOn = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
                        activeInstance.TriggerCapsLockHUD(isCapsOn);
                    });
                }
            }
            return CallNextHookEx(keyboardHookId, nCode, wParam, lParam);
        }

        private void CheckOutsideClick(int screenX, int screenY)
        {
            if (isDraggingDockItem) return;
            if (!isExpanded) return;
            if (isShelfPinned) return;

            try
            {
                Point p = ShapeRoot.PointFromScreen(new Point(screenX, screenY));
                if (p.X < 0 || p.Y < 0 || p.X > ShapeRoot.ActualWidth || p.Y > ShapeRoot.ActualHeight)
                {
                    isExpanded = false;
                    if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                    {
                        UpdateIndicatorVisuals();
                    }
                }
            }
            catch { }
        }

        #region Dock / File Shelf Engine

        private void InitDockShelf()
        {
            DockItemsList.ItemsSource = DockShelfManager.Instance.Items;
            DockShelfManager.Instance.OnShelfChanged += (status, count) =>
            {
                Dispatcher.Invoke(() =>
                {
                    DockCompactRing.SetStatus(status, count);

                    if (count > 0)
                    {
                        ShelfEmptyPrompt.Visibility = Visibility.Collapsed;
                        ShelfFilesScrollViewer.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ShelfEmptyPrompt.Visibility = Visibility.Visible;
                        ShelfFilesScrollViewer.Visibility = Visibility.Collapsed;
                    }

                    if (status == DockShelfStatus.Idle)
                    {
                        DockCompactContainer.Visibility = Visibility.Collapsed;
                        DockCompactRing.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        DockCompactContainer.Visibility = Visibility.Visible;
                        DockCompactRing.Visibility = Visibility.Visible;
                    }

                    if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                    {
                        UpdateIndicatorVisuals();
                    }
                });
            };
        }

        private void ShapeRoot_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent(DataFormats.Bitmap) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                e.Data.GetDataPresent(DataFormats.Text) ||
                e.Data.GetDataPresent(DataFormats.Html))
            {
                double targetW = 490;
                double targetH = 175;
                isExpanded = true;
                currentExpandedTab = ExpandedActivityTab.Shelf;
                UpdateIndicatorVisuals();
                AnimateSize(targetW, targetH);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void ShapeRoot_DragLeave(object sender, DragEventArgs e)
        {
        }

        private void ShapeRoot_Drop(object sender, DragEventArgs e)
        {
            DockShelfManager.Instance.AddDataObject(e.Data);
            currentExpandedTab = ExpandedActivityTab.Shelf;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void DockCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Shelf;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void BtnAirDropShare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                string userDownloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(userDownloads))
                {
                    System.Diagnostics.Process.Start("explorer.exe", userDownloads);
                }
            }
            catch { }
        }

        private void BtnNavHome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Home;
            isExpanded = false;
            UpdateIndicatorVisuals();
        }

        private void BluetoothCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Bluetooth;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void BtnNavShelf_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Shelf;
            UpdateIndicatorVisuals();
        }

        private void BtnNavMusic_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Music;
            UpdateIndicatorVisuals();
        }

        private void BtnNavTimer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Timer;
            UpdateIndicatorVisuals();
        }

        private async void BtnNavBluetooth_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Bluetooth;
            await BluetoothBatteryManager.Instance.RefreshDevicesAsync();
            UpdateIndicatorVisuals();
        }

        private void BtnNavNetwork_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Network;
            UpdateIndicatorVisuals();
        }

        private void NetworkCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Network;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void UpdateNetworkVisuals()
        {
            bool isVisibleOnUi = (NetworkCompactContainer.Visibility == Visibility.Visible) ||
                                 (isExpanded && currentExpandedTab == ExpandedActivityTab.Network);

            NetworkSpeedManager.Instance.SetActive(isVisibleOnUi);

            if (!isVisibleOnUi) return; // Skip all UI property assignments and DWM GPU composition when not on screen!

            bool connected = NetworkSpeedManager.Instance.IsConnected;
            var accentColor = connected ? Color.FromRgb(0x37, 0xC0, 0x58) : Color.FromRgb(0xFF, 0x3B, 0x30);
            var accentBrush = new SolidColorBrush(accentColor);
            var borderBrush = new SolidColorBrush(Color.FromArgb((byte)(connected ? 0x33 : 0x40), accentColor.R, accentColor.G, accentColor.B));
            var badgeBg = new SolidColorBrush(Color.FromArgb((byte)(connected ? 0x18 : 0x22), accentColor.R, accentColor.G, accentColor.B));

            // 1. Compact View (Stealth View)
            IconNetworkCompactLink.Stroke = accentBrush;
            TxtNetworkCompact.Foreground = accentBrush;
            TxtNetworkCompact.Text = NetworkSpeedManager.Instance.FormattedDownloadSpeed;

            // 2. Expanded View
            BorderNetworkTile.BorderBrush = borderBrush;
            IconNetworkExpandedLink.Stroke = accentBrush;
            TxtNetworkInterface.Text = connected ? $"{NetworkSpeedManager.Instance.ActiveInterfaceName} Network" : $"{NetworkSpeedManager.Instance.ActiveInterfaceName} Disconnected";
            TxtNetworkDownloadArrow.Foreground = accentBrush;
            TxtNetworkExpandedSpeed.Foreground = accentBrush;
            TxtNetworkExpandedSpeed.Text = NetworkSpeedManager.Instance.FormattedDownloadSpeed;
            TxtNetworkExpandedUpload.Text = NetworkSpeedManager.Instance.FormattedUploadSpeed;

            BadgeNetworkStatus.Background = badgeBg;
            BadgeNetworkStatus.BorderBrush = borderBrush;
            DotNetworkStatus.Fill = accentBrush;
            TxtNetworkStatus.Foreground = accentBrush;
            TxtNetworkStatus.Text = connected ? "Online" : "Offline";

            // 3. Top Tab Nav Button (if Network tab active)
            if (currentExpandedTab == ExpandedActivityTab.Network)
            {
                BtnNavNetwork.Background = new SolidColorBrush(Color.FromArgb(0x25, accentColor.R, accentColor.G, accentColor.B));
                IconNavNetwork.Stroke = accentBrush;
            }
        }

        private void BtnOpenBtSettings_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            BluetoothBatteryManager.OpenBluetoothSettings();
        }

        private void BtnClearShelf_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            DockShelfManager.Instance.ClearShelf();
            UpdateIndicatorVisuals();
        }

        private void BtnPinShelf_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            isShelfPinned = !isShelfPinned;
            if (isShelfPinned)
            {
                BtnPinShelf.Background = new SolidColorBrush(Color.FromArgb(0x35, 0x0A, 0x84, 0xFF));
                IconPinShape.Fill = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            }
            else
            {
                BtnPinShelf.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D));
                IconPinShape.Fill = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
            }
        }

        private ClipboardSubTab currentClipboardSubTab = ClipboardSubTab.Notes;

        private void BtnNavClipboard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Clipboard;
            UpdateIndicatorVisuals();
        }

        private void BtnSubTabNotes_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseNoteEditor();
            currentClipboardSubTab = ClipboardSubTab.Notes;
            UpdateSubTabPills();
            RenderClipboardHistoryList();
        }

        private void BtnSubTabClipboard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseNoteEditor();
            currentClipboardSubTab = ClipboardSubTab.Clipboard;
            UpdateSubTabPills();
            RenderClipboardHistoryList();
        }

        private void BtnSubTabScreenshots_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseNoteEditor();
            currentClipboardSubTab = ClipboardSubTab.Screenshots;
            UpdateSubTabPills();
            RenderClipboardHistoryList();
        }

        private void BtnSubTabSnippets_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseNoteEditor();
            currentClipboardSubTab = ClipboardSubTab.Snippets;
            UpdateSubTabPills();
            RenderClipboardHistoryList();
        }

        private void UpdateSubTabPills()
        {
            var activeBg = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            var inactiveBg = Brushes.Transparent;
            var activeFg = Brushes.White;
            var inactiveFg = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

            BtnSubTabNotes.Background = currentClipboardSubTab == ClipboardSubTab.Notes ? activeBg : inactiveBg;
            TxtSubTabNotes.Foreground = currentClipboardSubTab == ClipboardSubTab.Notes ? activeFg : inactiveFg;
            TxtSubTabNotes.FontWeight = currentClipboardSubTab == ClipboardSubTab.Notes ? FontWeights.SemiBold : FontWeights.Medium;

            BtnSubTabClipboard.Background = currentClipboardSubTab == ClipboardSubTab.Clipboard ? activeBg : inactiveBg;
            TxtSubTabClipboard.Foreground = currentClipboardSubTab == ClipboardSubTab.Clipboard ? activeFg : inactiveFg;
            TxtSubTabClipboard.FontWeight = currentClipboardSubTab == ClipboardSubTab.Clipboard ? FontWeights.SemiBold : FontWeights.Medium;

            BtnSubTabScreenshots.Background = currentClipboardSubTab == ClipboardSubTab.Screenshots ? activeBg : inactiveBg;
            TxtSubTabScreenshots.Foreground = currentClipboardSubTab == ClipboardSubTab.Screenshots ? activeFg : inactiveFg;
            TxtSubTabScreenshots.FontWeight = currentClipboardSubTab == ClipboardSubTab.Screenshots ? FontWeights.SemiBold : FontWeights.Medium;

            BtnSubTabSnippets.Background = currentClipboardSubTab == ClipboardSubTab.Snippets ? activeBg : inactiveBg;
            TxtSubTabSnippets.Foreground = currentClipboardSubTab == ClipboardSubTab.Snippets ? activeFg : inactiveFg;
            TxtSubTabSnippets.FontWeight = currentClipboardSubTab == ClipboardSubTab.Snippets ? FontWeights.SemiBold : FontWeights.Medium;
        }

        private HistoryItemModel? _currentEditingNoteItem = null;

        private void OpenNoteEditor(HistoryItemModel? item = null)
        {
            _currentEditingNoteItem = item;
            if (item != null)
            {
                TxtNoteEditorHeader.Text = item.Type == HistoryItemType.Snippet ? "Edit Snippet" : "Edit Note";
                TxtNoteEditTitle.Text = item.Title;
                TxtNoteEditContent.Text = item.Content;
            }
            else
            {
                string clipText = "";
                try { if (Clipboard.ContainsText()) clipText = Clipboard.GetText(); } catch { }

                TxtNoteEditorHeader.Text = currentClipboardSubTab == ClipboardSubTab.Snippets ? "New Snippet" : "New Note";
                TxtNoteEditTitle.Text = "";
                TxtNoteEditContent.Text = clipText;
            }

            ViewClipboardListContent.Visibility = Visibility.Collapsed;
            ViewNoteEditor.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrEmpty(TxtNoteEditTitle.Text))
                {
                    TxtNoteEditTitle.Focus();
                    TxtNoteEditTitle.SelectAll();
                }
                else
                {
                    TxtNoteEditContent.Focus();
                    TxtNoteEditContent.CaretIndex = TxtNoteEditContent.Text.Length;
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CloseNoteEditor()
        {
            _currentEditingNoteItem = null;
            ViewNoteEditor.Visibility = Visibility.Collapsed;
            ViewClipboardListContent.Visibility = Visibility.Visible;
            RenderClipboardHistoryList();
        }

        private void SaveNoteEditor()
        {
            string title = TxtNoteEditTitle.Text.Trim();
            string content = TxtNoteEditContent.Text.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                CloseNoteEditor();
                return;
            }

            if (_currentEditingNoteItem != null)
            {
                ClipboardHistoryManager.Instance.UpdateItem(_currentEditingNoteItem, title, content);
            }
            else
            {
                if (currentClipboardSubTab == ClipboardSubTab.Snippets)
                {
                    ClipboardHistoryManager.Instance.AddSnippet(title, content);
                }
                else
                {
                    ClipboardHistoryManager.Instance.AddNote(title, content);
                }
            }

            CloseNoteEditor();
        }

        private void BtnSaveNoteEdit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SaveNoteEditor();
        }

        private void BtnCancelNoteEdit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseNoteEditor();
        }

        private void TxtNoteEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveNoteEditor();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseNoteEditor();
            }
        }

        private void BtnNewNoteOrSnippet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenNoteEditor(null);
        }

        private void BtnClearClipboardHistory_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ClipboardHistoryManager.Instance.ClearClipboard();
        }

        private void BtnIncomingCallAccept_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            WhatsAppCallManager.Instance.AcceptCall();
        }

        private void BtnIncomingCallDecline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            WhatsAppCallManager.Instance.DeclineCall();
        }

        private void CallCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Call;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void BtnExpandedEndCall_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            WhatsAppCallManager.Instance.EndCall();
        }

        private void TxtClipboardSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            BtnClearSearch.Visibility = string.IsNullOrEmpty(TxtClipboardSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            RenderClipboardHistoryList();
        }

        private void BtnClearSearch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            TxtClipboardSearch.Text = "";
        }

        private void ClipboardScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scv)
            {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta * 0.5);
                e.Handled = true;
            }
        }

        private void RenderClipboardHistoryList()
        {
            if (ClipboardItemsStackPanel == null) return;
            ClipboardItemsStackPanel.Children.Clear();

            var sourceList = currentClipboardSubTab switch
            {
                ClipboardSubTab.Notes => ClipboardHistoryManager.Instance.NotesItems,
                ClipboardSubTab.Clipboard => ClipboardHistoryManager.Instance.ClipboardItems,
                ClipboardSubTab.Screenshots => ClipboardHistoryManager.Instance.ScreenshotsItems,
                ClipboardSubTab.Snippets => ClipboardHistoryManager.Instance.SnippetsItems,
                _ => ClipboardHistoryManager.Instance.ClipboardItems
            };

            string search = TxtClipboardSearch.Text.Trim();
            var filtered = string.IsNullOrEmpty(search)
                ? sourceList.ToList()
                : sourceList.Where(i => i.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || 
                                        i.Content.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                ClipboardListScrollViewer?.ScrollToTop();
                ClipboardEmptyPrompt.Visibility = Visibility.Visible;
                TxtClipboardEmptyTitle.Text = currentClipboardSubTab switch
                {
                    ClipboardSubTab.Notes => "No notes yet",
                    ClipboardSubTab.Clipboard => "Clipboard history is empty",
                    ClipboardSubTab.Screenshots => "No screenshots yet",
                    ClipboardSubTab.Snippets => "No snippets yet",
                    _ => "Empty"
                };
                TxtClipboardEmptySubtitle.Text = currentClipboardSubTab switch
                {
                    ClipboardSubTab.Notes => "Click the '+' button to add your first note",
                    ClipboardSubTab.Clipboard => "1-9 to paste • Ctrl+C to copy • Click to reuse",
                    ClipboardSubTab.Screenshots => "Win+Shift+S to capture • Ctrl+C to copy image",
                    ClipboardSubTab.Snippets => "Click the '+' button to create a reusable snippet",
                    _ => ""
                };
                return;
            }

            if (filtered.Count <= 3)
            {
                ClipboardListScrollViewer?.ScrollToTop();
            }

            ClipboardEmptyPrompt.Visibility = Visibility.Collapsed;

            foreach (var item in filtered)
            {
                var card = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 0, 2),
                    Cursor = Cursors.Hand,
                    ToolTip = item.Type == HistoryItemType.Image ? "Click to copy image to clipboard" : "Click to copy to clipboard"
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Left: Pin Icon (if pinned) + Thumbnail (if Image) + Title / Code Content Snippet
                var leftStack = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

                if (item.IsPinned)
                {
                    var pinIcon = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconPin"),
                        Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                        Width = 11,
                        Height = 11,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 7, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    System.Windows.Controls.DockPanel.SetDock(pinIcon, System.Windows.Controls.Dock.Left);
                    leftStack.Children.Add(pinIcon);
                }

                if (item.Type == HistoryItemType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                        bmp.DecodePixelWidth = 100;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();

                        var imgBorder = new Border
                        {
                            Width = 44,
                            Height = 32,
                            CornerRadius = new CornerRadius(5),
                            ClipToBounds = true,
                            Margin = new Thickness(0, 0, 9, 0),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                            BorderThickness = new Thickness(1),
                            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var imgControl = new Image
                        {
                            Source = bmp,
                            Stretch = Stretch.UniformToFill
                        };
                        imgBorder.Child = imgControl;
                        System.Windows.Controls.DockPanel.SetDock(imgBorder, System.Windows.Controls.Dock.Left);
                        leftStack.Children.Add(imgBorder);
                    }
                    catch { }
                }

                if (item.Type == HistoryItemType.ColorHex)
                {
                    try
                    {
                        var color = (Color)ColorConverter.ConvertFromString(item.Content.Trim());
                        var colorBox = new Border
                        {
                            Width = 12,
                            Height = 12,
                            CornerRadius = new CornerRadius(3),
                            Background = new SolidColorBrush(color),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                            BorderThickness = new Thickness(1),
                            Margin = new Thickness(0, 0, 7, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        System.Windows.Controls.DockPanel.SetDock(colorBox, System.Windows.Controls.Dock.Left);
                        leftStack.Children.Add(colorBox);
                    }
                    catch { }
                }

                var textInfoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

                var titleBlock = new TextBlock
                {
                    Text = item.Title,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = item.IsPinned ? FontWeights.SemiBold : FontWeights.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                textInfoStack.Children.Add(titleBlock);

                if (item.Type == HistoryItemType.Image && !string.IsNullOrEmpty(item.Content))
                {
                    var subBlock = new TextBlock
                    {
                        Text = item.Content,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                        FontSize = 11,
                        Margin = new Thickness(0, 1, 0, 0)
                    };
                    textInfoStack.Children.Add(subBlock);
                }

                leftStack.Children.Add(textInfoStack);
                Grid.SetColumn(leftStack, 0);
                grid.Children.Add(leftStack);

                // Right: Relative Time + Action Buttons (Pin, Delete)
                var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };

                var timeBlock = new TextBlock
                {
                    Text = item.RelativeTime,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73)),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                rightStack.Children.Add(timeBlock);

                // Pin toggle button
                var btnPin = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = item.IsPinned ? "Unpin" : "Pin to top"
                };
                var pinPath = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconPin"),
                    Fill = item.IsPinned ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)) : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                    Width = 9,
                    Height = 9,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnPin.Child = pinPath;
                btnPin.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;
                    ClipboardHistoryManager.Instance.TogglePin(item);
                };
                rightStack.Children.Add(btnPin);

                // Edit button (for Notes and Snippets)
                if (item.Type == HistoryItemType.Note || item.Type == HistoryItemType.Snippet || currentClipboardSubTab == ClipboardSubTab.Notes || currentClipboardSubTab == ClipboardSubTab.Snippets)
                {
                    var btnEdit = new Border
                    {
                        Width = 22,
                        Height = 22,
                        CornerRadius = new CornerRadius(11),
                        Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 0, 4, 0),
                        ToolTip = "Edit Note / Snippet"
                    };
                    var editPath = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconCompose"),
                        Fill = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                        Width = 9,
                        Height = 9,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnEdit.Child = editPath;
                    btnEdit.MouseLeftButtonDown += (s, ev) =>
                    {
                        ev.Handled = true;
                        OpenNoteEditor(item);
                    };
                    rightStack.Children.Add(btnEdit);
                }

                // Delete button
                var btnDel = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                    Cursor = Cursors.Hand,
                    ToolTip = "Delete"
                };
                var delPath = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconTrash"),
                    Fill = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                    Width = 9,
                    Height = 9,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnDel.Child = delPath;
                btnDel.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;
                    ClipboardHistoryManager.Instance.DeleteItem(item);
                };
                rightStack.Children.Add(btnDel);

                Grid.SetColumn(rightStack, 1);
                grid.Children.Add(rightStack);

                card.Child = grid;

                card.MouseEnter += (s, ev) =>
                {
                    card.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                };
                card.MouseLeave += (s, ev) =>
                {
                    card.Background = Brushes.Transparent;
                };

                card.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;

                    if (ev.ClickCount >= 2 && (item.Type == HistoryItemType.Note || item.Type == HistoryItemType.Snippet || currentClipboardSubTab == ClipboardSubTab.Notes || currentClipboardSubTab == ClipboardSubTab.Snippets))
                    {
                        OpenNoteEditor(item);
                        return;
                    }

                    ClipboardHistoryManager.Instance.CopyToClipboard(item);
                    titleBlock.Text = item.Type == HistoryItemType.Image ? "✓ Image copied!" : "✓ Copied to clipboard!";
                    titleBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE4, 0x6C));
                    Task.Delay(1200).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            titleBlock.Text = item.Title;
                            titleBlock.Foreground = Brushes.White;
                        });
                    });
                };

                ClipboardItemsStackPanel.Children.Add(card);
            }
        }

        private void ShelfDropZoneBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.OriginalSource is DependencyObject dep)
            {
                var parent = FindVisualParent<System.Windows.Controls.Border>(dep);
                if (parent != null && parent != ShelfDropZoneBorder) return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Select files to dock"
            };

            if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
            {
                DockShelfManager.Instance.AddFiles(dialog.FileNames);
                UpdateIndicatorVisuals();
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }

        private void ShelfFilesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ShelfFilesScrollViewer != null)
            {
                e.Handled = true;
                double offset = ShelfFilesScrollViewer.HorizontalOffset - (e.Delta * 0.8);
                ShelfFilesScrollViewer.ScrollToHorizontalOffset(offset);
            }
        }

        private void DockItemCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            dockDragStartPoint = e.GetPosition(this);

            // Double click opens / navigates
            if (e.ClickCount >= 2 && sender is FrameworkElement elem && elem.DataContext is DockItem item)
            {
                try
                {
                    if (item.ItemType == DockItemType.Link && !string.IsNullOrEmpty(item.TextContent))
                    {
                        Process.Start(new ProcessStartInfo(item.TextContent) { UseShellExecute = true });
                    }
                    else if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                    {
                        Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                    }
                    else if (!string.IsNullOrEmpty(item.TextContent))
                    {
                        Clipboard.SetText(item.TextContent);
                    }
                }
                catch { }
            }
        }

        private void DockItemCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement elem && elem.DataContext is DockItem item)
            {
                Point currentPoint = e.GetPosition(this);
                if (Math.Abs(currentPoint.X - dockDragStartPoint.X) > 8 || Math.Abs(currentPoint.Y - dockDragStartPoint.Y) > 8)
                {
                    isDraggingDockItem = true;
                    try
                    {
                        DataObject data = new DataObject();

                        // 1. Provide FileDrop if file or directory exists
                        if (!string.IsNullOrEmpty(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
                        {
                            data.SetData(DataFormats.FileDrop, new string[] { item.FilePath });
                        }

                        // 2. Provide Unicode Text
                        if (!string.IsNullOrEmpty(item.TextContent))
                        {
                            data.SetData(DataFormats.UnicodeText, item.TextContent);
                            data.SetData(DataFormats.Text, item.TextContent);
                        }
                        else if (!string.IsNullOrEmpty(item.FilePath))
                        {
                            data.SetData(DataFormats.UnicodeText, item.FilePath);
                        }

                        // 3. Provide Bitmap
                        if (item.Thumbnail is BitmapSource bmp)
                        {
                            data.SetData(DataFormats.Bitmap, bmp);
                        }

                        DragDropEffects result = DragDrop.DoDragDrop(elem, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                        
                        // Mark as used (100% full green ring + checkmark ✓ -> auto reverts to blue after 2.5s)
                        DockShelfManager.Instance.MarkItemUsed(item);
                    }
                    catch { }
                    finally
                    {
                        isDraggingDockItem = false;
                    }
                }
            }
        }

        private void BtnRemoveDockItem_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is DockItem item)
            {
                DockShelfManager.Instance.RemoveItem(item);
                if (DockShelfManager.Instance.Items.Count == 0)
                {
                    isExpanded = false;
                }
                UpdateIndicatorVisuals();
            }
        }

        private void ShelfDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent(DataFormats.Bitmap) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                e.Data.GetDataPresent(DataFormats.Text) ||
                e.Data.GetDataPresent(DataFormats.Html))
            {
                e.Effects = DragDropEffects.Copy;
                ShelfDropZoneDashed.StrokeThickness = 2.4;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void ShelfDropZone_DragLeave(object sender, DragEventArgs e)
        {
            e.Handled = true;
            ShelfDropZoneDashed.StrokeThickness = 1.2;
        }

        private void ShelfDropZone_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            ShelfDropZoneDashed.StrokeThickness = 1.2;
            DockShelfManager.Instance.AddDataObject(e.Data);
            UpdateIndicatorVisuals();
        }

        #endregion

        #region Apple AirDrop & LocalSend Engine

        private bool isAirDropHudActive = false;
        private bool isAirDropTransferActive = false;
        private readonly DispatcherTimer airDropAutoHideTimer = new DispatcherTimer();
        private double currentAirDropProgress = 0.0;
        private double targetAirDropProgress = 0.0;
        private readonly DispatcherTimer airDropProgressSmoothTimer = new DispatcherTimer(DispatcherPriority.Render);
        private bool isAirDropWaitingForCheckmark = false;

        private void InitAirDrop()
        {
            AirDropDevicesList.ItemsSource = AirDropDiscoveryService.Instance.DiscoveredDevices;
            AirDropDiscoveryService.Instance.OnDevicesUpdated += () => Dispatcher.Invoke(UpdateAirDropDevicesVisibility);

            airDropAutoHideTimer.Interval = TimeSpan.FromMilliseconds(4000);
            airDropAutoHideTimer.Tick += (s, e) =>
            {
                airDropAutoHideTimer.Stop();
                isAirDropHudActive = false;
                isAirDropTransferActive = false;
                HideAllHudViews();
                StealthView.Visibility = Visibility.Visible;
                UpdateIndicatorVisuals();
            };

            airDropProgressSmoothTimer.Interval = TimeSpan.FromMilliseconds(16);
            airDropProgressSmoothTimer.Tick += (s, e) =>
            {
                if (currentAirDropProgress < targetAirDropProgress)
                {
                    double delta = targetAirDropProgress - currentAirDropProgress;
                    double step = Math.Max(0.015, delta * 0.25);
                    currentAirDropProgress = Math.Min(targetAirDropProgress, currentAirDropProgress + step);
                    DrawAirDropProgressArc(currentAirDropProgress);
                }
                else if (isAirDropWaitingForCheckmark && currentAirDropProgress >= 0.99)
                {
                    isAirDropWaitingForCheckmark = false;
                    airDropProgressSmoothTimer.Stop();

                    AirDropCompactArc.Data = null;
                    AirDropCompactCheckmark.Visibility = Visibility.Visible;
                    AirDropCompactFailedCross.Visibility = Visibility.Collapsed;

                    airDropAutoHideTimer.Stop();
                    airDropAutoHideTimer.Start();
                }
            };

            AirDropManager.Instance.OnTransferStateChanged += (state, info) =>
            {
                Dispatcher.Invoke(() =>
                {
                    switch (state)
                    {
                        case AirDropState.ChoosingDevice:
                            isAirDropHudActive = true;
                            isVolumeHudActive = false;
                            isBrightnessHudActive = false;
                            isDndHudActive = false;
                            isBluetoothHudActive = false;
                            isExpanded = false;
                            airDropAutoHideTimer.Stop();
                            airDropProgressSmoothTimer.Stop();
                            isAirDropWaitingForCheckmark = false;

                            HideAllHudViews();
                            AirDropPickerView.Visibility = Visibility.Visible;
                            TxtPickerFileName.Text = info?.FileName ?? "file";

                            // Real Live Preview: Image or Document / Link
                            if (info?.Thumbnail != null)
                            {
                                AirDropPickerPreviewImg.Source = info.Thumbnail;
                                AirDropPickerPreviewImg.Visibility = Visibility.Visible;
                                AirDropPickerPreviewDoc.Visibility = Visibility.Collapsed;
                            }
                            else
                            {
                                AirDropPickerPreviewImg.Visibility = Visibility.Collapsed;
                                AirDropPickerPreviewDoc.Visibility = Visibility.Visible;

                                if (info != null && info.FileName.StartsWith("🔗"))
                                {
                                    AirDropPickerDocIcon.Data = (Geometry)FindResource("IconSafari");
                                    AirDropPickerDocIcon.Fill = new SolidColorBrush(Color.FromRgb(0x37, 0xA3, 0xDE));
                                    TxtPickerFileSize.Text = "Web Link";
                                }
                                else if (info != null && info.FileName.StartsWith("📝"))
                                {
                                    AirDropPickerDocIcon.Data = (Geometry)FindResource("IconNote");
                                    AirDropPickerDocIcon.Fill = new SolidColorBrush(Color.FromRgb(0x37, 0xA3, 0xDE));
                                    TxtPickerFileSize.Text = "Text Note";
                                }
                                else
                                {
                                    AirDropPickerDocIcon.Data = (Geometry)FindResource("IconNote");
                                    AirDropPickerDocIcon.Fill = new SolidColorBrush(Color.FromRgb(0x37, 0xA3, 0xDE));
                                    TxtPickerFileSize.Text = (info?.TotalBytes > 0) ? FormatBytes(info.TotalBytes) : "Document";
                                }
                            }

                            UpdateAirDropDevicesVisibility();
                            break;

                        case AirDropState.Transferring:
                            airDropAutoHideTimer.Stop();
                            isAirDropTransferActive = true;
                            isAirDropHudActive = false;
                            isAirDropWaitingForCheckmark = false;

                            HideAllHudViews();
                            StealthView.Visibility = Visibility.Visible;

                            // Hide ALL other compact indicators to prevent overlap
                            CompactAlbumArtBorder.Visibility = Visibility.Collapsed;
                            CompactVisualizer.Visibility = Visibility.Collapsed;
                            TimerCompactRing.Visibility = Visibility.Collapsed;
                            TxtTimerCompact.Visibility = Visibility.Collapsed;
                            DockCompactContainer.Visibility = Visibility.Collapsed;
                            DockCompactRing.Visibility = Visibility.Collapsed;
                            BluetoothCompactContainer.Visibility = Visibility.Collapsed;
                            TxtBluetoothCompact.Visibility = Visibility.Collapsed;
                            NetworkCompactContainer.Visibility = Visibility.Collapsed;
                            TxtNetworkCompact.Visibility = Visibility.Collapsed;
                            IdleFaceContainer.Visibility = Visibility.Collapsed;

                            // Show only AirDrop compact indicators
                            AirDropCompactContainer.Visibility = Visibility.Visible;
                            AirDropCompactProgressContainer.Visibility = Visibility.Visible;
                            AirDropCompactCheckmark.Visibility = Visibility.Collapsed;
                            AirDropCompactFailedCross.Visibility = Visibility.Collapsed;

                            TxtAirDropSubtitle.Text = info?.DeviceType ?? "mobile";
                            TxtAirDropTitle.Text = (info?.IsSending == true) 
                                ? $"AirDrop to {info?.DeviceName ?? "Device"}" 
                                : $"AirDrop from {info?.DeviceName ?? "Chi"}";

                            AirDropFileThumb.Source = info?.Thumbnail;

                            // Compact pill state (Left: Cyan AirDrop Icon, Right: Blue Progress Arc)
                            double targetCompactW = currentMode == ShapeDisplayMode.Notch ? 210.0 : 190.0;
                            AnimateSize(targetCompactW, 34.0);

                            currentAirDropProgress = 0.05;
                            targetAirDropProgress = 0.12;
                            DrawAirDropProgressArc(currentAirDropProgress);
                            if (!airDropProgressSmoothTimer.IsEnabled)
                            {
                                airDropProgressSmoothTimer.Start();
                            }
                            break;

                        case AirDropState.Completed:
                            isAirDropTransferActive = true;
                            isAirDropHudActive = false;

                            HideAllHudViews();
                            StealthView.Visibility = Visibility.Visible;

                            AirDropCompactProgressContainer.Visibility = Visibility.Visible;
                            AirDropCompactFailedCross.Visibility = Visibility.Collapsed;

                            double targetCompW = currentMode == ShapeDisplayMode.Notch ? 210.0 : 190.0;
                            AnimateSize(targetCompW, 34.0);

                            // Smoothly complete the arc to 100% before popping green checkmark!
                            targetAirDropProgress = 1.0;
                            isAirDropWaitingForCheckmark = true;
                            if (!airDropProgressSmoothTimer.IsEnabled)
                            {
                                airDropProgressSmoothTimer.Start();
                            }
                            break;

                        case AirDropState.Failed:
                            isAirDropTransferActive = true;
                            isAirDropHudActive = false;
                            isAirDropWaitingForCheckmark = false;
                            airDropProgressSmoothTimer.Stop();

                            HideAllHudViews();
                            StealthView.Visibility = Visibility.Visible;

                            AirDropCompactProgressContainer.Visibility = Visibility.Visible;
                            AirDropCompactCheckmark.Visibility = Visibility.Collapsed;
                            AirDropCompactFailedCross.Visibility = Visibility.Visible;
                            AirDropCompactArc.Data = null;

                            double targetFailW = currentMode == ShapeDisplayMode.Notch ? 210.0 : 190.0;
                            AnimateSize(targetFailW, 34.0);

                            airDropAutoHideTimer.Stop();
                            airDropAutoHideTimer.Start();
                            break;

                        case AirDropState.Idle:
                        case AirDropState.Cancelled:
                            isAirDropHudActive = false;
                            isAirDropTransferActive = false;
                            isAirDropWaitingForCheckmark = false;
                            airDropProgressSmoothTimer.Stop();
                            AirDropCompactContainer.Visibility = Visibility.Collapsed;
                            AirDropCompactProgressContainer.Visibility = Visibility.Collapsed;
                            AirDropCompactCheckmark.Visibility = Visibility.Collapsed;
                            AirDropCompactFailedCross.Visibility = Visibility.Collapsed;
                            AirDropCompactArc.Data = null;
                            HideAllHudViews();
                            StealthView.Visibility = Visibility.Visible;
                            UpdateIndicatorVisuals();
                            break;
                    }
                });
            };

            AirDropManager.Instance.OnProgressUpdated += (progress) =>
            {
                Dispatcher.Invoke(() =>
                {
                    targetAirDropProgress = Math.Clamp(progress, 0.05, 1.0);
                    if (!airDropProgressSmoothTimer.IsEnabled)
                    {
                        airDropProgressSmoothTimer.Start();
                    }
                });
            };

            AirDropManager.Instance.OnStatusChanged += (status) =>
            {
                Dispatcher.Invoke(() => TxtAirDropSubtitle.Text = status);
            };

            // Start AirDrop & LocalSend Background Receiver Server & Discovery Beacon
            try
            {
                AirDropReceiverServer.Instance.Start();
                AirDropDiscoveryService.Instance.StartBackgroundBeacon();
            }
            catch { }

            AirDropReceiverServer.Instance.OnIncomingTransferRequested += (req) =>
            {
                Dispatcher.Invoke(() =>
                {
                    isAirDropHudActive = true;
                    isAirDropTransferActive = false;
                    isVolumeHudActive = false;
                    isBrightnessHudActive = false;
                    isDndHudActive = false;
                    isBluetoothHudActive = false;
                    isExpanded = false;
                    airDropAutoHideTimer.Stop();
                    airDropProgressSmoothTimer.Stop();
                    isAirDropWaitingForCheckmark = false;

                    HideAllHudViews();
                    AirDropIncomingHudView.Visibility = Visibility.Visible;

                    AirDropIncomingHudView.Margin = currentMode == ShapeDisplayMode.Notch
                        ? new Thickness(20, 44, 20, 16)
                        : new Thickness(20, 16, 20, 16);

                    string fileCountText = req.FileCount == 1 ? (req.Files.FirstOrDefault()?.FileName ?? "1 file") : $"{req.FileCount} files";
                    TxtAirDropIncomingSender.Text = $"{req.SenderAlias} would like to share {fileCountText}";

                    if (req.Thumbnail != null)
                    {
                        ImgAirDropIncomingPreview.Source = req.Thumbnail;
                        ImgAirDropIncomingPreview.Visibility = Visibility.Visible;
                        IconAirDropIncomingFallback.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ImgAirDropIncomingPreview.Visibility = Visibility.Collapsed;
                        IconAirDropIncomingFallback.Visibility = Visibility.Visible;
                        string ext = System.IO.Path.GetExtension(req.PrimaryFileName).TrimStart('.').ToUpperInvariant();
                        TxtAirDropIncomingFileType.Text = string.IsNullOrEmpty(ext) ? "FILE" : ext;
                    }

                    double targetW = 430;
                    double targetH = currentMode == ShapeDisplayMode.Notch ? 208 : 172;
                    AnimateSize(targetW, targetH);
                });
            };

            AirDropReceiverServer.Instance.OnProgressUpdated += (prog, transferred, total) =>
            {
                Dispatcher.Invoke(() =>
                {
                    targetAirDropProgress = Math.Clamp(prog, 0.05, 1.0);
                    if (!airDropProgressSmoothTimer.IsEnabled)
                    {
                        airDropProgressSmoothTimer.Start();
                    }
                });
            };

            AirDropReceiverServer.Instance.OnTransferCompleted += (senderName, savedPaths) =>
            {
                Dispatcher.Invoke(() =>
                {
                    isAirDropTransferActive = true;
                    isAirDropHudActive = false;

                    HideAllHudViews();
                    StealthView.Visibility = Visibility.Visible;

                    UpdateIndicatorVisuals();

                    targetAirDropProgress = 1.0;
                    isAirDropWaitingForCheckmark = true;
                    if (!airDropProgressSmoothTimer.IsEnabled)
                    {
                        airDropProgressSmoothTimer.Start();
                    }

                    // Automatically pin received files to File Shelf for 1-click open / drag-and-drop
                    if (savedPaths != null && savedPaths.Count > 0)
                    {
                        try { DockShelfManager.Instance.AddFiles(savedPaths.ToArray()); } catch { }
                    }
                });
            };
        }

        private void BtnAirDropAccept_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AirDropReceiverServer.Instance.AcceptCurrentTransfer();

            // Collapse to compact pill with active progress bar
            isAirDropHudActive = false;
            isAirDropTransferActive = true;
            isAirDropWaitingForCheckmark = false;

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();

            currentAirDropProgress = 0.05;
            targetAirDropProgress = 0.10;
            DrawAirDropProgressArc(currentAirDropProgress);
            if (!airDropProgressSmoothTimer.IsEnabled) airDropProgressSmoothTimer.Start();
        }

        private void BtnAirDropDecline_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AirDropReceiverServer.Instance.DeclineCurrentTransfer();
            isAirDropHudActive = false;
            isAirDropTransferActive = false;
            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;
            UpdateIndicatorVisuals();
        }

        private void UpdateAirDropDevicesVisibility()
        {
            bool hasDevices = AirDropDiscoveryService.Instance.DiscoveredDevices.Count > 0;
            AirDropDevicesScrollViewer.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
            AirDropSearchingContainer.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;

            if (isAirDropHudActive && AirDropPickerView.Visibility == Visibility.Visible)
            {
                double targetW = 410.0;
                double targetH = currentMode == ShapeDisplayMode.Notch ? 212.0 : 206.0;
                AnimateSize(targetW, targetH);
            }
        }

        private void DrawAirDropProgressArc(double progress)
        {
            // 1. Large HUD Arc (56x56)
            double size = 56.0;
            double thickness = 4.67;
            double radius = (size - thickness) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            // 2. Compact Arc (20x20)
            double cSize = 20.0;
            double cThickness = 2.4;
            double cRadius = (cSize - cThickness) / 2.0;
            double cCx = cSize / 2.0;
            double cCy = cSize / 2.0;

            if (progress <= 0.0)
            {
                AirDropProgressArc.Data = null;
                AirDropCompactArc.Data = null;
                return;
            }

            if (progress >= 0.999)
            {
                AirDropProgressArc.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                AirDropCompactArc.Data = new EllipseGeometry(new Point(cCx, cCy), cRadius, cRadius);
                return;
            }

            double angle = progress * 360.0;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            bool isLargeArc = angle > 180.0;

            // Large Arc
            double endX = cx + radius * Math.Cos(radians);
            double endY = cy + radius * Math.Sin(radians);
            var figure = new PathFigure { StartPoint = new Point(cx, cy - radius), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment { Point = new Point(endX, endY), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise, IsLargeArc = isLargeArc });
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            AirDropProgressArc.Data = geo;

            // Compact Arc
            double cEndX = cCx + cRadius * Math.Cos(radians);
            double cEndY = cCy + cRadius * Math.Sin(radians);
            var cFigure = new PathFigure { StartPoint = new Point(cCx, cCy - cRadius), IsClosed = false, IsFilled = false };
            cFigure.Segments.Add(new ArcSegment { Point = new Point(cEndX, cEndY), Size = new Size(cRadius, cRadius), SweepDirection = SweepDirection.Clockwise, IsLargeArc = isLargeArc });
            var cGeo = new PathGeometry();
            cGeo.Figures.Add(cFigure);
            AirDropCompactArc.Data = cGeo;
        }

        private void AirDropCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (AirDropHudView.Visibility == Visibility.Visible)
            {
                // Collapse to compact
                AirDropHudView.Visibility = Visibility.Collapsed;
                StealthView.Visibility = Visibility.Visible;
                double targetCompactW = currentMode == ShapeDisplayMode.Notch ? 220.0 : 200.0;
                AnimateSize(targetCompactW, 34.0);
            }
            else
            {
                // Expand to large HUD
                StealthView.Visibility = Visibility.Collapsed;
                AirDropHudView.Visibility = Visibility.Visible;
                AnimateSize(367, 86);
            }
        }

        private void AirDropDropTile_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent(DataFormats.Bitmap) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                e.Data.GetDataPresent(DataFormats.Text) ||
                e.Data.GetDataPresent(DataFormats.Html))
            {
                e.Effects = DragDropEffects.Copy;
                AirDropTileDashed.StrokeThickness = 2.4;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void AirDropDropTile_DragLeave(object sender, DragEventArgs e)
        {
            e.Handled = true;
            AirDropTileDashed.StrokeThickness = 1.2;
        }

        private void AirDropDropTile_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            AirDropTileDashed.StrokeThickness = 1.2;

            string? targetFilePath = null;

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                targetFilePath = files[0];
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                string imgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AirDrop_Image_{DateTime.Now:HHmmss}.png");
                using (var fs = new FileStream(imgPath, FileMode.Create))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(fs);
                }
                targetFilePath = imgPath;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text))
            {
                string rawText = (e.Data.GetData(DataFormats.UnicodeText) as string) 
                              ?? (e.Data.GetData(DataFormats.Text) as string) 
                              ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(rawText))
                {
                    string displayName = "Note";
                    string trimmed = rawText.Trim();
                    if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                    {
                        string host = uri.Host.Replace("www.", "");
                        displayName = $"🔗 {host}";
                    }
                    else
                    {
                        displayName = trimmed.Length > 20 ? $"📝 {trimmed.Substring(0, 18)}..." : $"📝 {trimmed}";
                    }

                    string txtPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AirDrop_Clipboard.txt");
                    File.WriteAllText(txtPath, rawText);
                    AirDropManager.Instance.PrepareShare(txtPath, displayName);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(targetFilePath) && File.Exists(targetFilePath))
            {
                AirDropManager.Instance.PrepareShare(targetFilePath);
            }
        }

        private void AirDropDropTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = false,
                Title = "Select file to send via AirDrop"
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
            {
                AirDropManager.Instance.PrepareShare(dialog.FileName);
            }
        }

        private void AirDropDeviceCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is AirDropDevice dev)
            {
                _ = AirDropManager.Instance.SendToDeviceAsync(dev);
            }
        }

        private void BtnCloseAirDropPicker_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AirDropManager.Instance.CancelTransfer();
        }

        private void BtnAirDropQuickSendApp_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                string? file = AirDropManager.Instance.PendingFilePath;
                string localSendPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "LocalSend", "localsend_app.exe"
                );

                if (System.IO.File.Exists(localSendPath) && !string.IsNullOrEmpty(file))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = localSendPath,
                        Arguments = $"\"{file}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch { }
            AirDropManager.Instance.CancelTransfer();
        }

        private void BtnAirDropQuickShare_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                string? file = AirDropManager.Instance.PendingFilePath ?? AirDropManager.Instance.CurrentTransfer?.FilePath;

                string[] possibleQuickSharePaths = new[]
                {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Quick Share", "quick_share.exe"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Nearby Share", "nearby_share.exe"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Quick Share", "quick_share.exe"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby Share", "nearby_share.exe"),
                };

                bool launched = false;
                if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
                {
                    foreach (var path in possibleQuickSharePaths)
                    {
                        if (System.IO.File.Exists(path))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = path,
                                Arguments = $"\"{file}\"",
                                UseShellExecute = true
                            });
                            launched = true;
                            break;
                        }
                    }
                }

                if (!launched)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "shell:AppsFolder\\NearbyShare_21hpf16v5xp10!NearbyShare",
                        UseShellExecute = true
                    });
                }
            }
            catch { }
            AirDropManager.Instance.CancelTransfer();
        }

        private void BtnAirDropCancel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AirDropManager.Instance.CancelTransfer();
        }

        private void BtnAirDropOpen_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var file = AirDropManager.Instance.CurrentTransfer?.FilePath;
                if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
                {
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                }
                else
                {
                    string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    Process.Start(new ProcessStartInfo(downloads) { UseShellExecute = true });
                }
            }
            catch { }
            AirDropManager.Instance.CancelTransfer();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (d >= 1024 && i < suffixes.Length - 1)
            {
                d /= 1024.0;
                i++;
            }
            return $"{d:0.0} {suffixes[i]}";
        }

        #endregion

        #region Apple Screen Mirroring Engine

        private void ScreenShareCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (isScreenMirroringHudActive)
            {
                isScreenMirroringHudActive = false;
                HideAllHudViews();
                StealthView.Visibility = Visibility.Visible;
                UpdateIndicatorVisuals();
            }
            else
            {
                ExpandScreenMirroringHud();
            }
        }

        private void ExpandScreenMirroringHud()
        {
            isScreenMirroringHudActive = true;
            isVolumeHudActive = false;
            isBrightnessHudActive = false;
            isDndHudActive = false;
            isBluetoothHudActive = false;
            isAirDropHudActive = false;
            isExpanded = false;

            HideAllHudViews();
            ScreenMirroringHudView.Visibility = Visibility.Visible;

            ScreenMirroringHudView.Margin = currentMode == ShapeDisplayMode.Notch
                ? new Thickness(20, 38, 20, 16)
                : new Thickness(20, 14, 20, 14);

            double targetW = 360;
            double targetH = currentMode == ShapeDisplayMode.Notch ? 160 : 138;
            AnimateSize(targetW, targetH);
        }

        private void BtnStopScreenMirroring_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            StopActiveScreenShare();

            isScreenMirroringHudActive = false;
            isScreenSharingActive = false;
            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;
            UpdateIndicatorVisuals();
        }

        private void StopActiveScreenShare()
        {
            Task.Run(() =>
            {
                try
                {
                    // 1. Google Meet / Chrome / Edge / WebRTC floating sharing banners
                    EnumWindows((hWnd, lParam) =>
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            var sb = new StringBuilder(256);
                            GetWindowText(hWnd, sb, 256);
                            string title = sb.ToString();
                            if (!string.IsNullOrEmpty(title) &&
                                (title.Contains("is sharing", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("sharing your screen", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("meet.google.com", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("Screen Sharing Indicator", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("Discord Screen Share", StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    var element = System.Windows.Automation.AutomationElement.FromHandle(hWnd);
                                    if (element != null)
                                    {
                                        var condition = new System.Windows.Automation.PropertyCondition(
                                            System.Windows.Automation.AutomationElement.ControlTypeProperty,
                                            System.Windows.Automation.ControlType.Button);
                                        var buttons = element.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
                                        foreach (System.Windows.Automation.AutomationElement btn in buttons)
                                        {
                                            string name = btn.Current.Name;
                                            if (name.Contains("Stop", StringComparison.OrdinalIgnoreCase))
                                            {
                                                if (btn.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out object patternObj))
                                                {
                                                    ((System.Windows.Automation.InvokePattern)patternObj).Invoke();
                                                    return false;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }

                                // Fallback: post WM_CLOSE to the floating share notification window
                                PostMessage(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    // 2. Zoom global shortcut (Alt + S)
                    var zoomProcs = Process.GetProcessesByName("Zoom");
                    if (zoomProcs.Length > 0)
                    {
                        keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt down
                        keybd_event(0x53, 0, 0, UIntPtr.Zero); // S down
                        keybd_event(0x53, 0, 2, UIntPtr.Zero); // S up
                        keybd_event(0x12, 0, 2, UIntPtr.Zero); // Alt up
                    }
                }
                catch { }
            });
        }

        private void BtnNavScreenMirroring_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.ScreenMirroring;
            UpdateIndicatorVisuals();
        }

        private void BtnExpandedStopMirroring_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (isScreenSharingActive)
            {
                StopActiveScreenShare();
                isScreenSharingActive = false;
                TxtExpandedStopMirroringBtn.Text = "Windows Cast / Mirror";
                TxtExpandedScreenMirroringTarget.Text = "infinity";
            }
            else
            {
                // Open Windows native Cast / Project menu (Win + K)
                try
                {
                    keybd_event(0x5B, 0, 0, UIntPtr.Zero); // Win down
                    keybd_event(0x4B, 0, 0, UIntPtr.Zero); // K down
                    keybd_event(0x4B, 0, 2, UIntPtr.Zero); // K up
                    keybd_event(0x5B, 0, 2, UIntPtr.Zero); // Win up
                }
                catch { }
            }
        }

        #endregion

        #region Apple Timer & Stopwatch Engine

        private readonly DispatcherTimer timerAutoCollapseTimer = new DispatcherTimer();

        private void InitAppleTimer()
        {
            timerAutoCollapseTimer.Interval = TimeSpan.FromSeconds(10);
            timerAutoCollapseTimer.Tick += (s, e) =>
            {
                timerAutoCollapseTimer.Stop();
                AppleTimerManager.Instance.StopTimer();
                isExpanded = false;
                UpdateIndicatorVisuals();
            };

            AppleTimerManager.Instance.OnTimerTick += (state, remaining, progress) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (state == AppleTimerState.Inactive)
                    {
                        TimerCompactRing.Visibility = Visibility.Collapsed;
                        TxtTimerCompact.Visibility = Visibility.Collapsed;
                        if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                        {
                            UpdateIndicatorVisuals();
                        }
                        return;
                    }

                    if (state != AppleTimerState.Completed)
                    {
                        TxtTimerLabel.Text = "Timer";
                        TxtTimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0x8B, 0x28));
                        TxtTimerExpanded.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0x8B, 0x28));
                        BtnTimerPauseResume.Background = new SolidColorBrush(Color.FromArgb(0x73, 0xFB, 0x8B, 0x28));
                    }

                    string text = AppleTimerManager.FormatTimerText(remaining);
                    TxtTimerCompact.Text = text;
                    TxtTimerExpanded.Text = text;
                    TimerCompactRing.SetProgress(progress);

                    IconTimerPauseResume.Data = (Geometry)FindResource(state == AppleTimerState.Running ? "IconMediaPause" : "IconMediaPlay");

                    if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                    {
                        UpdateIndicatorVisuals();
                    }
                });
            };

            AppleTimerManager.Instance.OnTimerCompleted += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                    }
                    catch { }

                    currentExpandedTab = ExpandedActivityTab.Timer;
                    isExpanded = true;

                    TxtTimerLabel.Text = "Time's Up!";
                    TxtTimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                    TxtTimerExpanded.Text = "0:00";
                    TxtTimerExpanded.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                    BtnTimerPauseResume.Background = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0x45, 0x3A));
                    IconTimerPauseResume.Data = (Geometry)FindResource("IconMediaPlay");

                    timerAutoCollapseTimer.Stop();
                    timerAutoCollapseTimer.Start();

                    UpdateIndicatorVisuals();
                });
            };
        }

        private void CompactAlbumArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Music;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void TimerCompact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            currentExpandedTab = ExpandedActivityTab.Timer;
            isExpanded = true;
            UpdateIndicatorVisuals();
        }

        private void BtnTimerPauseResume_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            timerAutoCollapseTimer.Stop();
            AppleTimerManager.Instance.TogglePauseResume();
        }

        private void BtnTimerCancel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            timerAutoCollapseTimer.Stop();
            AppleTimerManager.Instance.StopTimer();
            UpdateIndicatorVisuals();
        }

        private void BtnTimerMinus_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.AdjustCustomMinutes(-1);
            TxtCustomMinutes.Text = AppleTimerManager.Instance.CustomDurationMinutes.ToString();
        }

        private void BtnTimerPlus_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.AdjustCustomMinutes(1);
            TxtCustomMinutes.Text = AppleTimerManager.Instance.CustomDurationMinutes.ToString();
        }

        private void BtnTimerStartCustom_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(AppleTimerManager.Instance.CustomDurationMinutes);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_1m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(1);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_5m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(5);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_10m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(10);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_15m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(15);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_25m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(25);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_30m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(30);
            UpdateIndicatorVisuals();
        }

        private void BtnPreset_60m_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AppleTimerManager.Instance.StartPreset(60);
            UpdateIndicatorVisuals();
        }

        #endregion

        #region Media & Audio Visualizer Session Engine

        private void InitMediaSession()
        {
            MediaSessionManager.Instance.OnTrackChanged += Media_OnTrackChanged;
            MediaSessionManager.Instance.OnPlaybackStateChanged += Media_OnPlaybackStateChanged;
            MediaSessionManager.Instance.OnTimelineChanged += Media_OnTimelineChanged;

            LyricsManager.Instance.OnLyricsLoaded += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (currentExpandedTab == ExpandedActivityTab.Music && isLyricsViewActive)
                    {
                        RenderLyricsList();
                    }
                });
            };

            LyricsManager.Instance.OnActiveIndexChanged += (newIndex) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (currentExpandedTab == ExpandedActivityTab.Music && isLyricsViewActive)
                    {
                        HighlightActiveLyricLine(newIndex);
                    }
                });
            };

            _ = MediaSessionManager.Instance.InitializeAsync();
        }

        private void TimelineTickerTimer_Tick(object? sender, EventArgs e)
        {
            var track = MediaSessionManager.Instance.CurrentTrack;
            if (string.IsNullOrWhiteSpace(track.Title)) return;
            if (!track.IsPlaying && (!isExpanded || currentExpandedTab != ExpandedActivityTab.Music)) return;

            var estPos = track.GetCurrentEstimatedPosition();
            TxtTimeElapsed.Text = FormatTime(estPos);
            TxtLyricsTimeElapsed.Text = FormatTime(estPos);

                var effectiveDuration = track.Duration;
                if (effectiveDuration <= TimeSpan.Zero && LyricsManager.Instance.HasLyrics && LyricsManager.Instance.CurrentLyrics.Count > 0)
                {
                    effectiveDuration = LyricsManager.Instance.CurrentLyrics[^1].Timestamp + TimeSpan.FromSeconds(15);
                }

                if (effectiveDuration > TimeSpan.Zero)
                {
                    TxtTimeRemaining.Text = FormatTime(effectiveDuration);
                    TxtLyricsTimeRemaining.Text = FormatTime(effectiveDuration);
                    double fraction = Math.Clamp(estPos.TotalSeconds / effectiveDuration.TotalSeconds, 0.0, 1.0);
                    double mainTrackW = MusicScrubberTrackBorder.ActualWidth > 0 ? MusicScrubberTrackBorder.ActualWidth : 376.0;
                    double lyricsTrackW = LyricsScrubberTrackBorder.ActualWidth > 0 ? LyricsScrubberTrackBorder.ActualWidth : 205.0;
                    MusicProgressFill.Width = fraction * mainTrackW;
                    LyricsMiniProgressFill.Width = fraction * lyricsTrackW;
                }
                else
                {
                    TxtTimeRemaining.Text = "0:00";
                    TxtLyricsTimeRemaining.Text = "0:00";
                }

                LyricsManager.Instance.UpdatePlaybackPosition(estPos);

                // Update continuous progressive word gradient highlight during lyrics view
                if (currentExpandedTab == ExpandedActivityTab.Music && isLyricsViewActive && LyricsManager.Instance.ActiveIndex >= 0)
                {
                    HighlightActiveLyricLine(LyricsManager.Instance.ActiveIndex);
                }

                // Smooth gliding momentum-based auto-scroll for lyrics
                if (currentExpandedTab == ExpandedActivityTab.Music && isLyricsViewActive && _lyricItems.Count > 0 && LyricsScrollViewer != null)
                {
                    if (!isManualLyricsScrolling || (DateTime.UtcNow - lastManualLyricsScrollTime).TotalSeconds > 3.0)
                    {
                        isManualLyricsScrolling = false;
                        double diff = _targetLyricsScrollOffset - _currentLyricsScrollOffset;
                        if (Math.Abs(diff) > 0.4)
                        {
                            _currentLyricsScrollOffset += diff * 0.18;
                            LyricsScrollViewer.ScrollToVerticalOffset(_currentLyricsScrollOffset);
                        }
                        else
                        {
                            _currentLyricsScrollOffset = _targetLyricsScrollOffset;
                            LyricsScrollViewer.ScrollToVerticalOffset(_currentLyricsScrollOffset);
                        }
                    }
                }

                // Dynamic Replay state when track reaches end
                bool isEnded = effectiveDuration > TimeSpan.Zero && (estPos >= effectiveDuration || (!track.IsPlaying && estPos >= effectiveDuration - TimeSpan.FromSeconds(1)));
                if (isEnded)
                {
                    IconPlayPauseShape.Data = (Geometry)FindResource("IconMediaReplay");
                    IconLyricsPlayPauseShape.Data = (Geometry)FindResource("IconMediaReplay");
                }
                else
                {
                    IconPlayPauseShape.Data = (Geometry)FindResource(track.IsPlaying ? "IconMediaPause" : "IconMediaPlay");
                    IconLyricsPlayPauseShape.Data = (Geometry)FindResource(track.IsPlaying ? "IconMediaPause" : "IconMediaPlay");
                }
        }

        private bool hasMusicSessionCompact() => MediaSessionManager.Instance.ShouldShowCompactMedia;

        private void Media_OnTrackChanged(TrackInfo track)
        {
            Dispatcher.Invoke(() =>
            {
                bool hasMusic = !string.IsNullOrWhiteSpace(track.Title);

                if (hasMusic)
                {
                    TxtMusicTitle.Text = track.Title;
                    TxtMusicArtist.Text = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
                    TxtLyricsSongTitle.Text = track.Title;
                    TxtLyricsArtist.Text = string.IsNullOrWhiteSpace(track.Artist) ? "" : track.Artist;
                    AppSourceBadge.SetAppSource(track.AppSource);
                    LyricsAppSourceBadge.SetAppSource(track.AppSource);
                    CompactVisualizer.SetAccentFromImage(track.Thumbnail, track.AppSource);

                    if (track.Thumbnail != null)
                    {
                        CompactAlbumArt.Source = track.Thumbnail;
                        ExpandedAlbumArt.Source = track.Thumbnail;
                        LyricsMiniAlbumArt.Source = track.Thumbnail;
                    }
                    else
                    {
                        CompactAlbumArt.Source = null;
                        ExpandedAlbumArt.Source = null;
                        LyricsMiniAlbumArt.Source = null;
                    }

                    CompactVisualizer.IsPlaying = track.IsPlaying;
                    IconPlayPauseShape.Data = (Geometry)FindResource(track.IsPlaying ? "IconMediaPause" : "IconMediaPlay");
                    IconLyricsPlayPauseShape.Data = (Geometry)FindResource(track.IsPlaying ? "IconMediaPause" : "IconMediaPlay");

                    // GPU Optimization: Start timeline ticker only when music is actually playing
                    if (track.IsPlaying && !timelineTickerTimer.IsEnabled) timelineTickerTimer.Start();

                    _ = LyricsManager.Instance.FetchLyricsForTrackAsync(track.Title, track.Artist, track.Duration);
                }
                else
                {
                    CompactAlbumArt.Source = null;
                    ExpandedAlbumArt.Source = null;
                    LyricsMiniAlbumArt.Source = null;
                    CompactVisualizer.IsPlaying = false;
                }

                if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
                {
                    UpdateIndicatorVisuals();
                }
            });
        }

        private void Media_OnPlaybackStateChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                CompactVisualizer.IsPlaying = isPlaying;
                var track = MediaSessionManager.Instance.CurrentTrack;
                var estPos = track.GetCurrentEstimatedPosition();
                bool isEnded = track.Duration > TimeSpan.Zero && (estPos >= track.Duration || (!isPlaying && estPos >= track.Duration - TimeSpan.FromSeconds(1)));

                if (isEnded)
                {
                    IconPlayPauseShape.Data = (Geometry)FindResource("IconMediaReplay");
                    IconLyricsPlayPauseShape.Data = (Geometry)FindResource("IconMediaReplay");
                }
                else
                {
                    IconPlayPauseShape.Data = (Geometry)FindResource(isPlaying ? "IconMediaPause" : "IconMediaPlay");
                    IconLyricsPlayPauseShape.Data = (Geometry)FindResource(isPlaying ? "IconMediaPause" : "IconMediaPlay");
                }

                // GPU Optimization: Only run timeline ticker when music is actively playing or expanded music view is open
                if (isPlaying || (isExpanded && currentExpandedTab == ExpandedActivityTab.Music))
                {
                    if (!timelineTickerTimer.IsEnabled) timelineTickerTimer.Start();
                }
                else
                {
                    if (timelineTickerTimer.IsEnabled) timelineTickerTimer.Stop();
                }
            });
        }

        private void Media_OnTimelineChanged(TimeSpan pos, TimeSpan dur)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTimeElapsed.Text = FormatTime(pos);
                TxtTimeRemaining.Text = FormatTime(dur);
                TxtLyricsTimeElapsed.Text = FormatTime(pos);

                LyricsManager.Instance.UpdatePlaybackPosition(pos);

                if (dur.TotalSeconds > 0)
                {
                    double fraction = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
                    double mainTrackW = MusicScrubberTrackBorder.ActualWidth > 0 ? MusicScrubberTrackBorder.ActualWidth : 376.0;
                    double lyricsTrackW = LyricsScrubberTrackBorder.ActualWidth > 0 ? LyricsScrubberTrackBorder.ActualWidth : 205.0;
                    MusicProgressFill.Width = fraction * mainTrackW;
                    LyricsMiniProgressFill.Width = fraction * lyricsTrackW;
                }
                else
                {
                    MusicProgressFill.Width = 0;
                    LyricsMiniProgressFill.Width = 0;
                }
            });
        }

        private string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private async void BtnPlayPause_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var track = MediaSessionManager.Instance.CurrentTrack;
            var estPos = track.GetCurrentEstimatedPosition();
            bool isEnded = track.Duration > TimeSpan.Zero && (estPos >= track.Duration || (!track.IsPlaying && estPos >= track.Duration - TimeSpan.FromSeconds(1)));
            if (isEnded)
            {
                MusicProgressFill.Width = 0;
                LyricsMiniProgressFill.Width = 0;
                TxtTimeElapsed.Text = "0:00";
                TxtLyricsTimeElapsed.Text = "0:00";
                IconPlayPauseShape.Data = (Geometry)FindResource("IconMediaPause");
                IconLyricsPlayPauseShape.Data = (Geometry)FindResource("IconMediaPause");
                CompactVisualizer.IsPlaying = true;
                await MediaSessionManager.Instance.ReplayAsync();
            }
            else
            {
                await MediaSessionManager.Instance.TogglePlayPauseAsync();
            }
        }

        private async void BtnNext_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await MediaSessionManager.Instance.NextTrackAsync();
        }

        private async void BtnPrev_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await MediaSessionManager.Instance.PreviousTrackAsync();
        }

        private async void BtnReplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            MusicProgressFill.Width = 0;
            LyricsMiniProgressFill.Width = 0;
            TxtTimeElapsed.Text = "0:00";
            TxtLyricsTimeElapsed.Text = "0:00";
            IconPlayPauseShape.Data = (Geometry)FindResource("IconMediaPause");
            IconLyricsPlayPauseShape.Data = (Geometry)FindResource("IconMediaPause");
            CompactVisualizer.IsPlaying = true;
            await MediaSessionManager.Instance.ReplayAsync();
        }

        private async void MusicScrubber_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem)
            {
                Point pt = e.GetPosition(elem);
                double totalWidth = elem.ActualWidth;
                if (totalWidth <= 0) return;

                double fraction = Math.Clamp(pt.X / totalWidth, 0.0, 1.0);
                var track = MediaSessionManager.Instance.CurrentTrack;

                var effectiveDuration = track.Duration;
                if (effectiveDuration <= TimeSpan.Zero && LyricsManager.Instance.HasLyrics && LyricsManager.Instance.CurrentLyrics.Count > 0)
                {
                    effectiveDuration = LyricsManager.Instance.CurrentLyrics[^1].Timestamp + TimeSpan.FromSeconds(15);
                }

                double mainTrackW = MusicScrubberTrackBorder.ActualWidth > 0 ? MusicScrubberTrackBorder.ActualWidth : totalWidth;
                double lyricsTrackW = LyricsScrubberTrackBorder.ActualWidth > 0 ? LyricsScrubberTrackBorder.ActualWidth : 205.0;

                MusicProgressFill.Width = fraction * mainTrackW;
                LyricsMiniProgressFill.Width = fraction * lyricsTrackW;

                if (effectiveDuration.TotalSeconds > 0)
                {
                    var targetPos = TimeSpan.FromSeconds(fraction * effectiveDuration.TotalSeconds);
                    TxtTimeElapsed.Text = FormatTime(targetPos);
                    TxtLyricsTimeElapsed.Text = FormatTime(targetPos);
                    LyricsManager.Instance.UpdatePlaybackPosition(targetPos);
                    await MediaSessionManager.Instance.SeekAsync(targetPos);
                }
            }
        }

        private void BtnToggleLyrics_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            isLyricsViewActive = true;
            MusicStandardPlayerView.Visibility = Visibility.Collapsed;
            MusicLyricsKaraokeView.Visibility = Visibility.Visible;
            double targetW = 580;
            double targetH = currentMode == ShapeDisplayMode.Notch ? 300 : 285;
            AnimateSize(targetW, targetH);

            var track = MediaSessionManager.Instance.CurrentTrack;
            if (!LyricsManager.Instance.HasLyrics && !LyricsManager.Instance.IsLoading && !string.IsNullOrWhiteSpace(track.Title))
            {
                _ = LyricsManager.Instance.FetchLyricsForTrackAsync(track.Title, track.Artist, track.Duration);
            }
            RenderLyricsList();
        }

        private void BtnTogglePlayerFromLyrics_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            isLyricsViewActive = false;
            MusicLyricsKaraokeView.Visibility = Visibility.Collapsed;
            MusicStandardPlayerView.Visibility = Visibility.Visible;
            double targetW = 510;
            double targetH = 195;
            AnimateSize(targetW, targetH);
        }

        private void LyricsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            isManualLyricsScrolling = true;
            lastManualLyricsScrollTime = DateTime.UtcNow;
            if (sender is ScrollViewer scroller)
            {
                double newOffset = Math.Max(0, Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset - (e.Delta / 2.5)));
                scroller.ScrollToVerticalOffset(newOffset);
                _currentLyricsScrollOffset = newOffset;
                _targetLyricsScrollOffset = newOffset;
                e.Handled = true;
            }
        }

        private void RenderLyricsList()
        {
            if (LyricsLinesStackPanel == null || LyricsScrollViewer == null) return;
            LyricsLinesStackPanel.Children.Clear();
            _lyricItems.Clear();
            _currentActiveLyricIndex = -1;
            _currentLyricsScrollOffset = 0;
            _targetLyricsScrollOffset = 0;
            LyricsScrollViewer.ScrollToVerticalOffset(0);

            if (LyricsManager.Instance.IsLoading)
            {
                LyricsLoadingPrompt.Visibility = Visibility.Visible;
                LyricsEmptyPrompt.Visibility = Visibility.Collapsed;
                return;
            }

            LyricsLoadingPrompt.Visibility = Visibility.Collapsed;

            if (!LyricsManager.Instance.HasLyrics)
            {
                LyricsEmptyPrompt.Visibility = Visibility.Visible;
                return;
            }

            LyricsEmptyPrompt.Visibility = Visibility.Collapsed;

            var lyrics = LyricsManager.Instance.CurrentLyrics;
            int active = LyricsManager.Instance.ActiveIndex;

            for (int i = 0; i < lyrics.Count; i++)
            {
                var line = lyrics[i];
                int lineIndex = i;
                bool isActive = (i == active);

                var scaleTransform = new ScaleTransform(isActive ? 1.06 : 0.94, isActive ? 1.06 : 0.94);

                var activeStop = new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), isActive ? 1.0 : 0.0);
                var transitionStop = new GradientStop(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), isActive ? 1.0 : 0.10);
                var dimStop = new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0);

                var flowBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                flowBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.0));
                flowBrush.GradientStops.Add(activeStop);
                flowBrush.GradientStops.Add(transitionStop);
                flowBrush.GradientStops.Add(dimStop);

                var textBlock = new TextBlock
                {
                    Text = line.Text,
                    Foreground = isActive ? flowBrush : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    FontSize = 18.5,
                    FontWeight = FontWeights.Bold,
                    Opacity = isActive ? 1.0 : 0.35,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 24,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = scaleTransform
                };

                var card = new Border
                {
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(0, 3, 0, 3),
                    Margin = new Thickness(0, 5, 0, 5),
                    Background = Brushes.Transparent, // Pure seamless Apple Music background - Zero gray box!
                    Cursor = Cursors.Hand,
                    Tag = lineIndex,
                    Child = textBlock
                };

                card.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    await MediaSessionManager.Instance.SeekAsync(line.Timestamp);
                    HighlightActiveLyricLine(lineIndex);
                };

                var item = new LyricCardItem
                {
                    Card = card,
                    TextBlock = textBlock,
                    ScaleTransform = scaleTransform,
                    FlowGradientBrush = flowBrush,
                    ActiveStop = activeStop,
                    TransitionStop = transitionStop,
                    IsActive = isActive
                };

                _lyricItems.Add(item);
                LyricsLinesStackPanel.Children.Add(card);
            }

            if (active >= 0 && active < _lyricItems.Count)
            {
                HighlightActiveLyricLine(active);
            }
        }

        private void HighlightActiveLyricLine(int activeIndex)
        {
            if (_lyricItems.Count == 0 || LyricsScrollViewer == null) return;

            var track = MediaSessionManager.Instance.CurrentTrack;
            var currentPos = track.GetCurrentEstimatedPosition();
            var lyrics = LyricsManager.Instance.CurrentLyrics;

            // 1. Smooth Transition When Active Line Changes (Zero layout rearrangement jank!)
            if (_currentActiveLyricIndex != activeIndex)
            {
                int prevIndex = _currentActiveLyricIndex;
                _currentActiveLyricIndex = activeIndex;

                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

                // Smoothly fade out & scale down previous active line
                if (prevIndex >= 0 && prevIndex < _lyricItems.Count)
                {
                    var prev = _lyricItems[prevIndex];
                    prev.IsActive = false;

                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0.35, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    prev.TextBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                    var scaleDownX = new System.Windows.Media.Animation.DoubleAnimation(0.94, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    var scaleDownY = new System.Windows.Media.Animation.DoubleAnimation(0.94, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    prev.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
                    prev.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);

                    prev.TextBlock.Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
                }

                // Smoothly fade in & scale up new active line
                if (activeIndex >= 0 && activeIndex < _lyricItems.Count)
                {
                    var curr = _lyricItems[activeIndex];
                    curr.IsActive = true;

                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    curr.TextBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                    var scaleUpX = new System.Windows.Media.Animation.DoubleAnimation(1.06, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    var scaleUpY = new System.Windows.Media.Animation.DoubleAnimation(1.06, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    curr.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUpX);
                    curr.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUpY);

                    curr.TextBlock.Foreground = curr.FlowGradientBrush;

                    // Calculate smooth target scroll position
                    double itemTop = 0;
                    for (int k = 0; k < activeIndex; k++)
                    {
                        itemTop += _lyricItems[k].Card.ActualHeight > 0 ? _lyricItems[k].Card.ActualHeight + 10 : 34;
                    }
                    double activeItemH = curr.Card.ActualHeight > 0 ? curr.Card.ActualHeight : 34;
                    double viewportHeight = LyricsScrollViewer.ActualHeight > 0 ? LyricsScrollViewer.ActualHeight : 205;
                    _targetLyricsScrollOffset = Math.Max(0, (itemTop + (activeItemH / 2.0)) - (viewportHeight / 2.0));
                }
            }

            // 2. Real-time Progressive Glow Gradient Sweep on the Active Line
            if (activeIndex >= 0 && activeIndex < _lyricItems.Count && activeIndex < lyrics.Count)
            {
                var activeItem = _lyricItems[activeIndex];
                var syncPos = currentPos + LyricsManager.SyncLatencyOffset;
                var lineStart = lyrics[activeIndex].Timestamp;
                var lineEnd = (activeIndex + 1 < lyrics.Count) ? lyrics[activeIndex + 1].Timestamp : (lineStart + TimeSpan.FromSeconds(4.0));
                double lineDur = (lineEnd - lineStart).TotalSeconds;
                double singingDuration = lineDur > 6.0
                    ? Math.Min(lineDur, Math.Max(2.0, lyrics[activeIndex].Text.Length * 0.12))
                    : Math.Max(1.0, lineDur - 0.25);

                if (singingDuration > 0 && syncPos >= lineStart)
                {
                    double frac = Math.Clamp((syncPos - lineStart).TotalSeconds / singingDuration, 0.0, 1.0);
                    activeItem.ActiveStop.Offset = frac;
                    activeItem.TransitionStop.Offset = Math.Min(1.0, frac + 0.10);
                }
                else
                {
                    activeItem.ActiveStop.Offset = 0.0;
                    activeItem.TransitionStop.Offset = 0.10;
                }
            }
        }

        #endregion

        #region Audio Endpoint Real-Time Detection

        private struct AudioEndpointInfo
        {
            public string Id;
            public string Name;
            public uint FormFactor; // 1 = Speakers, 3 = Headphones, 5 = Headset
            public AudioDeviceCategory Category;
            public string CleanName;
        }

        private static AudioEndpointInfo _cachedActiveAudioEndpoint = new AudioEndpointInfo { Id = "", Name = "", FormFactor = 1, Category = AudioDeviceCategory.InternalSpeakers, CleanName = "Internal Speakers" };

        private AudioEndpointInfo GetCachedAudioEndpoint()
        {
            if (string.IsNullOrEmpty(_cachedActiveAudioEndpoint.Name))
            {
                _cachedActiveAudioEndpoint = GetActiveAudioEndpointDetails();
            }
            return _cachedActiveAudioEndpoint;
        }

        private HashSet<string> _knownActiveEndpointNames = new(StringComparer.OrdinalIgnoreCase);
        private AudioDeviceCategory _lastReportedAudioCategory = AudioDeviceCategory.InternalSpeakers;
        private bool _isAudioEndpointsInitialized = false;

        private void CheckForActiveAudioEndpointChanges()
        {
            try
            {
                var currentActiveList = GetAllActiveAudioEndpoints();
                var currentNonSpeakerNames = currentActiveList
                    .Where(ep => ep.Category != AudioDeviceCategory.InternalSpeakers)
                    .Select(ep => ep.CleanName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!_isAudioEndpointsInitialized)
                {
                    _knownActiveEndpointNames = currentNonSpeakerNames;
                    var initEp = GetActiveAudioEndpointDetails();
                    lastKnownAudioEndpoint = initEp.Name;
                    _lastReportedAudioCategory = initEp.Category;
                    _isAudioEndpointsInitialized = true;
                    return;
                }

                // 1. Check for newly connected audio endpoints (Headphones, TWS, Earbuds, BT speakers)
                foreach (var ep in currentActiveList)
                {
                    if (ep.Category != AudioDeviceCategory.InternalSpeakers && !_knownActiveEndpointNames.Contains(ep.CleanName))
                    {
                        int? battery = GetDeviceBatteryPercent(ep.Name) ?? GetDeviceBatteryPercent(ep.CleanName);
                        _lastReportedAudioCategory = ep.Category;
                        lastKnownAudioEndpoint = ep.Name;
                        Dispatcher.Invoke(() =>
                        {
                            TriggerAudioDeviceHUD(ep.Category, ep.CleanName, battery);
                        });
                    }
                }

                bool hadExternalDevices = _knownActiveEndpointNames.Count > 0;
                bool hasExternalDevicesNow = currentNonSpeakerNames.Count > 0;
                _knownActiveEndpointNames = currentNonSpeakerNames;

                // 2. Check if default audio output changed (including returning to Internal Speakers)
                var defaultEp = GetActiveAudioEndpointDetails();
                _cachedActiveAudioEndpoint = defaultEp;

                if (!string.IsNullOrEmpty(defaultEp.Name) && defaultEp.Name != lastKnownAudioEndpoint)
                {
                    lastKnownAudioEndpoint = defaultEp.Name;
                    var oldCategory = _lastReportedAudioCategory;
                    _lastReportedAudioCategory = defaultEp.Category;

                    Dispatcher.Invoke(() =>
                    {
                        InitWindowsCoreAudio();
                        if (defaultEp.Category == AudioDeviceCategory.InternalSpeakers)
                        {
                            TriggerAudioDeviceHUD(AudioDeviceCategory.InternalSpeakers, "Internal Speakers", null);
                        }
                        else
                        {
                            int? battery = GetDeviceBatteryPercent(defaultEp.Name) ?? GetDeviceBatteryPercent(defaultEp.CleanName);
                            TriggerAudioDeviceHUD(defaultEp.Category, defaultEp.CleanName, battery);
                        }
                    });
                }
                else if (hadExternalDevices && !hasExternalDevicesNow && _lastReportedAudioCategory != AudioDeviceCategory.InternalSpeakers)
                {
                    // External device was disconnected/unplugged, now playing on Internal Speakers
                    _lastReportedAudioCategory = AudioDeviceCategory.InternalSpeakers;
                    lastKnownAudioEndpoint = defaultEp.Name;
                    Dispatcher.Invoke(() =>
                    {
                        InitWindowsCoreAudio();
                        TriggerAudioDeviceHUD(AudioDeviceCategory.InternalSpeakers, "Internal Speakers", null);
                    });
                }
            }
            catch { }
        }

        private void AudioEndpointWatcherTimer_Tick(object? sender, EventArgs e)
        {
            Task.Run(async () =>
            {
                CheckForActiveAudioEndpointChanges();
                await BluetoothBatteryManager.Instance.RefreshDevicesAsync();
            });
        }

        private AudioEndpointInfo GetActiveAudioEndpointDetails()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? dev = null;
            IPropertyStore? store = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                enumerator.GetDefaultAudioEndpoint(0, 1, out dev);
                if (dev != null)
                {
                    string id = "";
                    try { dev.GetId(out id); } catch { }

                    dev.OpenPropertyStore(0, out store);
                    if (store != null)
                    {
                        string name = "";
                        uint formFactor = 1;

                        // 1. Read Name (PKEY_Device_FriendlyName: {a45c254e-df1c-4efd-8020-67d146a850e0}, 14)
                        var keyName = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                        var propName = new PROPVARIANT();
                        try
                        {
                            store.GetValue(ref keyName, out propName);
                            if (propName.pwszVal != IntPtr.Zero)
                            {
                                name = Marshal.PtrToStringUni(propName.pwszVal) ?? "";
                            }
                        }
                        finally
                        {
                            NativeMethods.PropVariantClear(ref propName);
                        }

                        // 2. Read FormFactor (PKEY_AudioEndpoint_FormFactor: {1da5d803-d01f-4565-99e8-b7385e2b0c14}, 0)
                        var keyForm = new PROPERTYKEY { fmtid = new Guid("1da5d803-d01f-4565-99e8-b7385e2b0c14"), pid = 0 };
                        var propForm = new PROPVARIANT();
                        try
                        {
                            store.GetValue(ref keyForm, out propForm);
                            formFactor = propForm.uintVal;
                        }
                        finally
                        {
                            NativeMethods.PropVariantClear(ref propForm);
                        }

                        var category = ClassifyAudioDevice(name, formFactor);
                        var clean = CleanDeviceName(name, category);

                        return new AudioEndpointInfo
                        {
                            Id = id,
                            Name = name,
                            FormFactor = formFactor,
                            Category = category,
                            CleanName = clean
                        };
                    }
                }
            }
            catch { }
            finally
            {
                if (store != null) try { Marshal.ReleaseComObject(store); } catch { }
                if (dev != null) try { Marshal.ReleaseComObject(dev); } catch { }
                if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
            }
            return new AudioEndpointInfo { Id = "", Name = "", FormFactor = 1, Category = AudioDeviceCategory.InternalSpeakers, CleanName = "Internal Speakers" };
        }

        private List<AudioEndpointInfo> GetAllActiveAudioEndpoints()
        {
            var list = new List<AudioEndpointInfo>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // Query all active playback audio endpoints
                int hr = enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out collection);
                if (hr == 0 && collection != null)
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        int itemHr = collection.Item(i, out IMMDevice dev);
                        if (itemHr != 0 || dev == null) continue;

                        string id = "";
                        try { dev.GetId(out id); } catch { }
                        if (string.IsNullOrEmpty(id) || seenIds.Contains(id))
                        {
                            try { Marshal.ReleaseComObject(dev); } catch { }
                            continue;
                        }
                        seenIds.Add(id);

                        dev.OpenPropertyStore(0, out IPropertyStore store);
                        string name = "";
                        uint formFactor = 1;
                        if (store != null)
                        {
                            var keyName = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                            var propName = new PROPVARIANT();
                            try
                            {
                                store.GetValue(ref keyName, out propName);
                                if (propName.pwszVal != IntPtr.Zero)
                                    name = Marshal.PtrToStringUni(propName.pwszVal) ?? "";
                            }
                            finally { NativeMethods.PropVariantClear(ref propName); }

                            var keyForm = new PROPERTYKEY { fmtid = new Guid("1da5d803-d01f-4565-99e8-b7385e2b0c14"), pid = 0 };
                            var propForm = new PROPVARIANT();
                            try
                            {
                                store.GetValue(ref keyForm, out propForm);
                                formFactor = propForm.uintVal;
                            }
                            finally { NativeMethods.PropVariantClear(ref propForm); }
                            try { Marshal.ReleaseComObject(store); } catch { }
                        }

                        var category = ClassifyAudioDevice(name, formFactor);
                        var clean = CleanDeviceName(name, category);
                        list.Add(new AudioEndpointInfo
                        {
                            Id = id,
                            Name = name,
                            FormFactor = formFactor,
                            Category = category,
                            CleanName = clean
                        });
                        try { Marshal.ReleaseComObject(dev); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                if (collection != null) try { Marshal.ReleaseComObject(collection); } catch { }
                if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
            }

            // Ensure Internal Speakers is present
            if (!list.Any(ep => ep.Category == AudioDeviceCategory.InternalSpeakers))
            {
                list.Insert(0, new AudioEndpointInfo
                {
                    Id = "",
                    Name = "Internal Speakers",
                    FormFactor = 1,
                    Category = AudioDeviceCategory.InternalSpeakers,
                    CleanName = "Internal Speakers"
                });
            }

            return list;
        }

        public void SetDefaultAudioDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            try
            {
                var policyConfig = new PolicyConfigClient() as IPolicyConfig;
                if (policyConfig != null)
                {
                    policyConfig.SetDefaultEndpoint(deviceId, 0); // eConsole
                    policyConfig.SetDefaultEndpoint(deviceId, 1); // eMultimedia
                    policyConfig.SetDefaultEndpoint(deviceId, 2); // eCommunications
                }

                InitWindowsCoreAudio();
                _cachedActiveAudioEndpoint = GetActiveAudioEndpointDetails();
                UpdateIndicatorVisuals();
            }
            catch { }
        }

        private bool _isAudioOutputDropdownOpen = false;

        private void BtnToggleOutputSelector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _isAudioOutputDropdownOpen = !_isAudioOutputDropdownOpen;
            UpdateIndicatorVisuals();
        }

        private void PopulateAudioOutputDevicesList(AudioEndpointInfo currentActive)
        {
            AudioOutputDevicesList.Children.Clear();
            var endpoints = GetAllActiveAudioEndpoints();

            TxtSelectedOutputName.Text = currentActive.CleanName;
            if (currentActive.Category == AudioDeviceCategory.TwsEarbuds)
            {
                IconSelectedOutput.Data = (Geometry)FindResource("IconTwsEarbuds");
            }
            else if (currentActive.Category == AudioDeviceCategory.WirelessHeadphones || currentActive.Category == AudioDeviceCategory.WiredHeadphones)
            {
                IconSelectedOutput.Data = (Geometry)FindResource("IconHeadphones");
            }
            else
            {
                IconSelectedOutput.Data = (Geometry)FindResource("IconSpeakerMedium");
            }

            foreach (var ep in endpoints)
            {
                bool isActive = (!string.IsNullOrEmpty(currentActive.Id) && ep.Id == currentActive.Id) ||
                                (string.Equals(ep.Name, currentActive.Name, StringComparison.OrdinalIgnoreCase)) ||
                                (string.Equals(ep.CleanName, currentActive.CleanName, StringComparison.OrdinalIgnoreCase));

                var border = new Border
                {
                    Height = 34,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(isActive ? Color.FromArgb(0x35, 0x0A, 0x84, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(isActive ? Color.FromRgb(0x0A, 0x84, 0xFF) : Colors.Transparent),
                    BorderThickness = new Thickness(isActive ? 1 : 0),
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand,
                    Tag = ep.Id
                };

                var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Left: Icon
                var path = new System.Windows.Shapes.Path
                {
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                if (ep.Category == AudioDeviceCategory.TwsEarbuds)
                {
                    path.Data = (Geometry)FindResource("IconTwsEarbuds");
                    path.Stroke = new SolidColorBrush(isActive ? Color.FromRgb(0x0A, 0x84, 0xFF) : Colors.White);
                    path.StrokeThickness = 1.4;
                }
                else if (ep.Category == AudioDeviceCategory.WirelessHeadphones || ep.Category == AudioDeviceCategory.WiredHeadphones)
                {
                    path.Data = (Geometry)FindResource("IconHeadphones");
                    path.Fill = new SolidColorBrush(isActive ? Color.FromRgb(0x0A, 0x84, 0xFF) : Colors.White);
                }
                else
                {
                    path.Data = (Geometry)FindResource("IconSpeakerMedium");
                    path.Fill = new SolidColorBrush(isActive ? Color.FromRgb(0x0A, 0x84, 0xFF) : Colors.White);
                }
                Grid.SetColumn(path, 0);
                grid.Children.Add(path);

                // Center: Name
                var txt = new TextBlock
                {
                    Text = ep.CleanName,
                    Foreground = new SolidColorBrush(isActive ? Color.FromRgb(0x0A, 0x84, 0xFF) : Colors.White),
                    FontSize = 12,
                    FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(txt, 1);
                grid.Children.Add(txt);

                // Right: Checkmark
                if (isActive)
                {
                    var check = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconCheckmark"),
                        Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xE4, 0x6C)),
                        Width = 10,
                        Height = 10,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(check, 2);
                    grid.Children.Add(check);
                }

                border.Child = grid;
                string devId = ep.Id;
                border.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;
                    _isAudioOutputDropdownOpen = false;
                    if (!string.IsNullOrEmpty(devId))
                    {
                        SetDefaultAudioDevice(devId);
                    }
                };

                AudioOutputDevicesList.Children.Add(border);
            }
        }

        private void BtnDisconnectCurrentBt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var current = GetCachedAudioEndpoint();

                // 1. Disconnect Bluetooth at radio level if it's a wireless device
                if (current.Category == AudioDeviceCategory.TwsEarbuds || current.Category == AudioDeviceCategory.WirelessHeadphones)
                {
                    string targetName = current.CleanName;
                    string rawName = current.Name;
                    Task.Run(() =>
                    {
                        BluetoothDisconnectHelper.DisconnectDevice(targetName);
                        BluetoothDisconnectHelper.DisconnectDevice(rawName);
                    });
                }

                // 2. Switch default audio endpoint to Internal Speakers
                var endpoints = GetAllActiveAudioEndpoints();
                var speaker = endpoints.FirstOrDefault(ep => ep.Category == AudioDeviceCategory.InternalSpeakers);
                if (!string.IsNullOrEmpty(speaker.Id))
                {
                    SetDefaultAudioDevice(speaker.Id);
                }
                else
                {
                    var fallback = endpoints.FirstOrDefault(ep => ep.Category != AudioDeviceCategory.TwsEarbuds && ep.Category != AudioDeviceCategory.WirelessHeadphones);
                    if (!string.IsNullOrEmpty(fallback.Id))
                    {
                        SetDefaultAudioDevice(fallback.Id);
                    }
                }

                _isAudioOutputDropdownOpen = false;
                TriggerAudioDeviceHUD(AudioDeviceCategory.InternalSpeakers, "Internal Speakers", null);
            }
            catch { }
        }

        private string GetActiveAudioEndpointName()
        {
            return GetActiveAudioEndpointDetails().Name;
        }

        private AudioDeviceCategory ClassifyAudioDevice(string name, uint formFactor = 1)
        {
            string lower = name.ToLowerInvariant();

            // Extract the inside candidate name if wrapped in parentheses e.g. "Headphones (Rockerz 550)" -> "rockerz 550"
            var match = Regex.Match(lower, @"\(([^)]+)\)");
            string inner = match.Success ? match.Groups[1].Value.Trim() : lower;

            // 1. TWS Earbuds / AirPods / In-Ear / Earphones
            if (inner.Contains("airdopes") || inner.Contains("airpod") || inner.Contains("earbud") || 
                inner.Contains("buds") || inner.Contains("tws") || inner.Contains("in-ear") || 
                inner.Contains("dots") || inner.Contains("atom") || inner.Contains("nirvana") || 
                inner.Contains("enco") || inner.Contains("duopods") || inner.Contains("truke") || 
                inner.Contains("boult") || inner.Contains("noise") || inner.Contains("realme") || 
                inner.Contains("boat ear") || inner.Contains("earphone") || inner.Contains("ear (") || 
                inner.Contains("freebuds") || inner.Contains("galaxy buds") || inner.Contains("soundcore") ||
                inner.Contains("alpha"))
            {
                return AudioDeviceCategory.TwsEarbuds;
            }

            // 2. Bluetooth / Wireless Headphones
            if (inner.Contains("rockerz") || inner.Contains("bluetooth") || inner.Contains("wireless") || 
                inner.Contains("hands-free") || inner.Contains("avrcp") || inner.Contains("a2dp") || 
                inner.Contains("sony") || inner.Contains("bose") || inner.Contains("jbl") || 
                inner.Contains("sennheiser") || inner.Contains("skullcandy") || inner.Contains("boat") ||
                inner.Contains("wh-") || inner.Contains("ch-") || inner.Contains("tune") ||
                inner.Contains("marshall") || inner.Contains("beats") || inner.Contains("anker") ||
                inner.Contains("vivo") || inner.Contains("iqoo") || inner.Contains("redmi") ||
                inner.Contains("mw-1901") || inner.Contains("zip"))
            {
                if (inner.Contains("speaker") || inner.Contains("soundbar") || inner.Contains("flip") || inner.Contains("charge"))
                    return AudioDeviceCategory.InternalSpeakers;
                return AudioDeviceCategory.WirelessHeadphones;
            }

            // Check if device name exists in Windows Bluetooth PnP devices
            if (BluetoothBatteryManager.Instance.ConnectedDevices.Any(d => d.CleanName.Contains(inner, StringComparison.OrdinalIgnoreCase) || inner.Contains(d.CleanName, StringComparison.OrdinalIgnoreCase)))
            {
                return AudioDeviceCategory.WirelessHeadphones;
            }

            // 3. Wired 3.5mm Headphone Jack (ONLY if explicitly named Headphone/Headset or non-speaker formFactor 3/5)
            if (!lower.Contains("speaker") && (lower.StartsWith("headphone") || lower.StartsWith("headset") || formFactor == 3 || formFactor == 5))
            {
                return AudioDeviceCategory.WiredHeadphones;
            }

            // 4. Default / Built-in Speakers ("Speaker (Realtek(R) Audio)", "Speakers", etc.)
            return AudioDeviceCategory.InternalSpeakers;
        }

        private string CleanDeviceName(string raw, AudioDeviceCategory category)
        {
            if (category == AudioDeviceCategory.InternalSpeakers)
            {
                return "Internal Speakers";
            }

            if (category == AudioDeviceCategory.WiredHeadphones)
            {
                return "Headphones";
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return category switch
                {
                    AudioDeviceCategory.TwsEarbuds => "TWS Earbuds",
                    AudioDeviceCategory.WirelessHeadphones => "Wireless Headphones",
                    _ => "Internal Speakers"
                };
            }

            // 1. Extract device name inside parentheses if present e.g. "Headphones (Airdopes ATOM 81)" -> "Airdopes ATOM 81"
            var match = Regex.Match(raw, @"\(([^)]+)\)");
            string candidate = match.Success ? match.Groups[1].Value.Trim() : raw;

            if (candidate.Contains("Realtek", StringComparison.OrdinalIgnoreCase) || candidate.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.StartsWith("Headphone", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("Headset", StringComparison.OrdinalIgnoreCase))
                {
                    return "Headphones";
                }
                return "Internal Speakers";
            }

            // Remove driver prefixes like "2- Airdopes", "Realtek(R) Audio", "Avrcp Transport", etc.
            candidate = Regex.Replace(candidate, @"^\d+-\s*", "");
            candidate = Regex.Replace(candidate, @"\s*\([^)]*(Hands-Free|Avrcp|A2DP|Stereo|Audio|Driver)[^)]*\)", "", RegexOptions.IgnoreCase);
            candidate = candidate.Replace("Avrcp Transport", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("A2DP SNK", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free AG Audio", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free AG", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free HF Audio", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free HF", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Stereo", "", StringComparison.OrdinalIgnoreCase)
                                 .Trim();

            return string.IsNullOrWhiteSpace(candidate) ? (category == AudioDeviceCategory.TwsEarbuds ? "TWS Earbuds" : "Bluetooth Audio") : candidate;
        }



        private int? GetDeviceBatteryPercent(string deviceName)
        {
            try
            {
                int? bat = BluetoothBatteryManager.Instance.GetBatteryForDevice(deviceName);
                if (bat.HasValue) return bat;

                var primary = BluetoothBatteryManager.Instance.PrimaryConnectedDevice;
                if (primary != null && primary.BatteryLevel.HasValue)
                {
                    return primary.BatteryLevel.Value;
                }
            }
            catch { }
            return null;
        }

        private void HideAllHudViews()
        {
            StealthView.Visibility = Visibility.Collapsed;
            VolumeHudView.Visibility = Visibility.Collapsed;
            BrightnessHudView.Visibility = Visibility.Collapsed;
            DndHudView.Visibility = Visibility.Collapsed;
            BluetoothHudView.Visibility = Visibility.Collapsed;
            AirDropHudView.Visibility = Visibility.Collapsed;
            AirDropIncomingHudView.Visibility = Visibility.Collapsed;
            AirDropPickerView.Visibility = Visibility.Collapsed;
            ScreenMirroringHudView.Visibility = Visibility.Collapsed;
            ClipboardHudView.Visibility = Visibility.Collapsed;
            CapsLockHudView.Visibility = Visibility.Collapsed;
            IncomingCallHudView.Visibility = Visibility.Collapsed;
            UniversalExpandedContainer.Visibility = Visibility.Collapsed;
        }

        private void HideAllExpandedTabBodies()
        {
            if (ViewShelfBody != null) ViewShelfBody.Visibility = Visibility.Collapsed;
            if (ViewMusicBody != null) ViewMusicBody.Visibility = Visibility.Collapsed;
            if (ViewTimerBody != null) ViewTimerBody.Visibility = Visibility.Collapsed;
            if (ViewBluetoothBody != null) ViewBluetoothBody.Visibility = Visibility.Collapsed;
            if (ViewNetworkBody != null) ViewNetworkBody.Visibility = Visibility.Collapsed;
            if (ViewScreenMirroringBody != null) ViewScreenMirroringBody.Visibility = Visibility.Collapsed;
            if (ViewClipboardBody != null) ViewClipboardBody.Visibility = Visibility.Collapsed;
            if (ViewCallExpandedBody != null) ViewCallExpandedBody.Visibility = Visibility.Collapsed;
        }

        private CancellationTokenSource? _clipboardHudCts;

        public void TriggerClipboardHUD(string title, string subtitle, bool isScreenshot)
        {
            // Do not interrupt expanded container or incoming AirDrop
            if (isExpanded || isAirDropHudActive) return;

            _clipboardHudCts?.Cancel();
            _clipboardHudCts = new CancellationTokenSource();
            var token = _clipboardHudCts.Token;

            Dispatcher.Invoke(() =>
            {
                isClipboardHudActive = true;
                isVolumeHudActive = false;
                isBrightnessHudActive = false;
                isDndHudActive = false;
                isBluetoothHudActive = false;
                isAirDropHudActive = false;
                isCapsLockHudActive = false;

                HideAllHudViews();
                IdleFaceContainer.Visibility = Visibility.Collapsed;
                PrivacyDotLeftContainer.Visibility = Visibility.Collapsed;
                PrivacyDotRightContainer.Visibility = Visibility.Collapsed;
                StealthView.Visibility = Visibility.Collapsed;

                ClipboardHudView.Visibility = Visibility.Visible;
                ClipboardHudIcon.Data = (Geometry)FindResource(isScreenshot ? "IconScreenShareLeft" : "IconClipboard");

                ClipboardHudProgressContainer.Visibility = Visibility.Visible;
                ClipboardHudCompletedContainer.Visibility = Visibility.Collapsed;
                DrawClipboardHudProgress(0.05);

                double targetW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                double targetH = currentMode == ShapeDisplayMode.Notch ? 34 : 30;
                AnimateSize(targetW, targetH);
            });

            Task.Run(async () =>
            {
                // Smooth Progress Arc Fill from 0.05 to 1.0 (20 steps of 25ms = 500ms smooth fill)
                for (int i = 1; i <= 20; i++)
                {
                    if (token.IsCancellationRequested) return;
                    await Task.Delay(25);
                    double progress = i / 20.0;
                    Dispatcher.Invoke(() =>
                    {
                        DrawClipboardHudProgress(progress);
                    });
                }

                if (token.IsCancellationRequested) return;

                // Brief pause at full circle so the full complete ring is clearly seen
                await Task.Delay(100);
                if (token.IsCancellationRequested) return;

                // Switch to Completed Green Checkmark ✓ (matching media_1788011751371.png)
                Dispatcher.Invoke(() =>
                {
                    ClipboardHudProgressContainer.Visibility = Visibility.Collapsed;
                    ClipboardHudCompletedContainer.Visibility = Visibility.Visible;
                });

                await Task.Delay(1600);
                if (token.IsCancellationRequested) return;

                // Return to idle / compact
                Dispatcher.Invoke(() =>
                {
                    isClipboardHudActive = false;
                    ClipboardHudView.Visibility = Visibility.Collapsed;
                    UpdateIndicatorVisuals();
                });
            });
        }

        private void DrawClipboardHudProgress(double progress)
        {
            double size = 22.0;
            double thickness = 2.2;
            double radius = (size - thickness) / 2.0;
            double cx = 11.0, cy = 11.0;

            progress = Math.Clamp(progress, 0.001, 0.999);
            double angle = progress * 360.0;
            double rad = (angle - 90) * Math.PI / 180.0;

            double startX = cx;
            double startY = cy - radius;
            double endX = cx + radius * Math.Cos(rad);
            double endY = cy + radius * Math.Sin(rad);

            bool isLargeArc = angle > 180.0;

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });
            geometry.Figures.Add(figure);
            ClipboardHudProgressArc.Data = geometry;
        }

        public void TriggerCapsLockHUD(bool isCapsOn)
        {
            if (isExpanded || isAirDropHudActive) return;

            isCapsLockHudActive = true;
            isVolumeHudActive = false;
            isBrightnessHudActive = false;
            isDndHudActive = false;
            isBluetoothHudActive = false;
            isAirDropHudActive = false;
            isClipboardHudActive = false;

            // Authentic Clean Apple MacBook Caps Lock UI (No LED dot)
            if (isCapsOn)
            {
                TxtCapsLockStatus.Text = "On";
                TxtCapsLockStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                CapsLockKeycapSurface.BorderBrush = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));
                CapsLockArrowIcon.Fill = new SolidColorBrush(Colors.White);
            }
            else
            {
                TxtCapsLockStatus.Text = "Off";
                TxtCapsLockStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
                CapsLockKeycapSurface.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
                CapsLockArrowIcon.Fill = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            }

            HideAllHudViews();
            CapsLockHudView.Visibility = Visibility.Visible;
            CapsLockHudView.Margin = currentMode == ShapeDisplayMode.Notch ? new Thickness(16, 0, 16, 0) : new Thickness(14, 0, 14, 0);

            double targetW = currentMode == ShapeDisplayMode.Notch ? 210.0 : 195.0;
            double targetH = currentMode == ShapeDisplayMode.Notch ? notchHeight : 34.0;

            AnimateSize(targetW, targetH);

            capsLockAutoHideTimer.Stop();
            capsLockAutoHideTimer.Start();
        }

        private void CapsLockAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            capsLockAutoHideTimer.Stop();
            isCapsLockHudActive = false;

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();
        }

        private void TriggerCheckmarkWithConnectingArc()
        {
            BatteryGaugeContainer.Visibility = Visibility.Collapsed;
            CheckmarkContainer.Visibility = Visibility.Visible;
            BtCheckmarkIcon.Visibility = Visibility.Collapsed;
            btConnectingProgress = 0.05;
            DrawBtConnectingArc(btConnectingProgress);
            btConnectingArcTimer.Stop();
            btConnectingArcTimer.Start();
        }

        private void DrawBtConnectingArc(double progress)
        {
            double size = 24.0;
            double thickness = 2.4;
            double radius = (size - thickness) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            if (progress <= 0)
            {
                BtConnectingArc.Data = null;
                return;
            }
            if (progress >= 0.999)
            {
                BtConnectingArc.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                return;
            }

            double angle = progress * 360.0;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            bool isLargeArc = angle > 180.0;
            double endX = cx + radius * Math.Cos(radians);
            double endY = cy + radius * Math.Sin(radians);

            var figure = new PathFigure { StartPoint = new Point(cx, cy - radius), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment { Point = new Point(endX, endY), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise, IsLargeArc = isLargeArc });
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            BtConnectingArc.Data = geo;
        }

        private string _lastTriggeredDeviceName = "";
        private DateTime _lastTriggeredTime = DateTime.MinValue;

        public void TriggerAudioDeviceHUD(AudioDeviceCategory category, string deviceName, int? batteryLevel = null)
        {
            // Debounce any duplicate HUD popup triggered within 1500ms
            var now = DateTime.UtcNow;
            if ((now - _lastTriggeredTime).TotalMilliseconds < 1500 &&
                (string.Equals(_lastTriggeredDeviceName, deviceName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(deviceName, "Headphones", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(deviceName, "Internal Speakers", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _lastTriggeredDeviceName = deviceName;
            _lastTriggeredTime = now;

            isBluetoothHudActive = true;
            isVolumeHudActive = false;
            isBrightnessHudActive = false;
            isDndHudActive = false;
            isAirDropHudActive = false;
            isExpanded = false;

            HideAllHudViews();
            BluetoothHudView.Visibility = Visibility.Visible;
            BluetoothHudView.Margin = currentMode == ShapeDisplayMode.Notch ? new Thickness(22, 6, 22, 6) : new Thickness(16, 0, 16, 0);

            string cleanName = CleanDeviceName(deviceName, category);
            int? actualBattery = batteryLevel ?? GetDeviceBatteryPercent(deviceName);

            switch (category)
            {
                case AudioDeviceCategory.TwsEarbuds:
                    DeviceIconImage.Visibility = Visibility.Collapsed;
                    DeviceIconVector.Visibility = Visibility.Visible;
                    DeviceIconVector.Data = (Geometry)FindResource("IconTwsEarbuds");
                    DeviceIconVector.Fill = null;
                    DeviceIconVector.Stroke = new SolidColorBrush(Colors.White);
                    DeviceIconVector.StrokeThickness = 1.8;
                    TxtBtSubtitle.Text = "TWS Connected";
                    TxtBtDeviceName.Text = string.IsNullOrWhiteSpace(cleanName) ? "TWS Earbuds" : cleanName;
                    if (actualBattery.HasValue)
                    {
                        BatteryGaugeContainer.Visibility = Visibility.Visible;
                        CheckmarkContainer.Visibility = Visibility.Collapsed;
                        SetupBatteryGauge(actualBattery.Value);
                    }
                    else
                    {
                        TriggerCheckmarkWithConnectingArc();
                    }
                    break;

                case AudioDeviceCategory.WirelessHeadphones:
                    DeviceIconImage.Visibility = Visibility.Collapsed;
                    DeviceIconVector.Visibility = Visibility.Visible;
                    DeviceIconVector.Data = (Geometry)FindResource("IconHeadphones");
                    DeviceIconVector.Fill = new SolidColorBrush(Colors.White);
                    DeviceIconVector.Stroke = null;
                    TxtBtSubtitle.Text = "Connected";
                    TxtBtDeviceName.Text = string.IsNullOrWhiteSpace(cleanName) ? "Wireless Headphones" : cleanName;
                    if (actualBattery.HasValue)
                    {
                        BatteryGaugeContainer.Visibility = Visibility.Visible;
                        CheckmarkContainer.Visibility = Visibility.Collapsed;
                        SetupBatteryGauge(actualBattery.Value);
                    }
                    else
                    {
                        TriggerCheckmarkWithConnectingArc();
                    }
                    break;

                case AudioDeviceCategory.WiredHeadphones:
                    DeviceIconImage.Visibility = Visibility.Collapsed;
                    DeviceIconVector.Visibility = Visibility.Visible;
                    DeviceIconVector.Data = (Geometry)FindResource("IconHeadphones");
                    DeviceIconVector.Fill = new SolidColorBrush(Colors.White);
                    DeviceIconVector.Stroke = null;
                    TxtBtSubtitle.Text = "Headphones Connected";
                    TxtBtDeviceName.Text = "Headphones";
                    BatteryGaugeContainer.Visibility = Visibility.Collapsed;
                    CheckmarkContainer.Visibility = Visibility.Visible;
                    TriggerCheckmarkWithConnectingArc();
                    break;

                case AudioDeviceCategory.InternalSpeakers:
                default:
                    DeviceIconVector.Visibility = Visibility.Collapsed;
                    DeviceIconImage.Visibility = Visibility.Visible;
                    DeviceIconImage.Source = imgSpeaker;
                    TxtBtSubtitle.Text = "Connected";
                    TxtBtDeviceName.Text = "Internal Speakers";
                    BatteryGaugeContainer.Visibility = Visibility.Collapsed;
                    CheckmarkContainer.Visibility = Visibility.Visible;
                    TriggerCheckmarkWithConnectingArc();
                    break;
            }

            AnimateSpringDeviceIcon();

            // Compact Apple Dynamic Island HUD sizing (275px width x 44px/42px height - centered!)
            double targetW = currentMode == ShapeDisplayMode.Notch ? 280.0 : 270.0;
            double targetH = currentMode == ShapeDisplayMode.Notch ? 46.0 : 42.0;
            AnimateSize(targetW, targetH);

            bluetoothAutoHideTimer.Stop();
            bluetoothAutoHideTimer.Start();
        }

        private void SetupBatteryGauge(int batteryLevel)
        {
            int clamped = Math.Clamp(batteryLevel, 0, 100);
            TxtBtBatteryPercent.Text = $"{clamped}";

            Color tintColor = clamped switch
            {
                < 20 => Color.FromRgb(255, 59, 48),    // Apple Red (#FF3B30)
                < 50 => Color.FromRgb(255, 214, 10),  // Apple Yellow (#FFD60A)
                _ => Color.FromRgb(0, 228, 108)       // Figma Neon Green (#00E46C)
            };
            var tintBrush = new SolidColorBrush(tintColor);
            TxtBtBatteryPercent.Foreground = tintBrush;
            BtBatteryArc.Stroke = tintBrush;

            DrawBatteryCircleArc(clamped / 100.0);
        }

        private void DrawBatteryCircleArc(double progress)
        {
            double size = 24.0;
            double thickness = 2.4;
            double radius = (size - thickness) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            if (progress <= 0)
            {
                BtBatteryArc.Data = null;
                return;
            }

            if (progress >= 0.999)
            {
                BtBatteryArc.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                return;
            }

            double angle = progress * 360.0;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            double endX = cx + radius * Math.Cos(radians);
            double endY = cy + radius * Math.Sin(radians);

            bool isLargeArc = angle > 180.0;

            var figure = new PathFigure
            {
                StartPoint = new Point(cx, cy - radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            BtBatteryArc.Data = geo;
        }

        private void DrawExpBatteryCircleArc(double progress)
        {
            double size = 40.0;
            double thickness = 3.8;
            double radius = (size - thickness) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            if (progress <= 0)
            {
                ExpBtBatteryArc.Data = null;
                return;
            }

            if (progress >= 0.999)
            {
                ExpBtBatteryArc.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                return;
            }

            double angle = progress * 360.0;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            double endX = cx + radius * Math.Cos(radians);
            double endY = cy + radius * Math.Sin(radians);

            bool isLargeArc = angle > 180.0;

            var figure = new PathFigure
            {
                StartPoint = new Point(cx, cy - radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            ExpBtBatteryArc.Data = geo;
        }

        private void AnimateSpringDeviceIcon()
        {
            var springScale = new DoubleAnimationUsingKeyFrames();
            springScale.KeyFrames.Add(new SplineDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            springScale.KeyFrames.Add(new SplineDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new KeySpline(0.2, 0.8, 0.4, 1.0)));
            springScale.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            springScale.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));

            BtIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScale);
            BtIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScale);
        }

        private void BluetoothAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            bluetoothAutoHideTimer.Stop();
            isBluetoothHudActive = false;

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();
        }

        #endregion

        #region Windows 11 Pure Display Brightness Listener & Engine

        private ManagementEventWatcher? _brightnessEventWatcher;

        private void InitBrightnessWatcher()
        {
            Task.Run(() =>
            {
                try
                {
                    var scope = new ManagementScope(@"root\wmi");
                    scope.Connect();
                    var query = new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
                    _brightnessEventWatcher = new ManagementEventWatcher(scope, query);
                    _brightnessEventWatcher.EventArrived += (s, e) =>
                    {
                        try
                        {
                            int b = Convert.ToInt32(e.NewEvent["Brightness"]);
                            if (b != lastKnownBrightness && b >= 0)
                            {
                                lastKnownBrightness = b;
                                Dispatcher.Invoke(() => TriggerBrightnessHUD(b));
                            }
                        }
                        catch { }
                    };
                    _brightnessEventWatcher.Start();

                    // Read initial brightness value once (without polling)
                    int initB = GetWindowsBrightness();
                    if (initB >= 0)
                    {
                        lastKnownBrightness = initB;
                        displayedBrightnessLevel = initB;
                        isInitialBrightnessLoaded = true;
                    }
                }
                catch
                {
                    // WMI watcher failed (external DDC/CI monitor?) — fall back to slow 5s poller
                    Dispatcher.Invoke(() => brightnessPollTimer.Start());
                }
            });
        }

        private void BrightnessPollTimer_Tick(object? sender, EventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    int currentBrightness = GetWindowsBrightness();
                    if (currentBrightness < 0) return;

                    if (!isInitialBrightnessLoaded)
                    {
                        lastKnownBrightness = currentBrightness;
                        displayedBrightnessLevel = currentBrightness;
                        isInitialBrightnessLoaded = true;
                        return;
                    }

                    if (currentBrightness != lastKnownBrightness)
                    {
                        lastKnownBrightness = currentBrightness;
                        Dispatcher.Invoke(() => TriggerBrightnessHUD(currentBrightness));
                    }
                }
                catch { }
            });
        }

        private int GetWindowsBrightness()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject obj in searcher.Get())
                {
                    return Convert.ToInt32(obj["CurrentBrightness"]);
                }
            }
            catch { }

            // Fallback for external monitors via DDC/CI
            try
            {
                if (TryGetDdcCiBrightness(out int ddcB))
                {
                    return ddcB;
                }
            }
            catch { }

            return -1;
        }

        public void TriggerBrightnessHUD(int level)
        {
            isBrightnessHudActive = true;
            isVolumeHudActive = false;
            isDndHudActive = false;
            isBluetoothHudActive = false;
            isAirDropHudActive = false;
            isExpanded = false;

            int clamped = Math.Clamp(level, 0, 100);

            if (clamped < 33)
                BrightnessSunIcon.Data = (Geometry)FindResource("IconSunLow");
            else if (clamped < 66)
                BrightnessSunIcon.Data = (Geometry)FindResource("IconSunMedium");
            else
                BrightnessSunIcon.Data = (Geometry)FindResource("IconSunHigh");

            double fillWidth = Math.Max(0, (clamped / 100.0) * 52.0);
            BrightnessProgressFill.Width = fillWidth;

            bool isGoingUp = clamped >= displayedBrightnessLevel;
            Animate3DFlipBrightness($"{clamped}", isGoingUp);
            displayedBrightnessLevel = clamped;

            HideAllHudViews();
            BrightnessHudView.Visibility = Visibility.Visible;

            double targetW = currentMode == ShapeDisplayMode.Notch ? 280 : 260;
            double targetH = currentMode == ShapeDisplayMode.Notch ? notchHeight : islandHeight;

            if (ShapeRoot.Width != targetW || ShapeRoot.Height != targetH)
            {
                AnimateSize(targetW, targetH);
            }

            brightnessAutoHideTimer.Stop();
            brightnessAutoHideTimer.Start();
        }

        private void Animate3DFlipBrightness(string newText, bool isGoingUp)
        {
            if (TxtBrightnessNew.Text == newText) return;

            string oldText = TxtBrightnessNew.Text;
            TxtBrightnessOld.Text = oldText;
            TxtBrightnessNew.Text = newText;

            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var halfDuration = TimeSpan.FromMilliseconds(65);

            double oldTargetY = isGoingUp ? -8 : 8;
            double newStartY = isGoingUp ? 8 : -8;

            ScaleBrightnessOld.ScaleY = 1.0;
            TransBrightnessOld.Y = 0;
            TxtBrightnessOld.Opacity = 1.0;

            ScaleBrightnessOld.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.0, halfDuration) { EasingFunction = easeIn });
            TransBrightnessOld.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oldTargetY, halfDuration) { EasingFunction = easeIn });
            TxtBrightnessOld.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, halfDuration) { EasingFunction = easeIn });

            ScaleBrightnessNew.ScaleY = 0.0;
            TransBrightnessNew.Y = newStartY;
            TxtBrightnessNew.Opacity = 0.0;

            ScaleBrightnessNew.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
            TransBrightnessNew.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
            TxtBrightnessNew.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
        }

        private void BrightnessAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (isDraggingBrightness) return;
            brightnessAutoHideTimer.Stop();
            isBrightnessHudActive = false;

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();
        }

        private bool isDraggingBrightness = false;

        public static void SetWindowsBrightness(int brightnessPercent)
        {
            int target = Math.Clamp(brightnessPercent, 0, 100);
            Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)target });
                    }
                }
                catch { }

                try
                {
                    SetDdcCiBrightness((uint)target);
                }
                catch { }
            });
        }

        private void BrightnessSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingBrightness = true;
            (sender as UIElement)?.CaptureMouse();
            brightnessAutoHideTimer.Stop();
            HandleBrightnessSliderMove(e.GetPosition(BrightnessSliderTrack).X);
            e.Handled = true;
        }

        private void BrightnessSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDraggingBrightness)
            {
                brightnessAutoHideTimer.Stop();
                HandleBrightnessSliderMove(e.GetPosition(BrightnessSliderTrack).X);
                e.Handled = true;
            }
        }

        private void BrightnessSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDraggingBrightness)
            {
                isDraggingBrightness = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                brightnessAutoHideTimer.Start();
                e.Handled = true;
            }
        }

        private void HandleBrightnessSliderMove(double mouseX)
        {
            double trackWidth = 52.0;
            double fraction = Math.Clamp((mouseX - 4.0) / trackWidth, 0.0, 1.0);
            int pct = (int)Math.Round(fraction * 100);
            lastKnownBrightness = pct;
            SetWindowsBrightness(pct);
            TriggerBrightnessHUD(pct);
        }

        #region DDC/CI External Monitor Brightness Helper
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        private static bool TryGetDdcCiBrightness(out int brightness)
        {
            brightness = -1;
            try
            {
                IntPtr primaryMon = MonitorFromWindow(IntPtr.Zero, 1);
                if (primaryMon == IntPtr.Zero) return false;

                if (GetNumberOfPhysicalMonitorsFromHMONITOR(primaryMon, out uint count) && count > 0)
                {
                    var physMons = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(primaryMon, count, physMons))
                    {
                        try
                        {
                            if (GetMonitorBrightness(physMons[0].hPhysicalMonitor, out uint min, out uint cur, out uint max))
                            {
                                brightness = (int)cur;
                                return true;
                            }
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(count, physMons);
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void SetDdcCiBrightness(uint brightness)
        {
            try
            {
                IntPtr primaryMon = MonitorFromWindow(IntPtr.Zero, 1);
                if (primaryMon == IntPtr.Zero) return;

                if (GetNumberOfPhysicalMonitorsFromHMONITOR(primaryMon, out uint count) && count > 0)
                {
                    var physMons = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(primaryMon, count, physMons))
                    {
                        try
                        {
                            foreach (var m in physMons)
                            {
                                SetMonitorBrightness(m.hPhysicalMonitor, brightness);
                            }
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(count, physMons);
                        }
                    }
                }
            }
            catch { }
        }
        #endregion

        #endregion

        #region Windows 11 Pure System DND Listener

        private void CheckWindowsDndState()
        {
            try
            {
                bool systemDnd = WindowsDndNative.GetWindows11DndActive();
                
                if (!isInitialDndLoaded)
                {
                    isDndOn = systemDnd;
                    isInitialDndLoaded = true;
                    UpdateIndicatorVisuals();
                    return;
                }

                if (systemDnd != isDndOn)
                {
                    isDndOn = systemDnd;
                    ShowDndHUD(isDndOn);
                }
            }
            catch { }
        }

        public void ShowDndHUD(bool enabled)
        {
            isDndHudActive = true;
            isVolumeHudActive = false;
            isBrightnessHudActive = false;
            isBluetoothHudActive = false;
            isAirDropHudActive = false;
            isExpanded = false;

            TxtDndStatus.Text = enabled ? "On" : "Off";
            TxtDndStatus.Foreground = new SolidColorBrush(enabled ? Color.FromRgb(0x5E, 0x5C, 0xE6) : Color.FromRgb(0x8E, 0x8E, 0x93));

            HideAllHudViews();
            DndHudView.Visibility = Visibility.Visible;

            double targetW = currentMode == ShapeDisplayMode.Notch ? 240 : 220;
            double targetH = currentMode == ShapeDisplayMode.Notch ? notchHeight : islandHeight;

            if (ShapeRoot.Width != targetW || ShapeRoot.Height != targetH)
            {
                AnimateSize(targetW, targetH);
            }

            dndAutoHideTimer.Stop();
            dndAutoHideTimer.Start();
        }

        public void ToggleDnd()
        {
            isDndOn = !isDndOn;
            ShowDndHUD(isDndOn);
        }

        private void DndAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            dndAutoHideTimer.Stop();
            isDndHudActive = false;

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();
        }

        #endregion

        #region Crash-Proof Real-Time Volume Engine

        private void InitWindowsCoreAudio()
        {
            IMMDevice? dev = null;
            try
            {
                if (audioEndpointVolume != null)
                {
                    try { Marshal.ReleaseComObject(audioEndpointVolume); } catch { }
                    audioEndpointVolume = null;
                }

                if (deviceEnumerator == null)
                {
                    deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                }

                deviceEnumerator.GetDefaultAudioEndpoint(0, 1, out dev);
                
                if (dev != null)
                {
                    Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
                    dev.Activate(ref IID_IAudioEndpointVolume, 1, IntPtr.Zero, out object epvObj);
                    audioEndpointVolume = (IAudioEndpointVolume)epvObj;

                    if (audioEndpointVolume != null)
                    {
                        audioEndpointVolume.GetMasterVolumeLevelScalar(out float initialVol);
                        audioEndpointVolume.GetMute(out bool initialMute);
                        lastKnownVolume = initialVol;
                        lastKnownMute = initialMute;
                        displayedVolumeLevel = (int)Math.Round(initialVol * 100);
                        isInitialVolumeLoaded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText("audio_init.log", ex.ToString());
            }
            finally
            {
                if (dev != null) try { Marshal.ReleaseComObject(dev); } catch { }
            }
        }

        private void VolumePollTimer_Tick(object? sender, EventArgs e)
        {
            if (audioEndpointVolume == null)
            {
                InitWindowsCoreAudio();
                return;
            }

            try
            {
                audioEndpointVolume.GetMasterVolumeLevelScalar(out float currentVol);
                audioEndpointVolume.GetMute(out bool currentMute);

                if (!isInitialVolumeLoaded)
                {
                    lastKnownVolume = currentVol;
                    lastKnownMute = currentMute;
                    displayedVolumeLevel = (int)Math.Round(currentVol * 100);
                    isInitialVolumeLoaded = true;
                    return;
                }

                if (Math.Abs(currentVol - lastKnownVolume) > 0.001f || currentMute != lastKnownMute)
                {
                    lastKnownVolume = currentVol;
                    lastKnownMute = currentMute;
                    TriggerVolumeHUD(currentVol, currentMute);
                }
            }
            catch (COMException)
            {
                InitWindowsCoreAudio();
            }
            catch { }
        }

        public void TriggerVolumeHUD(float level, bool isMuted)
        {
            isVolumeHudActive = true;
            isBrightnessHudActive = false;
            isDndHudActive = false;
            isBluetoothHudActive = false;
            isAirDropHudActive = false;
            isExpanded = false;

            // GPU Optimization: Start the fast poller on-demand for real-time tracking during active volume changes
            if (!volumePollTimer.IsEnabled) volumePollTimer.Start();

            int pct = (int)Math.Round(level * 100);

            if (isMuted || pct == 0)
            {
                VolumeSpeakerIcon.Data = (Geometry)FindResource("IconSpeakerMute");
                VolumeSpeakerIcon.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)); // Red for Mute
                VolumeProgressFill.Width = 0;
            }
            else
            {
                VolumeSpeakerIcon.Fill = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
                if (pct < 33)
                    VolumeSpeakerIcon.Data = (Geometry)FindResource("IconSpeakerLow");
                else if (pct < 66)
                    VolumeSpeakerIcon.Data = (Geometry)FindResource("IconSpeakerMedium");
                else
                    VolumeSpeakerIcon.Data = (Geometry)FindResource("IconSpeakerHigh");

                double fillWidth = Math.Max(0, (pct / 100.0) * 52.0);
                VolumeProgressFill.Width = fillWidth;
            }

            bool isGoingUp = pct >= displayedVolumeLevel;
            Animate3DFlipVolume(isMuted ? "0" : $"{pct}", isGoingUp);
            displayedVolumeLevel = isMuted ? 0 : pct;

            HideAllHudViews();
            VolumeHudView.Visibility = Visibility.Visible;

            double targetW = currentMode == ShapeDisplayMode.Notch ? 270 : 250;
            double targetH = currentMode == ShapeDisplayMode.Notch ? notchHeight : islandHeight;

            if (ShapeRoot.Width != targetW || ShapeRoot.Height != targetH)
            {
                AnimateSize(targetW, targetH);
            }

            volumeAutoHideTimer.Stop();
            volumeAutoHideTimer.Start();
        }

        public void ToggleMasterMute()
        {
            try
            {
                if (audioEndpointVolume == null)
                {
                    InitWindowsCoreAudio();
                }

                if (audioEndpointVolume != null)
                {
                    audioEndpointVolume.GetMute(out bool currentMute);
                    bool newMute = !currentMute;
                    var guid = Guid.Empty;
                    audioEndpointVolume.SetMute(newMute, ref guid);
                    lastKnownMute = newMute;

                    audioEndpointVolume.GetMasterVolumeLevelScalar(out float currentVol);
                    lastKnownVolume = currentVol;

                    TriggerVolumeHUD(currentVol, newMute);
                }
            }
            catch { }
        }

        private void Animate3DFlipVolume(string newText, bool isGoingUp)
        {
            if (TxtVolumeNew.Text == newText) return;

            string oldText = TxtVolumeNew.Text;
            TxtVolumeOld.Text = oldText;
            TxtVolumeNew.Text = newText;

            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var halfDuration = TimeSpan.FromMilliseconds(65);

            double oldTargetY = isGoingUp ? -8 : 8;
            double newStartY = isGoingUp ? 8 : -8;

            ScaleVolumeOld.ScaleY = 1.0;
            TransVolumeOld.Y = 0;
            TxtVolumeOld.Opacity = 1.0;

            ScaleVolumeOld.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.0, halfDuration) { EasingFunction = easeIn });
            TransVolumeOld.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oldTargetY, halfDuration) { EasingFunction = easeIn });
            TxtVolumeOld.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, halfDuration) { EasingFunction = easeIn });

            ScaleVolumeNew.ScaleY = 0.0;
            TransVolumeNew.Y = newStartY;
            TxtVolumeNew.Opacity = 0.0;

            ScaleVolumeNew.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
            TransVolumeNew.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
            TxtVolumeNew.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, halfDuration) { BeginTime = halfDuration, EasingFunction = easeOut });
        }

        private void VolumeAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (isDraggingVolume) return;
            volumeAutoHideTimer.Stop();
            isVolumeHudActive = false;

            // GPU Optimization: Stop the fast 45ms volume poller now that HUD is dismissed
            if (volumePollTimer.IsEnabled) volumePollTimer.Stop();

            HideAllHudViews();
            StealthView.Visibility = Visibility.Visible;

            UpdateIndicatorVisuals();
        }

        private bool isDraggingVolume = false;

        public void SetMasterVolume(float scalar)
        {
            try
            {
                float clamped = Math.Clamp(scalar, 0f, 1f);
                var guid = Guid.Empty;
                audioEndpointVolume?.SetMasterVolumeLevelScalar(clamped, ref guid);
                lastKnownVolume = clamped;
                TriggerVolumeHUD(clamped, false);
            }
            catch { }
        }

        private void VolumeSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingVolume = true;
            (sender as UIElement)?.CaptureMouse();
            volumeAutoHideTimer.Stop();
            HandleVolumeSliderMove(e.GetPosition(VolumeSliderTrack).X);
            e.Handled = true;
        }

        private void VolumeSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDraggingVolume)
            {
                volumeAutoHideTimer.Stop();
                HandleVolumeSliderMove(e.GetPosition(VolumeSliderTrack).X);
                e.Handled = true;
            }
        }

        private void VolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDraggingVolume)
            {
                isDraggingVolume = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                volumeAutoHideTimer.Start();
                e.Handled = true;
            }
        }

        private void HandleVolumeSliderMove(double mouseX)
        {
            double trackWidth = 52.0;
            double fraction = Math.Clamp((mouseX - 4.0) / trackWidth, 0.0, 1.0);
            SetMasterVolume((float)fraction);
        }

        #endregion

        #region Privacy & Mode Logic

        private void SetupPrivacyPulseAnimation()
        {
            var pulseAnimLeft = new DoubleAnimation
            {
                From = 1.0,
                To = 0.25,
                Duration = TimeSpan.FromMilliseconds(1100),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            var glowAnimLeft = new DoubleAnimation
            {
                From = 0.85,
                To = 0.10,
                Duration = TimeSpan.FromMilliseconds(1100),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            var pulseAnimRight = new DoubleAnimation
            {
                From = 1.0,
                To = 0.25,
                Duration = TimeSpan.FromMilliseconds(1100),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            var glowAnimRight = new DoubleAnimation
            {
                From = 0.85,
                To = 0.10,
                Duration = TimeSpan.FromMilliseconds(1100),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            privacyPulseStoryboard = new Storyboard();
            privacyPulseStoryboard.Children.Add(pulseAnimLeft);
            privacyPulseStoryboard.Children.Add(glowAnimLeft);
            privacyPulseStoryboard.Children.Add(pulseAnimRight);
            privacyPulseStoryboard.Children.Add(glowAnimRight);

            Storyboard.SetTarget(pulseAnimLeft, PrivacyDotLeft);
            Storyboard.SetTargetProperty(pulseAnimLeft, new PropertyPath(OpacityProperty));
            Storyboard.SetTarget(glowAnimLeft, PrivacyDotLeftGlow);
            Storyboard.SetTargetProperty(glowAnimLeft, new PropertyPath(OpacityProperty));

            Storyboard.SetTarget(pulseAnimRight, PrivacyDotRight);
            Storyboard.SetTargetProperty(pulseAnimRight, new PropertyPath(OpacityProperty));
            Storyboard.SetTarget(glowAnimRight, PrivacyDotRightGlow);
            Storyboard.SetTargetProperty(glowAnimRight, new PropertyPath(OpacityProperty));
        }

        private void BackgroundWatcherTimer_Tick(object? sender, EventArgs e)
        {
            Task.Run(() =>
            {
                CheckWindowsPrivacyState();
                CheckWindowsDndState();
            });

            // Auto-collapse compact indicators for Music, Dock, and Bluetooth after 20s idle (Timer & Internet Speed remain active)
            if (!isExpanded)
            {
                bool needsRefresh = (MediaSessionManager.Instance.HasActiveSession && !MediaSessionManager.Instance.CurrentTrack.IsPlaying) ||
                                    DockShelfManager.Instance.HasItems ||
                                    BluetoothBatteryManager.Instance.ConnectedDevices.Count > 0;

                if (needsRefresh && !isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive)
                {
                    UpdateIndicatorVisuals();
                }
            }
        }

        private static readonly HashSet<string> CaptureProcessSet = new(StringComparer.OrdinalIgnoreCase)
        {
            "obs64", "obs32", "Captura", "ShareX", "Bandicam", "CamtasiaStudio", "CamRecorder",
            "ScreenClippingHost", "bcastdvr"
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private void CheckWindowsPrivacyState()
        {
            try
            {
                bool camInUse = IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam");
                bool micInUse = IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone");
                bool recInUse = IsScreenRecordingActive();
                bool shareInUse = IsScreenSharingActive();

                if (camInUse != isCameraActive || micInUse != isMicActive || recInUse != isRecordingActive || shareInUse != isScreenSharingActive)
                {
                    isCameraActive = camInUse;
                    isMicActive = micInUse;
                    isRecordingActive = recInUse;
                    isScreenSharingActive = shareInUse;

                    Dispatcher.Invoke(() =>
                    {
                        if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive)
                        {
                            UpdateIndicatorVisuals();
                        }
                    });
                }
            }
            catch { }
        }

        private bool IsScreenRecordingActive()
        {
            try
            {
                // Check Windows Capability Access Manager for active screen recording
                if (IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\screenRecording") ||
                    IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureProgrammatic"))
                {
                    return true;
                }

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (CaptureProcessSet.Contains(proc.ProcessName)) return true;
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }
            return false;
        }

        private bool IsScreenSharingActive()
        {
            try
            {
                // 1. Check for WebRTC screen sharing floating banners (Google Meet, Discord, Teams, Zoom, Chrome, Brave, Edge, Firefox)
                bool hasSharingWindow = false;
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        var sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        string title = sb.ToString();
                        if (!string.IsNullOrEmpty(title))
                        {
                            if (title.Contains("is sharing your screen", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("is sharing a window", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("is sharing your tab", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("sharing your screen", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Screen Sharing Indicator", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Google Meet is sharing", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Discord Screen Share", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("meet.google.com is sharing", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Microsoft Teams - Screen Share", StringComparison.OrdinalIgnoreCase) ||
                                (title.Contains("Screen Share", StringComparison.OrdinalIgnoreCase) && !title.Contains("DynamicIsland", StringComparison.OrdinalIgnoreCase)))
                            {
                                hasSharingWindow = true;
                                return false;
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (hasSharingWindow) return true;

                // 2. Check Windows Graphics Capture Without Border (Used by WebRTC screen sharing in Chrome / Edge / Discord)
                if (IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureWithoutBorder") ||
                    IsDeviceInUse(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\screenCapture"))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool IsDeviceInUse(string subKeyPath)
        {
            try
            {
                // Check both CurrentUser and LocalMachine
                var hives = new[] { Registry.CurrentUser, Registry.LocalMachine };
                foreach (var root in hives)
                {
                    using var key = root.OpenSubKey(subKeyPath);
                    if (key == null) continue;

                    using var nonPackaged = key.OpenSubKey("NonPackaged");
                    if (nonPackaged != null)
                    {
                        foreach (var appName in nonPackaged.GetSubKeyNames())
                        {
                            using var appKey = nonPackaged.OpenSubKey(appName);
                            if (appKey != null)
                            {
                                var stopTime = appKey.GetValue("LastUsedTimeStop");
                                var startTime = appKey.GetValue("LastUsedTimeStart");
                                if (stopTime is long stopVal && stopVal == 0 && startTime is long startVal && startVal > 0)
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    foreach (var appName in key.GetSubKeyNames())
                    {
                        if (appName == "NonPackaged") continue;
                        using var appKey = key.OpenSubKey(appName);
                        if (appKey != null)
                        {
                            var stopTime = appKey.GetValue("LastUsedTimeStop");
                            var startTime = appKey.GetValue("LastUsedTimeStart");
                            if (stopTime is long stopVal && stopVal == 0 && startTime is long startVal && startVal > 0)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void ApplyMode()
        {
            this.Left = (SystemParameters.PrimaryScreenWidth / 2) - (this.Width / 2);

            if (currentMode == ShapeDisplayMode.Notch)
            {
                this.Top = 0;
                StealthView.Margin = new Thickness(18, 0, 18, 0);
                VolumeHudView.Margin = new Thickness(16, 0, 16, 0);
                BrightnessHudView.Margin = new Thickness(16, 0, 16, 0);
                BluetoothHudView.Margin = new Thickness(22, 6, 22, 6);
                AirDropHudView.Margin = new Thickness(22, 0, 22, 10);
                AirDropPickerView.Margin = new Thickness(16, 10, 16, 10);
                DndHudView.Margin = new Thickness(16, 0, 16, 0);
                CapsLockHudView.Margin = new Thickness(16, 0, 16, 0);
                UniversalExpandedContainer.Margin = new Thickness(22, 12, 22, 12);
            }
            else
            {
                this.Top = 8;
                StealthView.Margin = new Thickness(14, 0, 14, 0);
                VolumeHudView.Margin = new Thickness(12, 0, 12, 0);
                BrightnessHudView.Margin = new Thickness(12, 0, 12, 0);
                BluetoothHudView.Margin = new Thickness(16, 0, 16, 0);
                AirDropHudView.Margin = new Thickness(18, 0, 18, 0);
                AirDropPickerView.Margin = new Thickness(16, 8, 16, 8);
                DndHudView.Margin = new Thickness(12, 0, 12, 0);
                CapsLockHudView.Margin = new Thickness(14, 0, 14, 0);
                UniversalExpandedContainer.Margin = new Thickness(22, 10, 22, 10);
            }

            if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive && !isScreenMirroringHudActive && !isCapsLockHudActive && !isClipboardHudActive)
            {
                UpdateIndicatorVisuals();
            }
        }

        private void UpdateIndicatorVisuals()
        {
            if (isVolumeHudActive || isDndHudActive || isBrightnessHudActive || isBluetoothHudActive || isAirDropHudActive || isScreenMirroringHudActive || isCapsLockHudActive || isClipboardHudActive)
            {
                return;
            }

            // Strict Privacy Indicator Priority Hierarchy & Colors
            Color? activePrivacyCoreColor = null;
            Color? activePrivacyGlowColor = null;

            if (isScreenSharingActive)
            {
                // When screen sharing is active (Google Meet, Discord, etc.), suppress privacy dot
                activePrivacyCoreColor = null;
                activePrivacyGlowColor = null;
            }
            else if (isRecordingActive)
            {
                // Priority 1: Screen Recording / OBS / Game Bar (Red)
                activePrivacyCoreColor = Color.FromRgb(0xFF, 0x3B, 0x30);
                activePrivacyGlowColor = Color.FromArgb(0x55, 0xFF, 0x3B, 0x30);
            }
            else if (isCameraActive && isMicActive)
            {
                // Priority 2: Both Camera & Mic active (Apple Blue / Cyan)
                activePrivacyCoreColor = Color.FromRgb(0x0A, 0x84, 0xFF);
                activePrivacyGlowColor = Color.FromArgb(0x55, 0x0A, 0x84, 0xFF);
            }
            else if (isCameraActive)
            {
                // Priority 3: Camera ONLY active (Apple Green)
                activePrivacyCoreColor = Color.FromRgb(0x30, 0xD1, 0x58);
                activePrivacyGlowColor = Color.FromArgb(0x55, 0x30, 0xD1, 0x58);
            }
            else if (isMicActive)
            {
                // Priority 4: Mic ONLY active (Apple Orange)
                activePrivacyCoreColor = Color.FromRgb(0xFF, 0x9F, 0x0A);
                activePrivacyGlowColor = Color.FromArgb(0x55, 0xFF, 0x9F, 0x0A);
            }

            bool hasPrivacy = activePrivacyCoreColor.HasValue && activePrivacyGlowColor.HasValue;

            if (hasPrivacy && activePrivacyCoreColor.HasValue && activePrivacyGlowColor.HasValue)
            {
                var coreBrush = new SolidColorBrush(activePrivacyCoreColor.Value);
                var glowBrush = new SolidColorBrush(activePrivacyGlowColor.Value);

                PrivacyDotLeft.Fill = coreBrush;
                PrivacyDotLeftGlow.Fill = glowBrush;
                PrivacyDotRight.Fill = coreBrush;
                PrivacyDotRightGlow.Fill = glowBrush;

                if (!isPrivacyPulsePlaying)
                {
                    isPrivacyPulsePlaying = true;
                    privacyPulseStoryboard?.Begin();
                }
            }
            else
            {
                if (isPrivacyPulsePlaying)
                {
                    isPrivacyPulsePlaying = false;
                    privacyPulseStoryboard?.Stop();
                }
            }

            DndStealthIndicator.Visibility = isDndOn ? Visibility.Visible : Visibility.Collapsed;

            if (isIncomingCallActive)
            {
                PrivacyDotLeftContainer.Visibility = Visibility.Collapsed;
                PrivacyDotRightContainer.Visibility = Visibility.Collapsed;
                IdleFaceContainer.Visibility = Visibility.Collapsed;
                HideAllHudViews();
                IncomingCallHudView.Visibility = Visibility.Visible;
                double targetW = 367;
                double targetH = currentMode == ShapeDisplayMode.Notch ? 95 : 86;
                AnimateSize(targetW, targetH);
                return;
            }
            else
            {
                IncomingCallHudView.Visibility = Visibility.Collapsed;
            }

            if (isClipboardHudActive)
            {
                PrivacyDotLeftContainer.Visibility = Visibility.Collapsed;
                PrivacyDotRightContainer.Visibility = Visibility.Collapsed;
                IdleFaceContainer.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasMusicCompact = MediaSessionManager.Instance.ShouldShowCompactMedia;
            bool hasMusicSession = MediaSessionManager.Instance.HasActiveSession;
            bool hasTimer = AppleTimerManager.Instance.IsActive;
            bool hasDockCompact = DockShelfManager.Instance.ShouldShowCompactDock;
            bool hasDockSession = DockShelfManager.Instance.HasItems;
            bool hasBluetoothCompact = BluetoothBatteryManager.Instance.ShouldShowCompactBluetooth;
            bool isIdle = !hasDockCompact && !hasMusicCompact && !hasTimer && !hasBluetoothCompact && currentExpandedTab != ExpandedActivityTab.Network;

            if (isExpanded)
            {
                PrivacyDotLeftContainer.Visibility = Visibility.Collapsed;
                PrivacyDotRightContainer.Visibility = Visibility.Collapsed;
                IdleFaceContainer.Visibility = Visibility.Collapsed;

                HideAllHudViews();
                HideAllExpandedTabBodies();
                UniversalExpandedContainer.Visibility = Visibility.Visible;

                // Hide top app switcher nav during Call mode
                if (UniversalNavContainer != null)
                {
                    UniversalNavContainer.Visibility = (currentExpandedTab == ExpandedActivityTab.Call) ? Visibility.Collapsed : Visibility.Visible;
                }

                // Reset nav button states matching dock_dropzone_mock.html
                var defaultBg = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D));
                var defaultMutedIcon = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xED));

                BtnNavHome.Background = defaultBg;
                BtnNavShelf.Background = defaultBg;
                BtnNavMusic.Background = defaultBg;
                BtnNavTimer.Background = defaultBg;
                BtnNavBluetooth.Background = defaultBg;
                BtnNavClipboard.Background = defaultBg;
                BtnNavNetwork.Background = defaultBg;
                BtnNavScreenMirroring.Background = defaultBg;

                IconNavHome.Stroke = defaultMutedIcon;
                IconNavShelf.Stroke = defaultMutedIcon;
                IconNavMusic.Stroke = defaultMutedIcon;
                IconNavTimer.Stroke = defaultMutedIcon;
                IconNavBluetooth.Fill = defaultMutedIcon;
                IconNavClipboard.Fill = defaultMutedIcon;
                IconNavNetwork.Stroke = defaultMutedIcon;
                IconNavScreenMirroring.Fill = defaultMutedIcon;

                BtnClearAllShelf.Visibility = (currentExpandedTab == ExpandedActivityTab.Shelf) ? Visibility.Visible : Visibility.Collapsed;
                BtnPinShelf.Visibility = (currentExpandedTab == ExpandedActivityTab.Shelf) ? Visibility.Visible : Visibility.Collapsed;

                if (currentExpandedTab == ExpandedActivityTab.ScreenMirroring)
                {
                    BtnNavScreenMirroring.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x37, 0xA3, 0xDE));
                    IconNavScreenMirroring.Fill = new SolidColorBrush(Color.FromRgb(0x37, 0xA3, 0xDE));
                    ViewScreenMirroringBody.Visibility = Visibility.Visible;

                    TxtExpandedStopMirroringBtn.Text = isScreenSharingActive ? "Stop Mirroring" : "Windows Cast / Mirror";
                    TxtExpandedScreenMirroringTarget.Text = isScreenSharingActive ? "Active Screen Share • infinity" : "infinity";

                    double targetW = 440;
                    double targetH = currentMode == ShapeDisplayMode.Notch ? 165 : 148;
                    AnimateSize(targetW, targetH);
                }
                else if (currentExpandedTab == ExpandedActivityTab.Network)
                {
                    ViewNetworkBody.Visibility = Visibility.Visible;

                    UpdateNetworkVisuals();

                    double targetW = 460;
                    double targetH = 130;
                    AnimateSize(targetW, targetH);
                }
                else if (currentExpandedTab == ExpandedActivityTab.Music)
                {
                    BtnNavMusic.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0x24, 0x55));
                    IconNavMusic.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x24, 0x55));
                    ViewMusicBody.Visibility = Visibility.Visible;

                    if (isLyricsViewActive)
                    {
                        MusicStandardPlayerView.Visibility = Visibility.Collapsed;
                        MusicLyricsKaraokeView.Visibility = Visibility.Visible;
                        double targetW = 490;
                        double targetH = currentMode == ShapeDisplayMode.Notch ? 290 : 275;
                        AnimateSize(targetW, targetH);
                        RenderLyricsList();
                    }
                    else
                    {
                        MusicLyricsKaraokeView.Visibility = Visibility.Collapsed;
                        MusicStandardPlayerView.Visibility = Visibility.Visible;
                        double targetW = 510;
                        double targetH = 195;
                        AnimateSize(targetW, targetH);
                    }
                }
                else if (currentExpandedTab == ExpandedActivityTab.Timer)
                {
                    BtnNavTimer.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFB, 0x8B, 0x28));
                    IconNavTimer.Stroke = new SolidColorBrush(Color.FromRgb(0xFB, 0x8B, 0x28));
                    ViewTimerBody.Visibility = Visibility.Visible;

                    if (AppleTimerManager.Instance.IsActive)
                    {
                        TimerActiveContainer.Visibility = Visibility.Visible;
                        TimerSetupContainer.Visibility = Visibility.Collapsed;
                        TxtTimerExpanded.Text = AppleTimerManager.FormatTimerText(AppleTimerManager.Instance.RemainingDuration);
                        IconTimerPauseResume.Data = (Geometry)FindResource(AppleTimerManager.Instance.State == AppleTimerState.Running ? "IconMediaPause" : "IconMediaPlay");

                        double targetW = 440;
                        double targetH = currentMode == ShapeDisplayMode.Notch ? 134 : 126;
                        AnimateSize(targetW, targetH);
                    }
                    else
                    {
                        TimerActiveContainer.Visibility = Visibility.Collapsed;
                        TimerSetupContainer.Visibility = Visibility.Visible;

                        double targetW = 440;
                        double targetH = currentMode == ShapeDisplayMode.Notch ? 148 : 140;
                        AnimateSize(targetW, targetH);
                    }
                }
                else if (currentExpandedTab == ExpandedActivityTab.Bluetooth)
                {
                    BtnNavBluetooth.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x0A, 0x9C, 0xFF));
                    IconNavBluetooth.Fill = new SolidColorBrush(Color.FromRgb(0x0A, 0x9C, 0xFF));
                    ViewBluetoothBody.Visibility = Visibility.Visible;

                    var endpoint = GetCachedAudioEndpoint();
                    TxtExpBtDeviceName.Text = string.IsNullOrWhiteSpace(endpoint.CleanName) ? "Internal Speakers" : endpoint.CleanName;

                    switch (endpoint.Category)
                    {
                        case AudioDeviceCategory.TwsEarbuds:
                            ExpDeviceIconImage.Visibility = Visibility.Collapsed;
                            ExpDeviceIconVector.Visibility = Visibility.Visible;
                            TxtExpBtSubtitle.Text = "TWS Connected";
                            break;
                        case AudioDeviceCategory.WiredHeadphones:
                            ExpDeviceIconVector.Visibility = Visibility.Collapsed;
                            ExpDeviceIconImage.Visibility = Visibility.Visible;
                            ExpDeviceIconImage.Source = imgWiredHeadphones ?? imgWirelessHeadphones;
                            TxtExpBtSubtitle.Text = "Headphones Connected";
                            break;
                        case AudioDeviceCategory.WirelessHeadphones:
                            ExpDeviceIconVector.Visibility = Visibility.Collapsed;
                            ExpDeviceIconImage.Visibility = Visibility.Visible;
                            ExpDeviceIconImage.Source = imgWirelessHeadphones;
                            TxtExpBtSubtitle.Text = "Connected";
                            break;
                        default:
                            ExpDeviceIconVector.Visibility = Visibility.Collapsed;
                            ExpDeviceIconImage.Visibility = Visibility.Visible;
                            ExpDeviceIconImage.Source = imgSpeaker;
                            TxtExpBtSubtitle.Text = "Audio Output";
                            break;
                    }

                    int? bat = (endpoint.Category == AudioDeviceCategory.TwsEarbuds || endpoint.Category == AudioDeviceCategory.WirelessHeadphones)
                        ? (BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.Name) ?? BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.CleanName) ?? BluetoothBatteryManager.Instance.PrimaryConnectedDevice?.BatteryLevel)
                        : null;

                    if (bat.HasValue)
                    {
                        ExpBatteryContainer.Visibility = Visibility.Visible;
                        int clampedBat = Math.Clamp(bat.Value, 0, 100);
                        TxtExpBtBatteryPercent.Text = $"{clampedBat}";
                        Color batColor = clampedBat switch
                        {
                            < 20 => Color.FromRgb(255, 59, 48),
                            < 50 => Color.FromRgb(255, 214, 10),
                            _ => Color.FromRgb(0x00, 0xE4, 0x6C)
                        };
                        var batBrush = new SolidColorBrush(batColor);
                        TxtExpBtBatteryPercent.Foreground = batBrush;
                        ExpBtBatteryArc.Stroke = batBrush;
                        DrawExpBatteryCircleArc(clampedBat / 100.0);
                    }
                    else
                    {
                        ExpBatteryContainer.Visibility = Visibility.Collapsed;
                    }

                    // Show Disconnect button if wireless device is connected
                    BtnDisconnectBt.Visibility = (endpoint.Category == AudioDeviceCategory.TwsEarbuds || endpoint.Category == AudioDeviceCategory.WirelessHeadphones)
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    // Populate Audio Output Dropdown List
                    PopulateAudioOutputDevicesList(endpoint);

                    if (_isAudioOutputDropdownOpen)
                    {
                        AudioOutputDropdownContainer.Visibility = Visibility.Visible;
                        IconOutputChevron.Data = (Geometry)FindResource("IconChevronUp");
                    }
                    else
                    {
                        AudioOutputDropdownContainer.Visibility = Visibility.Collapsed;
                        IconOutputChevron.Data = (Geometry)FindResource("IconChevronDown");
                    }

                    int itemCount = AudioOutputDevicesList.Children.Count;
                    double targetW = 460;
                    double targetH = _isAudioOutputDropdownOpen
                        ? (currentMode == ShapeDisplayMode.Notch ? (160 + (itemCount * 40) + 16) : (150 + (itemCount * 40) + 16))
                        : (currentMode == ShapeDisplayMode.Notch ? 156 : 148);
                    AnimateSize(targetW, targetH);
                }
                else if (currentExpandedTab == ExpandedActivityTab.Shelf)
                {
                    BtnNavShelf.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x0A, 0x84, 0xFF));
                    IconNavShelf.Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                    ViewShelfBody.Visibility = Visibility.Visible;

                    if (DockShelfManager.Instance.Items.Count > 0)
                    {
                        ShelfEmptyPrompt.Visibility = Visibility.Collapsed;
                        ShelfFilesScrollViewer.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ShelfEmptyPrompt.Visibility = Visibility.Visible;
                        ShelfFilesScrollViewer.Visibility = Visibility.Collapsed;
                    }

                    double targetW = 490;
                    double targetH = 175;
                    AnimateSize(targetW, targetH);
                }
                else if (currentExpandedTab == ExpandedActivityTab.Clipboard)
                {
                    BtnNavClipboard.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x37, 0xA3, 0xDE));
                    IconNavClipboard.Fill = new SolidColorBrush(Color.FromRgb(0x37, 0xA3, 0xDE));
                    ViewClipboardBody.Visibility = Visibility.Visible;

                    UpdateSubTabPills();
                    RenderClipboardHistoryList();

                    double targetW = 440;
                    double targetH = currentMode == ShapeDisplayMode.Notch ? 340 : 325;
                    AnimateSize(targetW, targetH);
                }
                else if (currentExpandedTab == ExpandedActivityTab.Call)
                {
                    ViewCallExpandedBody.Visibility = Visibility.Visible;
                    string callerName = WhatsAppCallManager.Instance.CurrentCall?.CallerName ?? "Tamia Castle";
                    TxtCallExpandedName.Text = callerName;
                    TxtCallExpandedInitial.Text = !string.IsNullOrWhiteSpace(callerName) ? callerName.Substring(0, 1).ToUpperInvariant() : "👤";
                    string durText = WhatsAppCallManager.Instance.CurrentCall != null
                        ? (WhatsAppCallManager.Instance.CurrentCall.Duration.TotalHours >= 1 ? WhatsAppCallManager.Instance.CurrentCall.Duration.ToString(@"hh\:mm\:ss") : WhatsAppCallManager.Instance.CurrentCall.Duration.ToString(@"mm\:ss"))
                        : "00:00";
                    TxtCallExpandedSubtitle.Text = $"{durText} • {(WhatsAppCallManager.Instance.CurrentCall?.Type == CallType.Video ? "WhatsApp Video" : "WhatsApp Audio")}";
                    double targetW = 380;
                    double targetH = currentMode == ShapeDisplayMode.Notch ? 160 : 148;
                    AnimateSize(targetW, targetH);
                }
                else
                {
                    // Home
                    BtnNavHome.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                    IconNavHome.Stroke = Brushes.White;

                    double targetW = 460;
                    double targetH = 130;
                    AnimateSize(targetW, targetH);
                }
            }
            else
            {
                // Compact Pill Mode
                UniversalExpandedContainer.Visibility = Visibility.Collapsed;
                StealthView.Visibility = Visibility.Visible;

                CompactAlbumArtBorder.Visibility = Visibility.Collapsed;
                CompactVisualizer.Visibility = Visibility.Collapsed;
                TimerCompactRing.Visibility = Visibility.Collapsed;
                TxtTimerCompact.Visibility = Visibility.Collapsed;
                DockCompactContainer.Visibility = Visibility.Collapsed;
                DockCompactRing.Visibility = Visibility.Collapsed;
                BluetoothCompactContainer.Visibility = Visibility.Collapsed;
                TxtBluetoothCompact.Visibility = Visibility.Collapsed;
                NetworkCompactContainer.Visibility = Visibility.Collapsed;
                TxtNetworkCompact.Visibility = Visibility.Collapsed;
                AirDropCompactContainer.Visibility = Visibility.Collapsed;
                AirDropCompactProgressContainer.Visibility = Visibility.Collapsed;
                ScreenShareCompactLeftContainer.Visibility = Visibility.Collapsed;
                ScreenShareCompactRightContainer.Visibility = Visibility.Collapsed;
                CallCompactVoiceLeftContainer.Visibility = Visibility.Collapsed;
                CallCompactVideoLeftContainer.Visibility = Visibility.Collapsed;
                CallCompactWaveform.Visibility = Visibility.Collapsed;

                double baseW = currentMode == ShapeDisplayMode.Notch ? notchBaseWidth : islandBaseWidth;
                double baseH = currentMode == ShapeDisplayMode.Notch ? notchHeight : islandHeight;

                bool isMultiActivityActive = false;

                if (WhatsAppCallManager.Instance.IsCallActive)
                {
                    if (WhatsAppCallManager.Instance.CurrentCall?.Type == CallType.Video)
                    {
                        CallCompactVideoLeftContainer.Visibility = Visibility.Visible;
                        CallCompactWaveform.Visibility = Visibility.Visible;
                        baseW = currentMode == ShapeDisplayMode.Notch ? 175 : 165;
                        baseH = currentMode == ShapeDisplayMode.Notch ? 36 : 38;
                    }
                    else
                    {
                        CallCompactVoiceLeftContainer.Visibility = Visibility.Visible;
                        CallCompactWaveform.Visibility = Visibility.Visible;
                        string durText = WhatsAppCallManager.Instance.CurrentCall != null
                            ? (WhatsAppCallManager.Instance.CurrentCall.Duration.TotalHours >= 1 ? WhatsAppCallManager.Instance.CurrentCall.Duration.ToString(@"hh\:mm\:ss") : WhatsAppCallManager.Instance.CurrentCall.Duration.ToString(@"mm\:ss"))
                            : "00:00";
                        TxtCallCompactDuration.Text = durText;
                        baseW = currentMode == ShapeDisplayMode.Notch ? 215 : 200;
                        baseH = currentMode == ShapeDisplayMode.Notch ? 36 : 38;
                    }
                    isMultiActivityActive = true;
                }
                else if (isAirDropHudActive || isAirDropTransferActive)
                {
                    AirDropCompactContainer.Visibility = Visibility.Visible;
                    AirDropCompactProgressContainer.Visibility = Visibility.Visible;
                    baseW = currentMode == ShapeDisplayMode.Notch ? 210 : 190;
                    isMultiActivityActive = true;
                }
                else if (isScreenSharingActive)
                {
                    ScreenShareCompactLeftContainer.Visibility = Visibility.Visible;
                    ScreenShareCompactRightContainer.Visibility = Visibility.Visible;
                    baseW = currentMode == ShapeDisplayMode.Notch ? 200 : 185;
                    isMultiActivityActive = true;
                }
                else if (currentExpandedTab == ExpandedActivityTab.Network)
                {
                    NetworkCompactContainer.Visibility = Visibility.Visible;
                    TxtNetworkCompact.Visibility = Visibility.Visible;
                    UpdateNetworkVisuals();
                    baseW = currentMode == ShapeDisplayMode.Notch ? 200 : 185;
                    isMultiActivityActive = true;
                }
                else if (currentExpandedTab == ExpandedActivityTab.Music && hasMusicCompact)
                {
                    CompactAlbumArtBorder.Visibility = Visibility.Visible;
                    CompactVisualizer.Visibility = Visibility.Visible;
                    CompactVisualizer.SetAccentFromImage(MediaSessionManager.Instance.CurrentTrack.Thumbnail, MediaSessionManager.Instance.CurrentTrack.AppSource);
                    CompactVisualizer.IsPlaying = MediaSessionManager.Instance.CurrentTrack.IsPlaying;
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (currentExpandedTab == ExpandedActivityTab.Timer && hasTimer)
                {
                    TimerCompactRing.Visibility = Visibility.Visible;
                    TxtTimerCompact.Visibility = Visibility.Visible;
                    TxtTimerCompact.Text = AppleTimerManager.FormatTimerText(AppleTimerManager.Instance.RemainingDuration);
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (currentExpandedTab == ExpandedActivityTab.Shelf && hasDockCompact)
                {
                    DockCompactContainer.Visibility = Visibility.Visible;
                    DockCompactRing.Visibility = Visibility.Visible;
                    DockCompactRing.SetStatus(DockShelfManager.Instance.Status, DockShelfManager.Instance.ItemCount);
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (currentExpandedTab == ExpandedActivityTab.Bluetooth)
                {
                    var endpoint = GetCachedAudioEndpoint();
                    if (endpoint.Category == AudioDeviceCategory.TwsEarbuds || endpoint.Category == AudioDeviceCategory.WirelessHeadphones)
                    {
                        BluetoothCompactContainer.Visibility = Visibility.Visible;
                        if (endpoint.Category == AudioDeviceCategory.TwsEarbuds)
                        {
                            IconCompactTws.Visibility = Visibility.Visible;
                            IconCompactHeadphones.Visibility = Visibility.Collapsed;
                            IconCompactBluetooth.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            IconCompactTws.Visibility = Visibility.Collapsed;
                            IconCompactHeadphones.Visibility = Visibility.Visible;
                            IconCompactBluetooth.Visibility = Visibility.Collapsed;
                        }

                        TxtBluetoothCompact.Visibility = Visibility.Visible;
                        int? bat = BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.Name) ?? BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.CleanName) ?? BluetoothBatteryManager.Instance.PrimaryConnectedDevice?.BatteryLevel;
                        TxtBluetoothCompact.Text = bat.HasValue ? $"{bat.Value}%" : "Connected";
                        baseW = currentMode == ShapeDisplayMode.Notch ? (bat.HasValue ? 195 : 212) : (bat.HasValue ? 180 : 196);
                        isMultiActivityActive = true;
                    }
                }
                // Fallback to active background tasks
                else if (hasMusicCompact)
                {
                    CompactAlbumArtBorder.Visibility = Visibility.Visible;
                    CompactVisualizer.Visibility = Visibility.Visible;
                    CompactVisualizer.SetAccentFromImage(MediaSessionManager.Instance.CurrentTrack.Thumbnail, MediaSessionManager.Instance.CurrentTrack.AppSource);
                    CompactVisualizer.IsPlaying = MediaSessionManager.Instance.CurrentTrack.IsPlaying;
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (hasTimer)
                {
                    TimerCompactRing.Visibility = Visibility.Visible;
                    TxtTimerCompact.Visibility = Visibility.Visible;
                    TxtTimerCompact.Text = AppleTimerManager.FormatTimerText(AppleTimerManager.Instance.RemainingDuration);
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (hasDockCompact)
                {
                    DockCompactContainer.Visibility = Visibility.Visible;
                    DockCompactRing.Visibility = Visibility.Visible;
                    DockCompactRing.SetStatus(DockShelfManager.Instance.Status, DockShelfManager.Instance.ItemCount);
                    baseW = currentMode == ShapeDisplayMode.Notch ? 195 : 180;
                    isMultiActivityActive = true;
                }
                else if (GetCachedAudioEndpoint().Category == AudioDeviceCategory.TwsEarbuds || GetCachedAudioEndpoint().Category == AudioDeviceCategory.WirelessHeadphones)
                {
                    var endpoint = GetCachedAudioEndpoint();
                    BluetoothCompactContainer.Visibility = Visibility.Visible;
                    if (endpoint.Category == AudioDeviceCategory.TwsEarbuds)
                    {
                        IconCompactTws.Visibility = Visibility.Visible;
                        IconCompactHeadphones.Visibility = Visibility.Collapsed;
                        IconCompactBluetooth.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        IconCompactTws.Visibility = Visibility.Collapsed;
                        IconCompactHeadphones.Visibility = Visibility.Visible;
                        IconCompactBluetooth.Visibility = Visibility.Collapsed;
                    }

                    TxtBluetoothCompact.Visibility = Visibility.Visible;
                    int? bat = BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.Name) ?? BluetoothBatteryManager.Instance.GetBatteryForDevice(endpoint.CleanName) ?? BluetoothBatteryManager.Instance.PrimaryConnectedDevice?.BatteryLevel;
                    TxtBluetoothCompact.Text = bat.HasValue ? $"{bat.Value}%" : "Connected";
                    baseW = currentMode == ShapeDisplayMode.Notch ? (bat.HasValue ? 195 : 212) : (bat.HasValue ? 180 : 196);
                    isMultiActivityActive = true;
                }

                if (isMultiActivityActive)
                {
                    // Multi-activity active: Privacy dot shows on LEFT wing, Mascot is hidden
                    IdleFaceContainer.Visibility = Visibility.Collapsed;
                    PrivacyDotLeftContainer.Visibility = hasPrivacy ? Visibility.Visible : Visibility.Collapsed;
                    PrivacyDotRightContainer.Visibility = Visibility.Collapsed;
                    if (hasPrivacy) baseW += 18;
                }
                else
                {
                    // Idle state: Mascot face shows in center, Privacy dot shows on RIGHT wing!
                    IdleFaceContainer.Visibility = IsIdleFaceEnabled ? Visibility.Visible : Visibility.Collapsed;
                    PrivacyDotLeftContainer.Visibility = Visibility.Collapsed;
                    PrivacyDotRightContainer.Visibility = hasPrivacy ? Visibility.Visible : Visibility.Collapsed;
                    baseW = hasPrivacy ? (currentMode == ShapeDisplayMode.Notch ? 165 : 155) : (currentMode == ShapeDisplayMode.Notch ? notchBaseWidth : islandBaseWidth);
                }

                AnimateSize(baseW, baseH);
            }

            // GPU/CPU Optimization: Network speed timer runs strictly when rendered on UI, otherwise completely stopped
            bool isNetworkActive = (NetworkCompactContainer.Visibility == Visibility.Visible) ||
                                   (isExpanded && currentExpandedTab == ExpandedActivityTab.Network);
            NetworkSpeedManager.Instance.SetActive(isNetworkActive);
        }

        #endregion

        #region DynamicNotch Geometry Math

        private void RedrawGeometry(double width, double height)
        {
            if (width <= 0 || height <= 0) return;

            if (currentMode == ShapeDisplayMode.Notch)
            {
                double topR = height > 40 ? 9.5 : topEarRadius;
                double btmR = height > 40 ? 24.0 : bottomRadius;

                var figure = new PathFigure
                {
                    StartPoint = new Point(0, 0),
                    IsClosed = true,
                    IsFilled = true
                };

                // 1. Top-left concave flare curve
                figure.Segments.Add(new QuadraticBezierSegment(new Point(topR, 0), new Point(topR, topR), true));

                // 2. Left vertical straight edge
                figure.Segments.Add(new LineSegment(new Point(topR, height - btmR), true));

                // 3. Bottom-left convex corner
                figure.Segments.Add(new QuadraticBezierSegment(new Point(topR, height), new Point(topR + btmR, height), true));

                // 4. Bottom horizontal edge
                figure.Segments.Add(new LineSegment(new Point(width - topR - btmR, height), true));

                // 5. Bottom-right convex corner
                figure.Segments.Add(new QuadraticBezierSegment(new Point(width - topR, height), new Point(width - topR, height - btmR), true));

                // 6. Right vertical straight edge
                figure.Segments.Add(new LineSegment(new Point(width - topR, topR), true));

                // 7. Top-right concave flare curve
                figure.Segments.Add(new QuadraticBezierSegment(new Point(width - topR, 0), new Point(width, 0), true));

                var geo = new PathGeometry();
                geo.Figures.Add(figure);
                NotchPath.Data = geo;
            }
            else
            {
                double r = height > 40 ? 26.0 : islandRadius;
                NotchPath.Data = new RectangleGeometry(new Rect(0, 0, width, height), r, r);
            }
        }

        #endregion

        #region Animation

        private void AnimateSize(double targetWidth, double targetHeight)
        {
            if (ShapeRoot == null) return;

            // Apple Design Principle 3 (Interruptibility): Always start from live presentation value
            double currentW = ShapeRoot.ActualWidth > 0 ? ShapeRoot.ActualWidth : (double.IsNaN(ShapeRoot.Width) ? targetWidth : ShapeRoot.Width);
            double currentH = ShapeRoot.ActualHeight > 0 ? ShapeRoot.ActualHeight : (double.IsNaN(ShapeRoot.Height) ? targetHeight : ShapeRoot.Height);

            if (Math.Abs(currentW - targetWidth) < 0.5 && Math.Abs(currentH - targetHeight) < 0.5)
            {
                return;
            }

            // Apple Design Principle 4 (Critically Damped Spring: Damping 1.0, Response 0.32s)
            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(320);

            var wAnim = new DoubleAnimation(currentW, targetWidth, duration) { EasingFunction = ease };
            var hAnim = new DoubleAnimation(currentH, targetHeight, duration) { EasingFunction = ease };

            ShapeRoot.BeginAnimation(WidthProperty, wAnim);
            ShapeRoot.BeginAnimation(HeightProperty, hAnim);
        }

        #endregion

        #region Controls & Hotkeys

        private void ShapeRoot_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (isBrightnessHudActive)
            {
                int delta = e.Delta > 0 ? 5 : -5;
                int newB = Math.Clamp(displayedBrightnessLevel + delta, 0, 100);
                lastKnownBrightness = newB;
                SetWindowsBrightness(newB);
                TriggerBrightnessHUD(newB);
            }
            else
            {
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                float currentVol = lastKnownVolume >= 0 ? lastKnownVolume : (displayedVolumeLevel / 100f);
                float newVol = Math.Clamp(currentVol + delta, 0f, 1f);
                SetMasterVolume(newVol);
            }
        }

        private void ShapeRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isExpanded = !isExpanded;
            if (isExpanded && currentExpandedTab == ExpandedActivityTab.Home)
            {
                if (MediaSessionManager.Instance.HasActiveSession)
                    currentExpandedTab = ExpandedActivityTab.Music;
                else if (AppleTimerManager.Instance.IsActive)
                    currentExpandedTab = ExpandedActivityTab.Timer;
                else
                    currentExpandedTab = ExpandedActivityTab.Shelf;
            }
            if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive)
            {
                UpdateIndicatorVisuals();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.System && (e.SystemKey == Key.F4 || e.SystemKey == Key.F11))
            {
                if (e.SystemKey == Key.F11)
                {
                    ToggleMode();
                }
                e.Handled = true; // Shield: Consume Alt+F4 completely
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
            {
                // Ctrl+V: Paste from clipboard (Files, Images, Web URLs, Text) directly into Dock Shelf!
                DockShelfManager.Instance.PasteFromClipboard();
                currentExpandedTab = ExpandedActivityTab.Shelf;
                isExpanded = true;
                UpdateIndicatorVisuals();
                e.Handled = true;
            }
            else if (e.Key == Key.Tab || e.Key == Key.F11)
            {
                // Tab / F11: Toggle Notch vs Dynamic Island Mode
                ToggleMode();
            }
            else if (e.Key == Key.Escape)
            {
                isExpanded = false;
                if (!isVolumeHudActive && !isDndHudActive && !isBrightnessHudActive && !isBluetoothHudActive && !isAirDropHudActive) UpdateIndicatorVisuals();
            }
        }

        #endregion
    }

    #region CoreAudio Native COM Interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
        [FieldOffset(8)] public uint uintVal;
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, ref PROPERTYKEY pKey, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, ref PROPERTYKEY pKey, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient
    {
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    internal static class NativeMethods
    {
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PROPVARIANT pvar);
    }

    internal static class WindowsDndNative
    {
        [DllImport("ntdll.dll")]
        private static extern int ZwQueryWnfStateData(
            ref ulong StateName,
            IntPtr TypeId,
            IntPtr ExplicitScope,
            out uint ChangeStamp,
            IntPtr Buffer,
            ref uint BufferSize);

        private static ulong WNF_SHEL_QUIETHOURS_ACTIVE = 0xD83063EA3BE58C35;
        private static ulong WNF_SHEL_QUIET_HOURS_STATUS = 0xD83063EA3BF1C035;

        public static bool GetWindows11DndActive()
        {
            // 1. Try Windows 11 WNF State (Active)
            try
            {
                uint bufferSize = 4;
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    int status = ZwQueryWnfStateData(ref WNF_SHEL_QUIETHOURS_ACTIVE, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref bufferSize);
                    if (status == 0 && bufferSize >= 4)
                    {
                        int val = Marshal.ReadInt32(buffer);
                        if (val != 0) return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }

            // 2. Try Windows 11 WNF State (Status)
            try
            {
                uint bufferSize = 4;
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    int status = ZwQueryWnfStateData(ref WNF_SHEL_QUIET_HOURS_STATUS, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref bufferSize);
                    if (status == 0 && bufferSize >= 4)
                    {
                        int val = Marshal.ReadInt32(buffer);
                        if (val != 0) return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }

            // 3. Try Windows 10/11 Notification Settings Registry
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
                if (key != null)
                {
                    var val = key.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED");
                    if (val is int intVal && intVal == 0) return true;
                }
            }
            catch { }

            return false;
        }
    }

    internal static class BluetoothDisconnectHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public bool fConnected;
            public bool fRemembered;
            public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            public bool fReturnAuthenticated;
            public bool fReturnRemembered;
            public bool fReturnUnknown;
            public bool fReturnConnected;
            public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        public static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO deviceInfo);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern uint BluetoothDisconnectDevice(IntPtr hRadio, ref BLUETOOTH_ADDRESS pbtdi);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public static bool DisconnectDevice(string deviceName)
        {
            IntPtr hFindRadio = IntPtr.Zero;
            IntPtr hRadio = IntPtr.Zero;
            try
            {
                var rParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                hFindRadio = BluetoothFindFirstRadio(ref rParams, out hRadio);
                if (hRadio == IntPtr.Zero) return false;

                var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS)),
                    fReturnAuthenticated = true,
                    fReturnRemembered = true,
                    fReturnUnknown = true,
                    fReturnConnected = true,
                    fIssueInquiry = false,
                    hRadio = hRadio
                };

                var deviceInfo = new BLUETOOTH_DEVICE_INFO
                {
                    dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO))
                };

                string cleanSearch = (deviceName ?? "").ToLowerInvariant().Trim();

                IntPtr hFind = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
                if (hFind != IntPtr.Zero)
                {
                    try
                    {
                        do
                        {
                            string dName = (deviceInfo.szName ?? "").ToLowerInvariant().Trim();
                            if (deviceInfo.fConnected && (dName.Contains(cleanSearch) || cleanSearch.Contains(dName) ||
                                (cleanSearch.Contains("rockerz") && dName.Contains("rockerz")) ||
                                (cleanSearch.Contains("airdopes") && dName.Contains("airdopes"))))
                            {
                                var addr = new BLUETOOTH_ADDRESS { ullLong = deviceInfo.Address };
                                uint res = BluetoothDisconnectDevice(hRadio, ref addr);
                                return res == 0;
                            }
                        }
                        while (BluetoothFindNextDevice(hFind, ref deviceInfo));
                    }
                    finally
                    {
                        BluetoothFindDeviceClose(hFind);
                    }
                }
            }
            catch { }
            finally
            {
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
                if (hFindRadio != IntPtr.Zero) BluetoothFindRadioClose(hFindRadio);
            }
            return false;
        }
    }

    #endregion
}