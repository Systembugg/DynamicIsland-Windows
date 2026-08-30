using System;
using System.Windows.Media.Imaging;

namespace DynamicIsland.AirDrop
{
    public enum AirDropDeviceType
    {
        Mobile,
        Desktop,
        Tablet,
        Unknown
    }

    public class AirDropDevice
    {
        public string Alias { get; set; } = "Nearby Device";
        public string DeviceModel { get; set; } = "Phone";
        public AirDropDeviceType DeviceType { get; set; } = AirDropDeviceType.Mobile;
        public string IpAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 53317;
        public string Protocol { get; set; } = "http";
        public string Fingerprint { get; set; } = "";
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;

        public string DisplaySubtitle => !string.IsNullOrWhiteSpace(DeviceModel) ? DeviceModel : (DeviceType == AirDropDeviceType.Mobile ? "Phone" : "Computer");
        
        public string Initials
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Alias)) return "AP";
                var parts = Alias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1) return Alias.Substring(0, Math.Min(2, Alias.Length)).ToUpper();
                return $"{parts[0][0]}{parts[1][0]}".ToUpper();
            }
        }
    }

    public enum AirDropState
    {
        Idle,
        ChoosingDevice,
        Connecting,
        Transferring,
        Completed,
        Failed,
        Cancelled
    }

    public class AirDropIncomingFile
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "File";
        public long Size { get; set; } = 0;
        public string FileType { get; set; } = "";
        public string? Preview { get; set; } = null;
    }

    public class AirDropIncomingRequest
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public string SenderAlias { get; set; } = "Nearby Device";
        public string DeviceModel { get; set; } = "Phone";
        public string DeviceType { get; set; } = "mobile";
        public System.Collections.Generic.List<AirDropIncomingFile> Files { get; set; } = new();
        public long TotalSize => Files.Sum(f => f.Size);
        public int FileCount => Files.Count;
        public string PrimaryFileName => Files.FirstOrDefault()?.FileName ?? "File";
        public BitmapSource? Thumbnail { get; set; }
    }
}
