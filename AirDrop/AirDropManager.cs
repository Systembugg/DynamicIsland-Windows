using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.AirDrop
{
    public class AirDropTransferInfo
    {
        public string DeviceName { get; set; } = "Nearby Device";
        public string DeviceType { get; set; } = "mobile";
        public string FileName { get; set; } = "File";
        public string FilePath { get; set; } = "";
        public long TotalBytes { get; set; } = 0;
        public long TransferredBytes { get; set; } = 0;
        public double Progress => TotalBytes > 0 ? Math.Clamp((double)TransferredBytes / TotalBytes, 0.0, 1.0) : 0.0;
        public BitmapSource? Thumbnail { get; set; }
        public bool IsSending { get; set; } = true;
    }

    public class AirDropManager
    {
        public static AirDropManager Instance { get; } = new AirDropManager();

        private readonly FileSystemWatcher? downloadWatcher;
        
        private AirDropState state = AirDropState.Idle;
        private AirDropTransferInfo? currentTransfer;
        private string? pendingFilePath;

        public event Action<AirDropState, AirDropTransferInfo?>? OnTransferStateChanged;
        public event Action<double>? OnProgressUpdated;
        public event Action<string>? OnStatusChanged;

        public AirDropState State => state;
        public AirDropTransferInfo? CurrentTransfer => currentTransfer;
        public string? PendingFilePath => pendingFilePath;

        public AirDropManager()
        {
            try
            {
                string downloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads"
                );

                if (Directory.Exists(downloadsPath))
                {
                    downloadWatcher = new FileSystemWatcher(downloadsPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    downloadWatcher.Created += DownloadWatcher_Created;
                }
            }
            catch { }
        }

        public void PrepareShare(string filePath, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            pendingFilePath = filePath;
            var fileInfo = new FileInfo(filePath);

            currentTransfer = new AirDropTransferInfo
            {
                DeviceName = "Nearby Device",
                DeviceType = "mobile",
                FileName = displayName ?? fileInfo.Name,
                FilePath = filePath,
                TotalBytes = fileInfo.Length,
                TransferredBytes = 0,
                IsSending = true,
                Thumbnail = TryLoadThumbnail(filePath)
            };

            state = AirDropState.ChoosingDevice;
            AirDropDiscoveryService.Instance.StartScanning();
            OnTransferStateChanged?.Invoke(state, currentTransfer);
        }

        public async Task SendToDeviceAsync(AirDropDevice device)
        {
            if (string.IsNullOrEmpty(pendingFilePath) || !File.Exists(pendingFilePath)) return;

            if (currentTransfer != null)
            {
                currentTransfer.DeviceName = device.Alias;
                currentTransfer.DeviceType = "Waiting for phone...";
            }

            state = AirDropState.Transferring;
            OnTransferStateChanged?.Invoke(state, currentTransfer);

            bool success = await AirDropClient.Instance.SendFileAsync(
                device, 
                pendingFilePath,
                statusText =>
                {
                    if (currentTransfer != null)
                    {
                        currentTransfer.DeviceType = statusText;
                    }
                    OnStatusChanged?.Invoke(statusText);
                },
                progress =>
                {
                    if (currentTransfer != null)
                    {
                        currentTransfer.TransferredBytes = (long)(progress * currentTransfer.TotalBytes);
                        currentTransfer.DeviceType = $"Sending • {(int)(progress * 100)}%";
                    }
                    OnProgressUpdated?.Invoke(progress);
                }
            );

            AirDropDiscoveryService.Instance.StopScanning();

            if (success)
            {
                state = AirDropState.Completed;
                OnProgressUpdated?.Invoke(1.0);
                OnTransferStateChanged?.Invoke(state, currentTransfer);
            }
            else
            {
                if (currentTransfer != null)
                {
                    currentTransfer.DeviceType = AirDropClient.Instance.LastError;
                }
                state = AirDropState.Failed;
                OnTransferStateChanged?.Invoke(state, currentTransfer);
            }
        }

        public void CancelTransfer()
        {
            AirDropDiscoveryService.Instance.StopScanning();
            Task.Run(async () => await AirDropClient.Instance.CancelActiveTransferAsync());
            state = AirDropState.Idle;
            currentTransfer = null;
            pendingFilePath = null;
            OnTransferStateChanged?.Invoke(state, null);
        }

        private void DownloadWatcher_Created(object sender, FileSystemEventArgs e)
        {
            try
            {
                string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                if (ext == ".crdownload" || ext == ".tmp" || ext == ".part") return;

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (state == AirDropState.Idle)
                    {
                        var fi = new FileInfo(e.FullPath);
                        currentTransfer = new AirDropTransferInfo
                        {
                            DeviceName = "Nearby Device",
                            DeviceType = "mobile",
                            FileName = e.Name ?? "Incoming File",
                            FilePath = e.FullPath,
                            TotalBytes = fi.Exists ? fi.Length : 2048000,
                            TransferredBytes = fi.Exists ? fi.Length : 2048000,
                            IsSending = false,
                            Thumbnail = TryLoadThumbnail(e.FullPath)
                        };
                        state = AirDropState.Completed;
                        OnTransferStateChanged?.Invoke(state, currentTransfer);
                    }
                });
            }
            catch { }
        }

        public BitmapSource? TryLoadThumbnail(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".webp")
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.DecodePixelWidth = 240;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }
    }
}
