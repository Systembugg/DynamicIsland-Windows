using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
        private readonly DispatcherTimer _scanTimer;

        private AutomationElement? _cachedAcceptButton;
        private AutomationElement? _cachedDeclineButton;
        private IntPtr _cachedCallHwnd = IntPtr.Zero;

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

            // Background UI Automation scanner (3000ms — slowed down to minimize CPU cost of tree walks)
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
            _scanTimer.Tick += (s, e) => Task.Run(() => ScanForWhatsAppCall());
            _scanTimer.Start();

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

        private void ScanForWhatsAppCall()
        {
            try
            {
                // GPU Optimization: Skip expensive UI Automation tree walk if no WhatsApp process is running
                bool hasWhatsApp = false;
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                        {
                            hasWhatsApp = true;
                            break;
                        }
                    }
                    finally { proc.Dispose(); }
                }
                if (!hasWhatsApp)
                {
                    if (IsCallActive) EndCall();
                    return;
                }

                var root = AutomationElement.RootElement;
                if (root == null) return;

                var condWindow = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
                var windows = root.FindAll(TreeScope.Children, condWindow);

                bool foundCall = false;

                foreach (AutomationElement win in windows)
                {
                    try
                    {
                        int pid = win.Current.ProcessId;
                        string winTitle = win.Current.Name ?? "";

                        bool isWhatsApp = false;
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                                proc.ProcessName.Contains("msedgewebview", StringComparison.OrdinalIgnoreCase))
                            {
                                isWhatsApp = true;
                            }
                        }
                        catch { }

                        if (!isWhatsApp && !winTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Search for Buttons inside this window
                        var condButtons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                        var buttons = win.FindAll(TreeScope.Descendants, condButtons);

                        AutomationElement? btnAccept = null;
                        AutomationElement? btnDecline = null;

                        foreach (AutomationElement btn in buttons)
                        {
                            string btnName = btn.Current.Name ?? "";
                            if (btnName.Equals("Accept", StringComparison.OrdinalIgnoreCase) || btnName.Contains("Accept", StringComparison.OrdinalIgnoreCase))
                            {
                                btnAccept = btn;
                            }
                            else if (btnName.Equals("Decline", StringComparison.OrdinalIgnoreCase) || btnName.Contains("Decline", StringComparison.OrdinalIgnoreCase) || btnName.Contains("End call", StringComparison.OrdinalIgnoreCase))
                            {
                                btnDecline = btn;
                            }
                        }

                        if (btnAccept != null && btnDecline != null)
                        {
                            // INCOMING CALL WINDOW FOUND!
                            foundCall = true;
                            _cachedAcceptButton = btnAccept;
                            _cachedDeclineButton = btnDecline;
                            _cachedCallHwnd = new IntPtr(win.Current.NativeWindowHandle);

                            // Extract Caller Name & Call Type from Text elements
                            var condText = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
                            var textElements = win.FindAll(TreeScope.Descendants, condText);

                            string callerName = "WhatsApp Caller";
                            CallType callType = CallType.Voice;

                            string[] ignoreKeywords = new[] { "accept", "decline", "whatsapp", "voice call", "video call", "calling", "call", "end call", "mute", "unmute", "camera", "microphone", "more", "options", "settings", "close", "minimize", "maximize" };
                            string bestCallerName = "";

                            foreach (AutomationElement txt in textElements)
                            {
                                string t = (txt.Current.Name ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(t)) continue;

                                if (t.Contains("Video", StringComparison.OrdinalIgnoreCase))
                                {
                                    callType = CallType.Video;
                                }

                                bool isIgnore = false;
                                foreach (var ign in ignoreKeywords)
                                {
                                    if (t.Equals(ign, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isIgnore = true;
                                        break;
                                    }
                                }

                                if (!isIgnore && !t.Contains("Voice", StringComparison.OrdinalIgnoreCase) && !t.Contains("Video", StringComparison.OrdinalIgnoreCase) && !t.Contains("Call", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (string.IsNullOrEmpty(bestCallerName) || bestCallerName.Length < t.Length)
                                    {
                                        bestCallerName = t;
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(bestCallerName))
                            {
                                callerName = bestCallerName;
                            }

                            if (CurrentCall == null || CurrentCall.State != CallState.Incoming || CurrentCall.CallerName != callerName)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    TriggerIncomingCall(callerName, callType);
                                });
                            }
                            return;
                        }
                        else if (btnDecline != null && btnAccept == null)
                        {
                            // ONGOING CALL (Connected)
                            foundCall = true;
                            _cachedDeclineButton = btnDecline;
                            _cachedCallHwnd = new IntPtr(win.Current.NativeWindowHandle);

                            if (CurrentCall == null || CurrentCall.State == CallState.Incoming || CurrentCall.State == CallState.None)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    if (CurrentCall == null)
                                    {
                                        CurrentCall = new CallInfo
                                        {
                                            CallerName = "WhatsApp Caller",
                                            Subtitle = "WhatsApp Audio",
                                            State = CallState.OngoingVoice,
                                            StartTime = DateTime.UtcNow
                                        };
                                    }
                                    AcceptCall();
                                });
                            }
                            return;
                        }
                    }
                    catch { }
                }

                if (!foundCall && !IsPreviewMode && CurrentCall != null && (CurrentCall.State == CallState.OngoingVoice || CurrentCall.State == CallState.OngoingVideo || CurrentCall.State == CallState.Incoming))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        EndCall();
                    });
                }
            }
            catch { }
        }

        public bool IsPreviewMode { get; set; } = false;

        public void TriggerIncomingCall(string callerName, CallType type, uint? notificationId = null)
        {
            CurrentCall = new CallInfo
            {
                CallerName = string.IsNullOrWhiteSpace(callerName) ? "Tamia Castle" : callerName,
                Subtitle = type == CallType.Video ? "WhatsApp Video" : "WhatsApp Audio",
                AppName = "WhatsApp",
                Type = type,
                State = CallState.Incoming,
                StartTime = DateTime.UtcNow,
                NotificationId = notificationId
            };

            OnIncomingCall?.Invoke(CurrentCall);
        }

        public void AcceptCall()
        {
            if (CurrentCall == null) return;

            CurrentCall.State = CurrentCall.Type == CallType.Video ? CallState.OngoingVideo : CallState.OngoingVoice;
            CurrentCall.StartTime = DateTime.UtcNow;

            _durationTimer.Start();

            // Programmatically invoke WhatsApp Accept button if real call
            try
            {
                if (_cachedAcceptButton != null)
                {
                    var invoker = _cachedAcceptButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                    invoker?.Invoke();
                }
                else if (_cachedCallHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(_cachedCallHwnd);
                }
            }
            catch { }

            OnCallAnswered?.Invoke(CurrentCall);
        }

        public void DeclineCall()
        {
            if (CurrentCall == null) return;

            CurrentCall.State = CallState.Ended;
            _durationTimer.Stop();

            // Programmatically invoke WhatsApp Decline button if real call
            try
            {
                if (_cachedDeclineButton != null)
                {
                    var invoker = _cachedDeclineButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                    invoker?.Invoke();
                }
                else if (_cachedCallHwnd != IntPtr.Zero)
                {
                    PostMessage(_cachedCallHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }

            OnCallEnded?.Invoke(CurrentCall);

            Task.Delay(1200).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CurrentCall = null;
                    IsPreviewMode = false;
                });
            });
        }

        public void EndCall()
        {
            DeclineCall();
        }

        public void SimulateIncomingCall(string callerName = "Tamia Castle", bool isVideo = false)
        {
            IsPreviewMode = true;
            TriggerIncomingCall(callerName, isVideo ? CallType.Video : CallType.Voice);
        }

        public void SimulateOngoingVoiceCall(string callerName = "Tamia Castle")
        {
            IsPreviewMode = true;
            CurrentCall = new CallInfo
            {
                CallerName = callerName,
                Subtitle = "WhatsApp Audio",
                AppName = "WhatsApp",
                Type = CallType.Voice,
                State = CallState.OngoingVoice,
                StartTime = DateTime.UtcNow.AddSeconds(-48) // Starts at 00:48 matching SVG 2
            };
            _durationTimer.Start();
            OnCallAnswered?.Invoke(CurrentCall);
        }

        public void SimulateOngoingVideoCall(string callerName = "Tamia Castle")
        {
            IsPreviewMode = true;
            CurrentCall = new CallInfo
            {
                CallerName = callerName,
                Subtitle = "WhatsApp Video",
                AppName = "WhatsApp",
                Type = CallType.Video,
                State = CallState.OngoingVideo,
                StartTime = DateTime.UtcNow.AddSeconds(-48)
            };
            _durationTimer.Start();
            OnCallAnswered?.Invoke(CurrentCall);
        }

        public void ResetCall()
        {
            IsPreviewMode = false;
            if (CurrentCall != null)
            {
                CurrentCall.State = CallState.Ended;
                _durationTimer.Stop();
                OnCallEnded?.Invoke(CurrentCall);
                CurrentCall = null;
            }
        }
    }
}
