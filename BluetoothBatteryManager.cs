using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace DynamicIsland
{
    public class BluetoothDeviceInfo
    {
        public string Name { get; set; } = "Bluetooth Device";
        public string CleanName { get; set; } = "Bluetooth Device";
        public AudioDeviceCategory Category { get; set; } = AudioDeviceCategory.WirelessHeadphones;
        public int? BatteryLevel { get; set; } = null; // null if not reported
        public bool IsConnected { get; set; } = false;
        public string DeviceId { get; set; } = "";
    }

    public class BluetoothBatteryManager
    {
        private static BluetoothBatteryManager? _instance;
        public static BluetoothBatteryManager Instance => _instance ??= new BluetoothBatteryManager();

        public BluetoothDeviceInfo? PrimaryConnectedDevice { get; private set; }
        public List<BluetoothDeviceInfo> ConnectedDevices { get; private set; } = new();

        public DateTimeOffset? LastUpdateTimestamp { get; private set; }

        public void TouchActivity()
        {
            LastUpdateTimestamp = DateTimeOffset.Now;
        }

        public bool ShouldShowCompactBluetooth => ConnectedDevices.Count > 0 && (DateTimeOffset.Now - LastUpdateTimestamp.GetValueOrDefault(DateTimeOffset.MinValue)).TotalSeconds < 20;

        public event Action? DevicesUpdated;
        public event Action<string, AudioDeviceCategory, int?>? DeviceConnected;

        private HashSet<string> _previousConnectedNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialScan = true;

        #region SetupAPI Native PnP Engine

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_BatteryLevel = new DEVPROPKEY
        {
            fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
            pid = 2
        };

        private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new DEVPROPKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        private static readonly DEVPROPKEY DEVPKEY_NAME = new DEVPROPKEY
        {
            fmtid = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
            pid = 10
        };

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            byte[]? PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize,
            uint Flags);

        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint DIGCF_PRESENT = 0x00000002;

        public static Dictionary<string, int> ScanPnpBatteries()
        {
            var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] enumerators = new[] { "BTHENUM", "BTHHFENUM", "BTH", "BTHLE" };

            foreach (var enumerator in enumerators)
            {
                IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
                if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
                {
                    continue;
                }

                try
                {
                    uint index = 0;
                    var devInfoData = new SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)Marshal.SizeOf(devInfoData);

                    while (SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData))
                    {
                        index++;
                        string? name = GetStringProp(devInfoSet, ref devInfoData, DEVPKEY_Device_FriendlyName)
                                       ?? GetStringProp(devInfoSet, ref devInfoData, DEVPKEY_NAME);

                        if (string.IsNullOrWhiteSpace(name)) continue;

                        byte? battery = GetByteProp(devInfoSet, ref devInfoData, DEVPKEY_Device_BatteryLevel);

                        if (battery.HasValue)
                        {
                            string clean = CleanDeviceName(name);
                            results[clean] = battery.Value;
                            results[name] = battery.Value;
                        }
                    }
                }
                catch { }
                finally
                {
                    SetupDiDestroyDeviceInfoList(devInfoSet);
                }
            }

            return results;
        }

        private static string? GetStringProp(IntPtr hDevInfo, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            try
            {
                uint propType, reqSize;
                SetupDiGetDevicePropertyW(hDevInfo, ref data, ref key, out propType, null, 0, out reqSize, 0);
                if (reqSize == 0) return null;

                byte[] buf = new byte[reqSize];
                if (SetupDiGetDevicePropertyW(hDevInfo, ref data, ref key, out propType, buf, reqSize, out reqSize, 0))
                {
                    return Encoding.Unicode.GetString(buf).TrimEnd('\0');
                }
            }
            catch { }
            return null;
        }

        private static byte? GetByteProp(IntPtr hDevInfo, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            try
            {
                uint propType, reqSize;
                SetupDiGetDevicePropertyW(hDevInfo, ref data, ref key, out propType, null, 0, out reqSize, 0);
                if (reqSize == 0) return null;

                byte[] buf = new byte[reqSize];
                if (SetupDiGetDevicePropertyW(hDevInfo, ref data, ref key, out propType, buf, reqSize, out reqSize, 0))
                {
                    if (buf.Length > 0) return buf[0];
                }
            }
            catch { }
            return null;
        }

        #endregion

        private static readonly object _batteryLock = new object();
        private static Dictionary<string, int> _cachedBatteries = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Threading.Timer? _backgroundPoller;

        public BluetoothBatteryManager()
        {
            // Battery percentage changes slowly; real-time connects/disconnects are handled with 0 latency via WM_DEVICECHANGE
            _backgroundPoller = new System.Threading.Timer(_ =>
            {
                Task.Run(async () => await RefreshDevicesAsync());
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(25));
        }

        public async Task RefreshDevicesAsync()
        {
            try
            {
                var list = new List<BluetoothDeviceInfo>();

                // 1. Scan real-time PnP battery levels from Windows kernel on background thread
                var pnpBatteries = await Task.Run(() => ScanPnpBatteries());
                lock (_batteryLock)
                {
                    _cachedBatteries = pnpBatteries;
                }

                // 2. Query only currently connected Bluetooth devices (Instant query)
                DeviceInformationCollection? deviceInfos = null;
                try
                {
                    deviceInfos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
                }
                catch
                {
                    try
                    {
                        deviceInfos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
                    }
                    catch { }
                }

                if (deviceInfos != null)
                {
                    foreach (var info in deviceInfos)
                    {
                        string name = info.Name ?? "Bluetooth Device";
                        string clean = CleanDeviceName(name);
                        var category = DetectCategory(name);

                        int? battery = null;
                        if (pnpBatteries.TryGetValue(clean, out int b1)) battery = b1;
                        else if (pnpBatteries.TryGetValue(name, out int b2)) battery = b2;
                        else
                        {
                            foreach (var kvp in pnpBatteries)
                            {
                                if (kvp.Key.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                                    clean.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    battery = kvp.Value;
                                    break;
                                }
                            }
                        }

                        list.Add(new BluetoothDeviceInfo
                        {
                            DeviceId = info.Id,
                            Name = name,
                            CleanName = clean,
                            Category = category,
                            BatteryLevel = battery,
                            IsConnected = true
                        });
                    }
                }

                // Check for newly connected devices and trigger popup event
                if (!_isInitialScan)
                {
                    foreach (var dev in list)
                    {
                        if (!_previousConnectedNames.Contains(dev.CleanName) && !_previousConnectedNames.Contains(dev.Name))
                        {
                            DeviceConnected?.Invoke(dev.CleanName, dev.Category, dev.BatteryLevel);
                        }
                    }
                }
                else
                {
                    _isInitialScan = false;
                }

                _previousConnectedNames = list.Select(d => d.CleanName).Concat(list.Select(d => d.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                ConnectedDevices = list;
                PrimaryConnectedDevice = list.FirstOrDefault();
                TouchActivity();

                DevicesUpdated?.Invoke();
            }
            catch { }
        }

        public int? GetBatteryForDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return null;

            string clean = CleanDeviceName(deviceName);
            Dictionary<string, int> snapshot;
            lock (_batteryLock)
            {
                snapshot = _cachedBatteries;
            }

            if (snapshot.TryGetValue(clean, out int b1)) return b1;
            if (snapshot.TryGetValue(deviceName, out int b2)) return b2;

            foreach (var kvp in snapshot)
            {
                if (kvp.Key.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        public static AudioDeviceCategory DetectCategory(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("airpod") || lower.Contains("earbud") || lower.Contains("airdopes") || 
                lower.Contains("buds") || lower.Contains("tws") || lower.Contains("in-ear") || 
                lower.Contains("dots") || lower.Contains("atom") || lower.Contains("nirvana") || 
                lower.Contains("enco") || lower.Contains("duopods") || lower.Contains("truke") || 
                lower.Contains("boult") || lower.Contains("noise") || lower.Contains("realme") || 
                lower.Contains("boat") || lower.Contains("earphone") || lower.Contains("ear (") || 
                lower.Contains("freebuds") || lower.Contains("galaxy buds") || lower.Contains("soundcore"))
            {
                return AudioDeviceCategory.TwsEarbuds;
            }
            if (lower.Contains("speaker") || lower.Contains("soundbar") || lower.Contains("echo") || lower.Contains("nest") || lower.Contains("boom") || lower.Contains("jbl flip") || lower.Contains("jbl charge"))
            {
                return AudioDeviceCategory.InternalSpeakers;
            }
            return AudioDeviceCategory.WirelessHeadphones;
        }

        public static string CleanDeviceName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Bluetooth Audio";

            var match = Regex.Match(raw, @"\(([^)]+)\)");
            string candidate = match.Success ? match.Groups[1].Value.Trim() : raw;

            candidate = Regex.Replace(candidate, @"^\d+-\s*", "");
            candidate = Regex.Replace(candidate, @"\s*\([^)]*(Hands-Free|Avrcp|A2DP|Stereo|Audio|Driver)[^)]*\)", "", RegexOptions.IgnoreCase);
            candidate = candidate.Replace("Avrcp Transport", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("A2DP SNK", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free AG", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free HF", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Hands-Free", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("Stereo", "", StringComparison.OrdinalIgnoreCase)
                                 .Trim();

            return string.IsNullOrWhiteSpace(candidate) ? "Bluetooth Audio" : candidate;
        }

        public static void OpenBluetoothSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:bluetooth",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
