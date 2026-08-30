using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace DynamicIsland.Network
{
    public class NetworkSpeedManager
    {
        private static NetworkSpeedManager? instance;
        public static NetworkSpeedManager Instance => instance ??= new NetworkSpeedManager();

        private readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
        private long lastBytesReceived = 0;
        private long lastBytesSent = 0;
        private DateTime lastCheckTime = DateTime.UtcNow;
        private bool isFirstTick = true;

        public double DownloadSpeedBytesPerSec { get; private set; }
        public double UploadSpeedBytesPerSec { get; private set; }
        public string FormattedDownloadSpeed { get; private set; } = "0.0 KB/s";
        public string FormattedUploadSpeed { get; private set; } = "0.0 KB/s";
        public string FormattedTotalSpeed { get; private set; } = "0.0 KB/s";
        public string ActiveInterfaceName { get; private set; } = "Wi-Fi";
        public bool IsConnected { get; private set; } = true;

        public event Action? OnSpeedUpdated;

        public NetworkSpeedManager()
        {
            try
            {
                NetworkChange.NetworkAvailabilityChanged += (s, e) => MeasureSpeed();
                NetworkChange.NetworkAddressChanged += (s, e) => MeasureSpeed();
            }
            catch { }

            timer.Interval = TimeSpan.FromMilliseconds(2500);
            timer.Tick += Timer_Tick;
            timer.Start();
            MeasureSpeed();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            MeasureSpeed();
        }

        private void MeasureSpeed()
        {
            try
            {
                long currentBytesReceived = 0;
                long currentBytesSent = 0;

                var allInterfaces = NetworkInterface.GetAllNetworkInterfaces();

                // Find active physical interfaces (Wi-Fi or Ethernet)
                var activePhysical = allInterfaces
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                 (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                  ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                  ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) &&
                                 !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool networkAvailable = NetworkInterface.GetIsNetworkAvailable();
                IsConnected = networkAvailable && activePhysical.Count > 0;

                if (IsConnected)
                {
                    var primary = activePhysical.FirstOrDefault(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) 
                               ?? activePhysical.FirstOrDefault();

                    if (primary != null)
                    {
                        ActiveInterfaceName = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                    }
                    else
                    {
                        ActiveInterfaceName = "Wi-Fi";
                    }

                    foreach (var ni in activePhysical)
                    {
                        var stats = ni.GetIPv4Statistics();
                        currentBytesReceived += stats.BytesReceived;
                        currentBytesSent += stats.BytesSent;
                    }

                    var now = DateTime.UtcNow;
                    var elapsedSeconds = (now - lastCheckTime).TotalSeconds;

                    if (!isFirstTick && elapsedSeconds > 0)
                    {
                        long rxDiff = currentBytesReceived - lastBytesReceived;
                        long txDiff = currentBytesSent - lastBytesSent;

                        if (rxDiff < 0) rxDiff = 0;
                        if (txDiff < 0) txDiff = 0;

                        DownloadSpeedBytesPerSec = rxDiff / elapsedSeconds;
                        UploadSpeedBytesPerSec = txDiff / elapsedSeconds;
                        double totalSpeed = DownloadSpeedBytesPerSec + UploadSpeedBytesPerSec;

                        FormattedDownloadSpeed = FormatSpeed(DownloadSpeedBytesPerSec);
                        FormattedUploadSpeed = FormatSpeed(UploadSpeedBytesPerSec);
                        FormattedTotalSpeed = FormatSpeed(totalSpeed);
                    }

                    lastBytesReceived = currentBytesReceived;
                    lastBytesSent = currentBytesSent;
                    lastCheckTime = now;
                    isFirstTick = false;
                }
                else
                {
                    ActiveInterfaceName = "Wi-Fi";
                    DownloadSpeedBytesPerSec = 0;
                    UploadSpeedBytesPerSec = 0;
                    FormattedDownloadSpeed = "Offline";
                    FormattedUploadSpeed = "0.0 KB/s";
                    FormattedTotalSpeed = "Offline";
                    isFirstTick = true;
                }

                OnSpeedUpdated?.Invoke();
            }
            catch
            {
                IsConnected = false;
                ActiveInterfaceName = "Wi-Fi";
                FormattedDownloadSpeed = "Offline";
                FormattedUploadSpeed = "0.0 KB/s";
                FormattedTotalSpeed = "Offline";
                OnSpeedUpdated?.Invoke();
            }
        }

        public static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024)
            {
                return $"{bytesPerSec:0} B/s";
            }
            else if (bytesPerSec < 1024 * 1024)
            {
                double kb = bytesPerSec / 1024.0;
                return kb >= 100 ? $"{kb:0} KB/s" : $"{kb:0.0} KB/s";
            }
            else
            {
                double mb = bytesPerSec / (1024.0 * 1024.0);
                return $"{mb:0.0} MB/s";
            }
        }
    }
}
