using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DynamicIsland.Media
{
    public class LyricLine
    {
        public TimeSpan Timestamp { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public class LyricsManager
    {
        private static LyricsManager? _instance;
        public static LyricsManager Instance => _instance ??= new LyricsManager();

        public ObservableCollection<LyricLine> CurrentLyrics { get; } = new();
        public string CurrentTrackKey { get; private set; } = string.Empty;
        public bool IsLoading { get; private set; } = false;
        public bool HasLyrics => CurrentLyrics.Count > 0;
        public int ActiveIndex { get; private set; } = -1;
        public static readonly TimeSpan SyncLatencyOffset = TimeSpan.FromMilliseconds(110);

        public event Action? OnLyricsLoaded;
        public event Action<int>? OnActiveIndexChanged;

        private readonly HttpClient _httpClient;
        private readonly string _cacheDir;
        private CancellationTokenSource? _fetchCts;

        public LyricsManager()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(6);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DynamicIslandWindows/2.0");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDir = Path.Combine(appData, "DynamicIslandWindows", "LyricsCache");
            Directory.CreateDirectory(_cacheDir);
        }

        public async Task FetchLyricsForTrackAsync(string rawTitle, string rawArtist, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CurrentLyrics.Clear();
                    CurrentTrackKey = string.Empty;
                    ActiveIndex = -1;
                    IsLoading = false;
                    OnLyricsLoaded?.Invoke();
                });
                return;
            }

            string cleanTitle = CleanTrackTitle(rawTitle);
            string cleanArtist = CleanArtistName(rawArtist);
            string trackKey = $"{cleanTitle} - {cleanArtist}".ToLowerInvariant();

            if (CurrentTrackKey == trackKey && CurrentLyrics.Count > 0)
            {
                return; // Already loaded for this track
            }

            _fetchCts?.Cancel();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;

            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentTrackKey = trackKey;
                CurrentLyrics.Clear();
                ActiveIndex = -1;
                IsLoading = true;
                OnLyricsLoaded?.Invoke();
            });

            // 1. Check local cache
            string safeKey = Regex.Replace(trackKey, @"[^\w\-]", "_");
            string cachePath = Path.Combine(_cacheDir, $"{safeKey}.json");

            if (File.Exists(cachePath))
            {
                try
                {
                    string cachedJson = await File.ReadAllTextAsync(cachePath, token);
                    var cachedLines = JsonSerializer.Deserialize<List<LyricLine>>(cachedJson);
                    if (cachedLines != null && cachedLines.Count > 0)
                    {
                        if (token.IsCancellationRequested) return;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            CurrentLyrics.Clear();
                            foreach (var line in cachedLines) CurrentLyrics.Add(line);
                            IsLoading = false;
                            OnLyricsLoaded?.Invoke();
                        });
                        return;
                    }
                }
                catch { }
            }

            // 2. Fetch from LRCLIB API
            try
            {
                string? syncedLrc = await QueryLrcLibAsync(cleanTitle, cleanArtist, rawTitle, duration, token);

                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrWhiteSpace(syncedLrc))
                {
                    var parsed = ParseLrc(syncedLrc);
                    if (parsed.Count > 0)
                    {
                        // Save to cache
                        try
                        {
                            string json = JsonSerializer.Serialize(parsed);
                            await File.WriteAllTextAsync(cachePath, json, token);
                        }
                        catch { }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            CurrentLyrics.Clear();
                            foreach (var line in parsed) CurrentLyrics.Add(line);
                            IsLoading = false;
                            OnLyricsLoaded?.Invoke();
                        });
                        return;
                    }
                }
            }
            catch { }

            if (token.IsCancellationRequested) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentLyrics.Clear();
                IsLoading = false;
                OnLyricsLoaded?.Invoke();
            });
        }

        private async Task<string?> QueryLrcLibAsync(string cleanTitle, string cleanArtist, string rawTitle, TimeSpan duration, CancellationToken token)
        {
            int durSec = (int)duration.TotalSeconds;
            string primaryArtist = GetPrimaryArtist(cleanArtist);

            // Step 1: Search Query with clean title + primary artist (highest success rate)
            if (!string.IsNullOrWhiteSpace(cleanTitle))
            {
                string q = !string.IsNullOrWhiteSpace(primaryArtist) ? $"{cleanTitle} {primaryArtist}" : cleanTitle;
                var lrc = await TrySearchLrcLibAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}", token);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }

            // Step 2: Search Query with clean title only
            if (!string.IsNullOrWhiteSpace(cleanTitle))
            {
                var lrc = await TrySearchLrcLibAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle)}", token);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }

            // Step 3: Exact GET with clean title & clean artist
            if (!string.IsNullOrWhiteSpace(cleanTitle) && !string.IsNullOrWhiteSpace(cleanArtist))
            {
                var lrc = await TryGetLrcLibAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}" + (durSec > 0 ? $"&duration={durSec}" : ""), token);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }

            // Step 4: Exact GET with clean title & primary artist
            if (!string.IsNullOrWhiteSpace(cleanTitle) && !string.IsNullOrWhiteSpace(primaryArtist) && primaryArtist != cleanArtist)
            {
                var lrc = await TryGetLrcLibAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(primaryArtist)}" + (durSec > 0 ? $"&duration={durSec}" : ""), token);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }

            // Step 5: Search Query with raw title
            if (!string.IsNullOrWhiteSpace(rawTitle) && rawTitle != cleanTitle)
            {
                var lrc = await TrySearchLrcLibAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(rawTitle)}", token);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }

            return null;
        }

        private async Task<string?> TryGetLrcLibAsync(string url, CancellationToken token)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, token);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                    if (doc.RootElement.TryGetProperty("syncedLyrics", out var syncedProp) && syncedProp.ValueKind == JsonValueKind.String)
                    {
                        string? lrc = syncedProp.GetString();
                        if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
                    }
                    if (doc.RootElement.TryGetProperty("plainLyrics", out var plainProp) && plainProp.ValueKind == JsonValueKind.String)
                    {
                        string? plain = plainProp.GetString();
                        if (!string.IsNullOrWhiteSpace(plain)) return FormatPlainAsLrc(plain);
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task<string?> TrySearchLrcLibAsync(string url, CancellationToken token)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, token);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        string? firstPlain = null;
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("syncedLyrics", out var syn) && syn.ValueKind == JsonValueKind.String)
                            {
                                string? lrc = syn.GetString();
                                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
                            }
                            if (firstPlain == null && item.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String)
                            {
                                string? plText = pl.GetString();
                                if (!string.IsNullOrWhiteSpace(plText)) firstPlain = plText;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(firstPlain))
                        {
                            return FormatPlainAsLrc(firstPlain);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static List<LyricLine> ParseLrc(string lrcContent)
        {
            var lines = new List<LyricLine>();
            var regex = new Regex(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{2,3}))?\](.*)");

            var rawLines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in rawLines)
            {
                var match = regex.Match(raw.Trim());
                if (match.Success)
                {
                    int min = int.Parse(match.Groups[1].Value);
                    int sec = int.Parse(match.Groups[2].Value);
                    int ms = 0;
                    if (match.Groups[3].Success)
                    {
                        string msStr = match.Groups[3].Value;
                        if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                        else if (msStr.Length == 3) ms = int.Parse(msStr);
                    }

                    var timestamp = new TimeSpan(0, 0, min, sec, ms);
                    string text = match.Groups[4].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add(new LyricLine { Timestamp = timestamp, Text = text });
                    }
                }
            }

            return lines.OrderBy(l => l.Timestamp).ToList();
        }

        private static string FormatPlainAsLrc(string plain)
        {
            var sb = new System.Text.StringBuilder();
            var rawLines = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int sec = 0;
            foreach (var line in rawLines)
            {
                sb.AppendLine($"[{sec / 60:D2}:{sec % 60:D2}.00] {line.Trim()}");
                sec += 4;
            }
            return sb.ToString();
        }

        public void UpdatePlaybackPosition(TimeSpan position)
        {
            if (CurrentLyrics.Count == 0)
            {
                if (ActiveIndex != -1)
                {
                    ActiveIndex = -1;
                    OnActiveIndexChanged?.Invoke(-1);
                }
                return;
            }

            var syncPos = position + SyncLatencyOffset;

            int newIndex = -1;
            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                if (syncPos >= CurrentLyrics[i].Timestamp)
                {
                    newIndex = i;
                }
                else
                {
                    break;
                }
            }

            if (newIndex != ActiveIndex)
            {
                ActiveIndex = newIndex;
                OnActiveIndexChanged?.Invoke(newIndex);
            }
        }

        private static string CleanTrackTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string cleaned = title;
            cleaned = Regex.Replace(cleaned, @"\s*\|.*", ""); // Strip pipe YouTube title suffixes
            // Strip any bracketed or parenthesized subtitle e.g. (Title Track), (From "Movie"), (Feat...), [Official Video]
            cleaned = Regex.Replace(cleaned, @"\s*[\(\[][^\)\]]+[\)\]]", "", RegexOptions.IgnoreCase);
            // Strip trailing dashes "- From ...", "- Title Track", "- Remastered"
            cleaned = Regex.Replace(cleaned, @"\s*-\s*.*", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Replace("\"", "").Replace("'", "").Trim();
            return cleaned;
        }

        private static string CleanArtistName(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            string cleaned = artist;
            cleaned = Regex.Replace(cleaned, @"\s*-\s*Topic", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*VEVO", "", RegexOptions.IgnoreCase);
            return cleaned.Trim();
        }

        private static string GetPrimaryArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            string clean = CleanArtistName(artist);
            var parts = clean.Split(new[] { ',', '&', '/', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0].Trim() : clean;
        }
    }
}
