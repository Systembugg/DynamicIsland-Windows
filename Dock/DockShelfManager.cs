using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.Dock
{
    public enum DockShelfStatus
    {
        Idle,
        Docked,     // Half-filled 50% blue ring (waiting on shelf)
        Used        // 100% full green ring + checkmark (used / dragged out)
    }

    public enum DockItemType
    {
        File,
        Folder,
        Image,
        Link,
        Text,
        Archive
    }

    public class DockItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileSizeFormatted { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public string TextContent { get; set; } = string.Empty;
        public DockItemType ItemType { get; set; } = DockItemType.File;
        public ImageSource? Thumbnail { get; set; }
        public bool IsUsed { get; set; } = false;
    }

    public class DockShelfManager
    {
        public static DockShelfManager Instance { get; } = new DockShelfManager();

        public ObservableCollection<DockItem> Items { get; } = new ObservableCollection<DockItem>();
        public DockShelfStatus Status { get; private set; } = DockShelfStatus.Idle;

        public event Action<DockShelfStatus, int>? OnShelfChanged;

        public bool HasItems => Items.Count > 0;
        public int ItemCount => Items.Count;

        public DateTimeOffset? LastActivityTimestamp { get; private set; }

        public void TouchActivity()
        {
            LastActivityTimestamp = DateTimeOffset.Now;
        }

        public bool ShouldShowCompactDock => HasItems && (DateTimeOffset.Now - LastActivityTimestamp.GetValueOrDefault(DateTimeOffset.MinValue)).TotalSeconds < 20;

        private readonly string cacheDirectory;
        private readonly DispatcherTimer revertToBlueTimer;

        public DockShelfManager()
        {
            cacheDirectory = Path.Combine(Path.GetTempPath(), "DynamicIsland_DockCache");
            try
            {
                if (!Directory.Exists(cacheDirectory))
                {
                    Directory.CreateDirectory(cacheDirectory);
                }
            }
            catch { }

            revertToBlueTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2500)
            };
            revertToBlueTimer.Tick += (s, e) =>
            {
                revertToBlueTimer.Stop();
                if (Items.Count > 0)
                {
                    Status = DockShelfStatus.Docked;
                    OnShelfChanged?.Invoke(Status, Items.Count);
                }
            };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;

        public void AddDataObject(IDataObject data)
        {
            if (data == null) return;

            // 1. Files & Folders
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    AddFiles(files);
                    return;
                }
            }

            // 2. Images & Bitmaps
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                if (data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                {
                    AddImage(bmp);
                    return;
                }
            }

            // 3. Text & URLs
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string rawText = (data.GetData(DataFormats.UnicodeText) as string) 
                              ?? (data.GetData(DataFormats.Text) as string) 
                              ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(rawText))
                {
                    rawText = rawText.Trim();
                    if (IsUrl(rawText))
                    {
                        AddLink(rawText);
                    }
                    else
                    {
                        AddText(rawText);
                    }
                }
            }
        }

        public void PasteFromClipboard()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data != null)
                {
                    AddDataObject(data);
                }
            }
            catch { }
        }

        public void AddFiles(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            revertToBlueTimer.Stop();

            foreach (var path in filePaths)
            {
                try 
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;

                    bool isDir = Directory.Exists(path);
                    var info = new FileInfo(path);
                    string name = Path.GetFileName(path);
                    string ext = isDir ? "DIR" : Path.GetExtension(path).ToUpperInvariant().Replace(".", "");
                    string size = isDir ? "Folder" : FormatFileSize(info.Exists ? info.Length : 0);
                    var thumb = GetFileThumbnail(path);

                    var itemType = isDir ? DockItemType.Folder : ClassifyExtension(ext);

                    Items.Add(new DockItem
                    {
                        FilePath = path,
                        FileName = string.IsNullOrWhiteSpace(name) ? path : name,
                        FileSizeFormatted = size,
                        FileExtension = string.IsNullOrWhiteSpace(ext) ? "FILE" : ext,
                        ItemType = itemType,
                        Thumbnail = thumb,
                        IsUsed = false
                    });
                }
                catch { }
            }

            TouchActivity();
            Status = DockShelfStatus.Docked;
            OnShelfChanged?.Invoke(Status, Items.Count);
        }

        public void AddLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            revertToBlueTimer.Stop();
            try
            {
                string domain = "Link";
                try
                {
                    var uri = new Uri(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url);
                    domain = uri.Host.Replace("www.", "");
                }
                catch { }

                string shortcutPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(domain)}_{Guid.NewGuid().ToString("N").Substring(0, 6)}.url");
                File.WriteAllText(shortcutPath, $"[InternetShortcut]\nURL={url}\n");

                Items.Add(new DockItem
                {
                    FilePath = shortcutPath,
                    FileName = domain,
                    FileSizeFormatted = "Web Link",
                    FileExtension = "URL",
                    TextContent = url,
                    ItemType = DockItemType.Link,
                    Thumbnail = CreateVectorDrawingThumbnail("IconSafari"),
                    IsUsed = false
                });

                TouchActivity();
                Status = DockShelfStatus.Docked;
                OnShelfChanged?.Invoke(Status, Items.Count);
            }
            catch { }
        }

        public void AddText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            revertToBlueTimer.Stop();
            try
            {
                string preview = text.Length > 24 ? text.Substring(0, 24) + "..." : text;
                string cleanPreview = Regex.Replace(preview, @"\r\n?|\n", " ");

                string snippetPath = Path.Combine(cacheDirectory, $"Note_{Guid.NewGuid().ToString("N").Substring(0, 6)}.txt");
                File.WriteAllText(snippetPath, text);

                Items.Add(new DockItem
                {
                    FilePath = snippetPath,
                    FileName = cleanPreview,
                    FileSizeFormatted = $"{text.Length} chars",
                    FileExtension = "TXT",
                    TextContent = text,
                    ItemType = DockItemType.Text,
                    Thumbnail = CreateVectorDrawingThumbnail("IconNote"),
                    IsUsed = false
                });

                TouchActivity();
                Status = DockShelfStatus.Docked;
                OnShelfChanged?.Invoke(Status, Items.Count);
            }
            catch { }
        }

        public void AddImage(BitmapSource bitmap)
        {
            if (bitmap == null) return;

            revertToBlueTimer.Stop();
            try
            {
                string imgPath = Path.Combine(cacheDirectory, $"Image_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 4)}.png");
                
                using (var fs = new FileStream(imgPath, FileMode.Create))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(fs);
                }

                var info = new FileInfo(imgPath);

                Items.Add(new DockItem
                {
                    FilePath = imgPath,
                    FileName = Path.GetFileName(imgPath),
                    FileSizeFormatted = FormatFileSize(info.Length),
                    FileExtension = "PNG",
                    ItemType = DockItemType.Image,
                    Thumbnail = bitmap,
                    IsUsed = false
                });

                TouchActivity();
                Status = DockShelfStatus.Docked;
                OnShelfChanged?.Invoke(Status, Items.Count);
            }
            catch { }
        }

        public void MarkItemUsed(DockItem item)
        {
            TouchActivity();
            if (item != null) item.IsUsed = true;
            Status = DockShelfStatus.Used;
            OnShelfChanged?.Invoke(Status, Items.Count);

            // Revert back to 50% Blue after 2.5s celebration
            revertToBlueTimer.Stop();
            revertToBlueTimer.Start();
        }

        public void MarkAllUsed()
        {
            foreach (var itm in Items) itm.IsUsed = true;
            Status = DockShelfStatus.Used;
            OnShelfChanged?.Invoke(Status, Items.Count);

            revertToBlueTimer.Stop();
            revertToBlueTimer.Start();
        }

        public void RemoveItem(DockItem item)
        {
            if (item == null) return;
            Items.Remove(item);
            if (Items.Count == 0)
            {
                Status = DockShelfStatus.Idle;
                revertToBlueTimer.Stop();
            }
            OnShelfChanged?.Invoke(Status, Items.Count);
        }

        public void ClearShelf()
        {
            revertToBlueTimer.Stop();
            Items.Clear();
            Status = DockShelfStatus.Idle;
            OnShelfChanged?.Invoke(Status, 0);
        }

        private static bool IsUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
                   (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && text.Contains("."));
        }

        private static DockItemType ClassifyExtension(string ext)
        {
            ext = ext.ToLowerInvariant();
            return ext switch
            {
                "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" or "svg" or "ico" => DockItemType.Image,
                "zip" or "rar" or "7z" or "tar" or "gz" or "iso" => DockItemType.Archive,
                "txt" or "md" or "json" or "log" or "cs" or "js" or "html" or "css" or "py" => DockItemType.Text,
                _ => DockItemType.File
            };
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static ImageSource? CreateVectorDrawingThumbnail(string iconKey)
        {
            try
            {
                if (Application.Current?.Resources.Contains(iconKey) == true)
                {
                    if (Application.Current.Resources[iconKey] is Geometry geom)
                    {
                        var group = new DrawingGroup();
                        group.Children.Add(new GeometryDrawing(
                            new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                            new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1.5),
                            geom));
                        return new DrawingImage(group);
                    }
                }
            }
            catch { }
            return null;
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
            return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F1} GB";
        }

        private ImageSource? GetFileThumbnail(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif" or ".ico")
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.DecodePixelWidth = 64;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                SHFILEINFO shinfo = new SHFILEINFO();
                IntPtr hImg = SHGetFileInfo(path, 0, out shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var img = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        return img;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}

