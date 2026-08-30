using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DynamicIsland
{
    public enum HistoryItemType
    {
        Text,
        Code,
        Link,
        ColorHex,
        Image,
        Note,
        Snippet
    }

    public class HistoryItemModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public HistoryItemType Type { get; set; } = HistoryItemType.Text;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsPinned { get; set; } = false;
        public string RelativeTime => GetRelativeTime(CreatedAt);

        public static string GetRelativeTime(DateTime time)
        {
            var diff = DateTime.UtcNow - time;
            if (diff.TotalSeconds < 10) return "Just now";
            if (diff.TotalSeconds < 60) return $"{(int)diff.TotalSeconds}s ago";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"in {(int)diff.TotalHours} hours";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
            return time.ToLocalTime().ToString("MMM d");
        }
    }

    public class ClipboardHistoryManager
    {
        private static ClipboardHistoryManager? _instance;
        public static ClipboardHistoryManager Instance => _instance ??= new ClipboardHistoryManager();

        public ObservableCollection<HistoryItemModel> ClipboardItems { get; } = new();
        public ObservableCollection<HistoryItemModel> NotesItems { get; } = new();
        public ObservableCollection<HistoryItemModel> ScreenshotsItems { get; } = new();
        public ObservableCollection<HistoryItemModel> SnippetsItems { get; } = new();

        public event Action? OnDataChanged;
        public event Action<string, string, bool>? OnItemCaptured;

        private readonly string _storagePath;
        private readonly string _screenshotsDir;
        private string _lastCopiedText = "";
        private int _lastImageW = 0;
        private int _lastImageH = 0;
        private DateTime _lastImageTime = DateTime.MinValue;
        private bool _isInternalCopying = false;

        public ClipboardHistoryManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "DynamicIslandWindows");
            _screenshotsDir = Path.Combine(dir, "Screenshots");
            Directory.CreateDirectory(_screenshotsDir);
            _storagePath = Path.Combine(dir, "clipboard_notes_data.json");

            LoadFromDisk();
        }

        public void HandleClipboardUpdate()
        {
            if (_isInternalCopying) return;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        var now = DateTime.UtcNow;
                        // Bulletproof deduplication guard: ignore multiple clipboard formats fired for same screenshot within 2.5s
                        if (img.PixelWidth == _lastImageW && img.PixelHeight == _lastImageH && (now - _lastImageTime).TotalMilliseconds < 2500)
                        {
                            return;
                        }

                        _lastImageW = img.PixelWidth;
                        _lastImageH = img.PixelHeight;
                        _lastImageTime = now;

                        string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                        string filePath = Path.Combine(_screenshotsDir, fileName);

                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(img));
                            encoder.Save(fileStream);
                        }

                        string title = $"Screenshot {DateTime.Now:MMM d, h:mm tt}";
                        string info = $"{img.PixelWidth}x{img.PixelHeight} PNG";

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ScreenshotsItems.Insert(0, new HistoryItemModel
                            {
                                Title = title,
                                Content = info,
                                ImagePath = filePath,
                                Type = HistoryItemType.Image,
                                CreatedAt = DateTime.UtcNow
                            });

                            while (ScreenshotsItems.Count > 40)
                            {
                                var old = ScreenshotsItems[ScreenshotsItems.Count - 1];
                                ScreenshotsItems.RemoveAt(ScreenshotsItems.Count - 1);
                                try { if (File.Exists(old.ImagePath)) File.Delete(old.ImagePath); } catch { }
                            }

                            SaveToDisk();
                            OnDataChanged?.Invoke();
                            OnItemCaptured?.Invoke("Screenshot Captured", $"{img.PixelWidth}x{img.PixelHeight} • Just now", true);
                        });
                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(text) || text == _lastCopiedText) return;

                    _lastCopiedText = text;

                    // Classify text
                    HistoryItemType type = HistoryItemType.Text;
                    string trimmed = text.Trim();
                    if (trimmed.StartsWith("http://") || trimmed.StartsWith("https://") || trimmed.StartsWith("www."))
                    {
                        type = HistoryItemType.Link;
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3}|[A-Fa-f0-9]{8})$"))
                    {
                        type = HistoryItemType.ColorHex;
                    }
                    else if (trimmed.Contains("const ") || trimmed.Contains("function ") || trimmed.Contains("class ") || 
                             trimmed.Contains("import ") || trimmed.Contains("def ") || trimmed.Contains("{") || trimmed.Contains("=>"))
                    {
                        type = HistoryItemType.Code;
                    }

                    string title = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
                    if (title.Length > 50) title = title.Substring(0, 47) + "...";

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var existing = ClipboardItems.FirstOrDefault(i => i.Content == text);
                        if (existing != null) ClipboardItems.Remove(existing);

                        ClipboardItems.Insert(0, new HistoryItemModel
                        {
                            Title = title,
                            Content = text,
                            Type = type,
                            CreatedAt = DateTime.UtcNow
                        });

                        while (ClipboardItems.Count > 50)
                        {
                            ClipboardItems.RemoveAt(ClipboardItems.Count - 1);
                        }

                        SaveToDisk();
                        OnDataChanged?.Invoke();
                        OnItemCaptured?.Invoke("Copied to Clipboard", title, false);
                    });
                }
            }
            catch { }
        }

        public void AddNote(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            string t = string.IsNullOrWhiteSpace(title) ? (content.Split('\n').FirstOrDefault() ?? "New Note") : title;
            NotesItems.Insert(0, new HistoryItemModel
            {
                Title = t,
                Content = content,
                Type = HistoryItemType.Note,
                CreatedAt = DateTime.UtcNow
            });
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        public void AddSnippet(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            string t = string.IsNullOrWhiteSpace(title) ? (content.Split('\n').FirstOrDefault() ?? "Snippet") : title;
            SnippetsItems.Insert(0, new HistoryItemModel
            {
                Title = t,
                Content = content,
                Type = HistoryItemType.Snippet,
                CreatedAt = DateTime.UtcNow
            });
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        public void UpdateItem(HistoryItemModel item, string title, string content)
        {
            if (item == null || string.IsNullOrWhiteSpace(content)) return;
            item.Title = string.IsNullOrWhiteSpace(title) ? (content.Split('\n').FirstOrDefault() ?? "Note") : title;
            item.Content = content;
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        public void TogglePin(HistoryItemModel item)
        {
            item.IsPinned = !item.IsPinned;
            SortCollection(ClipboardItems);
            SortCollection(NotesItems);
            SortCollection(ScreenshotsItems);
            SortCollection(SnippetsItems);
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        public void DeleteItem(HistoryItemModel item)
        {
            ClipboardItems.Remove(item);
            NotesItems.Remove(item);
            ScreenshotsItems.Remove(item);
            SnippetsItems.Remove(item);
            if (!string.IsNullOrEmpty(item.ImagePath))
            {
                try { if (File.Exists(item.ImagePath)) File.Delete(item.ImagePath); } catch { }
            }
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        public void ClearClipboard()
        {
            var unpinned = ClipboardItems.Where(i => !i.IsPinned).ToList();
            foreach (var item in unpinned)
            {
                ClipboardItems.Remove(item);
            }
            var unpinnedScreens = ScreenshotsItems.Where(i => !i.IsPinned).ToList();
            foreach (var item in unpinnedScreens)
            {
                ScreenshotsItems.Remove(item);
                if (!string.IsNullOrEmpty(item.ImagePath))
                {
                    try { if (File.Exists(item.ImagePath)) File.Delete(item.ImagePath); } catch { }
                }
            }
            SaveToDisk();
            OnDataChanged?.Invoke();
        }

        private void SortCollection(ObservableCollection<HistoryItemModel> col)
        {
            var sorted = col.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.CreatedAt).ToList();
            col.Clear();
            foreach (var item in sorted) col.Add(item);
        }

        public void CopyToClipboard(HistoryItemModel item)
        {
            try
            {
                _isInternalCopying = true;

                if (item.Type == HistoryItemType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    _lastImageTime = DateTime.UtcNow; // ensure internal copy doesn't re-capture
                    _lastImageW = 0;
                    _lastImageH = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Clipboard.SetImage(bmp);
                    return;
                }

                _lastCopiedText = item.Content;
                Clipboard.SetText(item.Content);
            }
            catch { }
            finally
            {
                Task.Delay(1200).ContinueWith(_ => _isInternalCopying = false);
            }
        }

        private void SaveToDisk()
        {
            try
            {
                var data = new StorageWrapper
                {
                    Clipboard = ClipboardItems.ToList(),
                    Notes = NotesItems.ToList(),
                    Screenshots = ScreenshotsItems.ToList(),
                    Snippets = SnippetsItems.ToList()
                };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch { }
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_storagePath)) return;
                string json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<StorageWrapper>(json);
                if (data != null)
                {
                    ClipboardItems.Clear();
                    foreach (var item in data.Clipboard ?? new()) ClipboardItems.Add(item);

                    NotesItems.Clear();
                    foreach (var item in data.Notes ?? new()) NotesItems.Add(item);

                    ScreenshotsItems.Clear();
                    foreach (var item in data.Screenshots ?? new()) ScreenshotsItems.Add(item);

                    SnippetsItems.Clear();
                    foreach (var item in data.Snippets ?? new()) SnippetsItems.Add(item);
                }
            }
            catch { }
        }

        private class StorageWrapper
        {
            public List<HistoryItemModel>? Clipboard { get; set; }
            public List<HistoryItemModel>? Notes { get; set; }
            public List<HistoryItemModel>? Screenshots { get; set; }
            public List<HistoryItemModel>? Snippets { get; set; }
        }
    }
}
