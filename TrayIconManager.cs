using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland
{
    public class TrayIconManager : IDisposable
    {
        private const int WM_USER = 0x0400;
        private const int WM_TRAYICON = WM_USER + 100;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly MainWindow mainWindow;
        private IntPtr windowHandle;
        private HwndSource? hwndSource;
        private NOTIFYICONDATA notifyIconData;
        private IntPtr iconHandle = IntPtr.Zero;
        private bool isAdded = false;

        public TrayIconManager(MainWindow main)
        {
            mainWindow = main;
        }

        public void Initialize()
        {
            try
            {
                var helper = new WindowInteropHelper(mainWindow);
                windowHandle = helper.Handle;

                hwndSource = HwndSource.FromHwnd(windowHandle);
                hwndSource?.AddHook(WndProc);

                iconHandle = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION fallback

                notifyIconData = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                    hWnd = windowHandle,
                    uID = 1001,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_TRAYICON,
                    hIcon = iconHandle,
                    szTip = "Dynamic Island Windows"
                };

                isAdded = Shell_NotifyIcon(NIM_ADD, ref notifyIconData);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayMenu();
                    handled = true;
                }
                else if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    mainWindow.OpenSettingsWindow();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowTrayMenu()
        {
            SetForegroundWindow(windowHandle);

            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.8)
            };

            var titleItem = new MenuItem
            {
                Header = "🏝️ Dynamic Island",
                FontWeight = FontWeights.Bold,
                IsEnabled = false,
                Foreground = Brushes.White
            };
            menu.Items.Add(titleItem);
            menu.Items.Add(new Separator());

            var modeItem = new MenuItem { Header = "🔄 Switch Mode (F11)", Foreground = Brushes.White };
            modeItem.Click += (s, e) => mainWindow.ToggleMode();
            menu.Items.Add(modeItem);

            var settingsItem = new MenuItem { Header = "⚙️ Settings...", Foreground = Brushes.White };
            settingsItem.Click += (s, e) => mainWindow.OpenSettingsWindow();
            menu.Items.Add(settingsItem);

            var ramItem = new MenuItem { Header = "🧹 Optimize RAM", Foreground = Brushes.White };
            ramItem.Click += (s, e) => mainWindow.OptimizeMemory();
            menu.Items.Add(ramItem);

            menu.Items.Add(new Separator());

            var callVoiceItem = new MenuItem { Header = "📞 Preview WhatsApp Voice Call", Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0xC0, 0x58)) };
            callVoiceItem.Click += (s, e) => DynamicIsland.Call.WhatsAppCallManager.Instance.SimulateTestCall("Mata Shri 👼", false);
            menu.Items.Add(callVoiceItem);

            var callVideoItem = new MenuItem { Header = "📹 Preview WhatsApp Video Call", Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0xC0, 0x58)) };
            callVideoItem.Click += (s, e) => DynamicIsland.Call.WhatsAppCallManager.Instance.SimulateTestCall("Mata Shri 👼", true);
            menu.Items.Add(callVideoItem);

            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "❌ Exit Dynamic Island", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) };
            exitItem.Click += (s, e) => mainWindow.CloseApp();
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        public void Dispose()
        {
            if (isAdded)
            {
                Shell_NotifyIcon(NIM_DELETE, ref notifyIconData);
                isAdded = false;
            }
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
                iconHandle = IntPtr.Zero;
            }
            hwndSource?.RemoveHook(WndProc);
        }
    }
}
