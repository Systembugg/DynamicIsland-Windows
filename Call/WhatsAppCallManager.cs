using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Call
{
    public class WhatsAppCallManager
    {
        private static WhatsAppCallManager? _instance;
        public static WhatsAppCallManager Instance => _instance ??= new WhatsAppCallManager();

        public CallInfo? CurrentCall { get; private set; }
        public bool IsCallActive => CurrentCall != null && CurrentCall.State != CallState.None && CurrentCall.State != CallState.Ended;

        public event Action<CallInfo>? OnIncomingCall;
        public event Action<CallInfo>? OnCallAnswered;
        public event Action<CallInfo>? OnCallEnded;
        public event Action<TimeSpan>? OnDurationTick;

        private UserNotificationListener? _listener;
        private readonly DispatcherTimer _durationTimer;
        private readonly DispatcherTimer _processWatcherTimer;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        public WhatsAppCallManager()
        {
            _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _durationTimer.Tick += (s, e) =>
            {
                if (CurrentCall != null && (CurrentCall.State == CallState.OngoingVoice || CurrentCall.State == CallState.OngoingVideo))
                {
                    OnDurationTick?.Invoke(CurrentCall.Duration);
                }
            };

            _processWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _processWatcherTimer.Tick += (s, e) => CheckWhatsAppCallWindows();

            Task.Run(async () =>
            {
                await InitializeNotificationListenerAsync();
            });
        }

        private async Task InitializeNotificationListenerAsync()
        {
            try
            {
                _listener = UserNotificationListener.Current;
                if (_listener != null)
                {
                    var status = await _listener.RequestAccessAsync();
                    if (status == UserNotificationListenerAccessStatus.Allowed)
                    {
                        _listener.NotificationChanged += Listener_NotificationChanged;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CallManager] Notification listener init error: {ex.Message}");
            }
        }

        private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                if (args.ChangeKind == UserNotificationChangedKind.Added)
                {
                    var notification = sender.GetNotification(args.UserNotificationId);
                    if (notification != null)
                    {
                        ProcessNotification(notification);
                    }
                }
            }
            catch { }
        }

        private void ProcessNotification(UserNotification notification)
        {
            try
            {
                string appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
                string appId = notification.AppInfo?.AppUserModelId ?? "";

                if (!appName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) &&
                    !appId.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var toast = notification.Notification;
                if (toast?.Visual == null) return;

                var binding = toast.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return;

                var textElements = binding.GetTextElements().Select(t => t.Text).ToList();
                if (textElements.Count == 0) return;

                string combined = string.Join(" ", textElements).ToLowerInvariant();

                bool isVoiceCall = combined.Contains("incoming voice call") || combined.Contains("voice call") || combined.Contains("incoming call");
                bool isVideoCall = combined.Contains("incoming video call") || combined.Contains("video call");

                if (isVoiceCall || isVideoCall)
                {
                    string caller = textElements[0];
                    if (textElements.Count > 1 && (caller.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) || caller.Contains("Call", StringComparison.OrdinalIgnoreCase)))
                    {
                        caller = textElements[1];
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        TriggerIncomingCall(caller, isVideoCall ? CallType.Video : CallType.Voice, notification.Id);
                    });
                }
            }
            catch { }
        }

        public void TriggerIncomingCall(string callerName, CallType type, uint? notificationId = null)
        {
            CurrentCall = new CallInfo
            {
                CallerName = string.IsNullOrWhiteSpace(callerName) ? "WhatsApp Caller" : callerName,
                Subtitle = type == CallType.Video ? "WhatsApp Video" : "WhatsApp Audio",
                AppName = "WhatsApp",
                Type = type,
                State = CallState.Incoming,
                StartTime = DateTime.UtcNow,
                NotificationId = notificationId
            };

            _processWatcherTimer.Start();
            OnIncomingCall?.Invoke(CurrentCall);
        }

        public void AcceptCall()
        {
            if (CurrentCall == null) return;

            CurrentCall.State = CurrentCall.Type == CallType.Video ? CallState.OngoingVideo : CallState.OngoingVoice;
            CurrentCall.StartTime = DateTime.UtcNow;

            _durationTimer.Start();

            // Bring WhatsApp to foreground or activate window
            ActivateWhatsAppWindow();

            OnCallAnswered?.Invoke(CurrentCall);
        }

        public void DeclineCall()
        {
            if (CurrentCall == null) return;

            CurrentCall.State = CallState.Ended;
            _durationTimer.Stop();

            CloseWhatsAppCallWindow();

            OnCallEnded?.Invoke(CurrentCall);

            Task.Delay(1200).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CurrentCall = null;
                });
            });
        }

        public void EndCall()
        {
            DeclineCall();
        }

        private void CheckWhatsAppCallWindows()
        {
            if (CurrentCall == null) return;

            IntPtr callHwnd = FindWhatsAppCallWindow();

            if (CurrentCall.State == CallState.Incoming)
            {
                if (callHwnd != IntPtr.Zero)
                {
                    // Window appeared / call connected
                    AcceptCall();
                }
            }
            else if (CurrentCall.State == CallState.OngoingVoice || CurrentCall.State == CallState.OngoingVideo)
            {
                if (callHwnd == IntPtr.Zero && (DateTime.UtcNow - CurrentCall.StartTime).TotalSeconds > 4)
                {
                    // Call window closed -> call finished
                    EndCall();
                    _processWatcherTimer.Stop();
                }
            }
        }

        private IntPtr FindWhatsAppCallWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        if (proc.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                        {
                            var sb = new StringBuilder(256);
                            GetWindowText(hWnd, sb, 256);
                            string title = sb.ToString();
                            if (title.Contains("Call", StringComparison.OrdinalIgnoreCase) || 
                                title.Contains("WhatsApp Call", StringComparison.OrdinalIgnoreCase))
                            {
                                found = hWnd;
                                return false;
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private void ActivateWhatsAppWindow()
        {
            IntPtr hwnd = FindWhatsAppCallWindow();
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }

        private void CloseWhatsAppCallWindow()
        {
            IntPtr hwnd = FindWhatsAppCallWindow();
            if (hwnd != IntPtr.Zero)
            {
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void SimulateTestCall(string callerName = "Tamia Castle", bool isVideo = false)
        {
            TriggerIncomingCall(callerName, isVideo ? CallType.Video : CallType.Voice);
        }
    }
}
