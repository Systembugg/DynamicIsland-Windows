using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DynamicIsland.AirDrop
{
    public class AirDropDiscoveryService
    {
        public static AirDropDiscoveryService Instance { get; } = new AirDropDiscoveryService();

        private const int LocalSendPort = 53317;
        private const string MulticastIp = "224.0.0.167";

        private UdpClient? udpListener;
        private CancellationTokenSource? cts;
        private readonly HttpClient httpClient;

        public ObservableCollection<AirDropDevice> DiscoveredDevices { get; } = new ObservableCollection<AirDropDevice>();

        public event Action? OnDevicesUpdated;
        public bool IsScanning { get; private set; }

        public AirDropDiscoveryService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) };
        }

        public const string DeviceAlias = "infinity";

        public void StartScanning()
        {
            if (IsScanning) return;
            IsScanning = true;
            if (cts == null || cts.IsCancellationRequested) cts = new CancellationTokenSource();

            Application.Current?.Dispatcher.Invoke(() => DiscoveredDevices.Clear());

            Task.Run(() => ListenUdp(cts.Token));
            Task.Run(() => BroadcastPresence(cts.Token));
            Task.Run(() => ScanSubnet(cts.Token));
        }

        public void StartBackgroundBeacon()
        {
            if (cts == null || cts.IsCancellationRequested) cts = new CancellationTokenSource();
            Task.Run(() => ListenUdp(cts.Token));
            Task.Run(() => PeriodicBroadcastLoop(cts.Token));
        }

        private async Task PeriodicBroadcastLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await BroadcastPresence(token);
                try { await Task.Delay(2000, token); } catch { break; }
            }
        }

        public void StopScanning()
        {
            IsScanning = false;
        }

        private async Task ListenUdp(CancellationToken token)
        {
            try
            {
                if (udpListener == null)
                {
                    var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpSocket.Bind(new IPEndPoint(IPAddress.Any, LocalSendPort));
                    udpListener = new UdpClient { Client = udpSocket };

                    var mIp = IPAddress.Parse(MulticastIp);
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        {
                            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                            {
                                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    try { udpListener.JoinMulticastGroup(mIp, addr.Address); } catch { }
                                }
                            }
                        }
                    }
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpListener.ReceiveAsync(token);
                        string json = Encoding.UTF8.GetString(result.Buffer);
                        ParseAndAddDevice(json, result.RemoteEndPoint.Address.ToString());

                        if (json.Contains("\"announce\"") || json.Contains("\"announcement\"") || json.Contains("\"info\""))
                        {
                            _ = Task.Run(() => SendDirectPresence(result.RemoteEndPoint));
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }
            catch { }
        }

        private async Task SendDirectPresence(IPEndPoint targetEp)
        {
            try
            {
                using var client = new UdpClient();
                var payload = new
                {
                    alias = DeviceAlias,
                    version = "2.0",
                    deviceModel = "Windows",
                    deviceType = "desktop",
                    fingerprint = "dynamic-island-" + Environment.MachineName.ToLowerInvariant(),
                    port = LocalSendPort,
                    protocol = "http",
                    download = true,
                    announcement = true,
                    announce = true
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                await client.SendAsync(data, data.Length, targetEp);
            }
            catch { }
        }

        private async Task BroadcastPresence(CancellationToken token)
        {
            try
            {
                using var client = new UdpClient();
                client.EnableBroadcast = true;

                var payload = new
                {
                    alias = DeviceAlias,
                    version = "2.0",
                    deviceModel = "Windows",
                    deviceType = "desktop",
                    fingerprint = "dynamic-island-" + Environment.MachineName.ToLowerInvariant(),
                    port = LocalSendPort,
                    protocol = "http",
                    download = true,
                    announcement = true,
                    announce = true
                };

                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastIp), LocalSendPort);

                for (int i = 0; i < 2 && !token.IsCancellationRequested; i++)
                {
                    try { await client.SendAsync(data, data.Length, multicastEp); } catch { }

                    foreach (var ip in GetLocalIps())
                    {
                        try
                        {
                            var parts = ip.Split('.');
                            if (parts.Length == 4)
                            {
                                var bcast = new IPEndPoint(IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255"), LocalSendPort);
                                await client.SendAsync(data, data.Length, bcast);
                            }
                        }
                        catch { }
                    }

                    await Task.Delay(400, token);
                }
            }
            catch { }
        }

        private async Task ScanSubnet(CancellationToken token)
        {
            try
            {
                var localIps = GetLocalIps();
                var tasks = new System.Collections.Generic.List<Task>();

                foreach (var localIp in localIps)
                {
                    var parts = localIp.Split('.');
                    if (parts.Length != 4) continue;
                    string subnet = $"{parts[0]}.{parts[1]}.{parts[2]}";

                    for (int i = 1; i <= 254; i++)
                    {
                        string targetIp = $"{subnet}.{i}";
                        if (targetIp == localIp) continue;

                        tasks.Add(ProbeDeviceHttp(targetIp, token));
                        if (tasks.Count >= 30)
                        {
                            await Task.WhenAll(tasks);
                            tasks.Clear();
                        }
                    }
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }
            catch { }
        }

        private async Task ProbeDeviceHttp(string ip, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            
            // Try HTTPS first
            try
            {
                string url = $"https://{ip}:{LocalSendPort}/api/localsend/v2/info";
                var res = await httpClient.GetAsync(url, token);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(token);
                    ParseAndAddDevice(json, ip, "https");
                    return;
                }
            }
            catch { }

            // Try HTTP fallback
            try
            {
                string url = $"http://{ip}:{LocalSendPort}/api/localsend/v2/info";
                var res = await httpClient.GetAsync(url, token);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(token);
                    ParseAndAddDevice(json, ip, "http");
                }
            }
            catch { }
        }

        private void ParseAndAddDevice(string json, string ip, string defaultProto = "https")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1" || ip == "::1") return;

                var localIps = GetLocalIps();
                if (localIps.Contains(ip)) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string alias = root.TryGetProperty("alias", out var a) ? a.GetString() ?? "Phone" : "Nearby Device";
                string model = root.TryGetProperty("deviceModel", out var m) ? m.GetString() ?? "Mobile" : "Phone";
                string typeStr = root.TryGetProperty("deviceType", out var t) ? t.GetString() ?? "mobile" : "mobile";
                string proto = root.TryGetProperty("protocol", out var p) ? p.GetString() ?? defaultProto : defaultProto;
                string fingerprint = root.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? "" : "";
                int port = root.TryGetProperty("port", out var pt) ? pt.GetInt32() : LocalSendPort;

                // Ignore self / PC broadcasts (Dynamic Island PC or MachineName)
                if (alias.Equals("Dynamic Island PC", StringComparison.OrdinalIgnoreCase) ||
                    alias.Contains("Dynamic Island", StringComparison.OrdinalIgnoreCase) ||
                    fingerprint.StartsWith("dynamic-island-", StringComparison.OrdinalIgnoreCase) ||
                    alias.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var devType = typeStr.ToLowerInvariant() switch
                {
                    "desktop" or "web" => AirDropDeviceType.Desktop,
                    "tablet" => AirDropDeviceType.Tablet,
                    _ => AirDropDeviceType.Mobile
                };

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var existing = DiscoveredDevices.FirstOrDefault(d => d.IpAddress == ip);
                    if (existing != null)
                    {
                        existing.Alias = alias;
                        existing.DeviceModel = model;
                        existing.DeviceType = devType;
                        existing.Protocol = proto;
                        existing.Fingerprint = fingerprint;
                        existing.LastSeen = DateTime.UtcNow;
                    }
                    else
                    {
                        DiscoveredDevices.Add(new AirDropDevice
                        {
                            Alias = alias,
                            DeviceModel = model,
                            DeviceType = devType,
                            IpAddress = ip,
                            Port = port,
                            Protocol = proto,
                            Fingerprint = fingerprint,
                            LastSeen = DateTime.UtcNow
                        });
                    }
                    OnDevicesUpdated?.Invoke();
                });
            }
            catch { }
        }

        private string[] GetLocalIps()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => a.Address.ToString())
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
