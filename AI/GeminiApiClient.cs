using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DynamicIsland.AI
{
    public class AiResponseResult
    {
        public string Text { get; set; } = "";
        public MemoryItem? MatchedMemory { get; set; }
        public bool HasCard => MatchedMemory != null;
        public bool IsError { get; set; } = false;
    }

    public class GeminiApiClient
    {
        private static GeminiApiClient? _instance;
        public static GeminiApiClient Instance => _instance ??= new GeminiApiClient();

        private readonly HttpClient _httpClient;
        private string _apiKey = "";
        private const string Model = "gemini-2.5-flash";

        public GeminiApiClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            LoadApiKey();
        }

        private void LoadApiKey()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
                string configPath = Path.Combine(folder, "ai_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("GeminiApiKey", out var keyProp))
                    {
                        _apiKey = keyProp.GetString()?.Trim() ?? "";
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ?? "";
            }
        }

        public void SetApiKey(string key)
        {
            if (!string.IsNullOrWhiteSpace(key)) _apiKey = key.Trim();
        }

        public async Task<AiResponseResult> ProcessQueryAsync(string userPrompt, string? attachedImagePath = null, string? attachedBase64 = null)
        {
            try
            {
                // 1. Fetch ALL user memories so Gemini has full omniscient awareness of the user's vault
                var allMemories = AiMemoryDatabase.Instance.GetAllMemories(35);

                // Group by category for structured memory presentation
                var movieItems = allMemories.Where(m => m.Category.Equals("Movie", StringComparison.OrdinalIgnoreCase) || m.Category.Equals("Film", StringComparison.OrdinalIgnoreCase)).ToList();
                var animeItems = allMemories.Where(m => m.Category.Equals("Anime", StringComparison.OrdinalIgnoreCase) || m.Category.Equals("Manga", StringComparison.OrdinalIgnoreCase)).ToList();
                var seriesItems = allMemories.Where(m => m.Category.Equals("Series", StringComparison.OrdinalIgnoreCase) || m.Category.Equals("Show", StringComparison.OrdinalIgnoreCase) || m.Category.Equals("TV", StringComparison.OrdinalIgnoreCase)).ToList();
                var taskItems = allMemories.Where(m => m.Category.Equals("Task", StringComparison.OrdinalIgnoreCase) || m.Category.Equals("Reminder", StringComparison.OrdinalIgnoreCase)).ToList();
                var noteItems = allMemories.Where(m => !movieItems.Contains(m) && !animeItems.Contains(m) && !seriesItems.Contains(m) && !taskItems.Contains(m)).ToList();

                var vaultSb = new StringBuilder();
                vaultSb.AppendLine("\nUSER'S PERSISTENT MEMORY VAULT (Total Items: " + allMemories.Count + "):");

                vaultSb.AppendLine($"[MOVIES WATCHLIST (Total: {movieItems.Count})]");
                if (movieItems.Count > 0)
                {
                    foreach (var m in movieItems) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No movies saved yet)");

                vaultSb.AppendLine($"\n[ANIME WATCHLIST (Total: {animeItems.Count})]");
                if (animeItems.Count > 0)
                {
                    foreach (var m in animeItems) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No anime saved yet)");

                vaultSb.AppendLine($"\n[TV SERIES & SHOWS (Total: {seriesItems.Count})]");
                if (seriesItems.Count > 0)
                {
                    foreach (var m in seriesItems) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No series saved yet)");

                vaultSb.AppendLine($"\n[TASKS & REMINDERS (Total: {taskItems.Count})]");
                if (taskItems.Count > 0)
                {
                    foreach (var m in taskItems) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No tasks saved yet)");

                vaultSb.AppendLine($"\n[NOTES, SCREENSHOTS & GENERAL (Total: {noteItems.Count})]");
                if (noteItems.Count > 0)
                {
                    foreach (var m in noteItems.Take(8)) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No general notes saved yet)");

                string memoryContext = vaultSb.ToString();

                string systemInstruction = @"You are the Personal AI Companion integrated inside Windows Dynamic Island.
You are ultra-smart, proactive, concise, helpful, and speak naturally in English, Hindi, or Hinglish (matching the user's phrasing).

CRITICAL CONVERSATIONAL & MEMORY RULES:
1. ACCURATE MEMORY RECALL (NEVER FORGET OR MISS ITEMS):
- Refer to the USER'S PERSISTENT MEMORY VAULT below. It contains the complete real-time record of all items saved.
- When the user asks what is saved, asks for their watchlist, or asks what movies/anime/items exist (e.g., 'konsi movie hai', 'bata kitne movie save kiya', 'meri watchlist', 'what movies did i save', 'aur konsi hai'):
  ALWAYS list ALL items belonging to that category!
  Example: 'Aapki movie watchlist me 2 movies hain: 1. Ice Cream Man (2026), 2. The End of Oak Street.'
  NEVER miss any item or falsely claim there is only 1 item if multiple exist!
- If the user asks about a specific item or the latest item, you can display its rich visual card by appending:
  <MEMORY_SHOW id=""exact_id""/> at the very end of your response.

2. VISION & ACTIVE SCREEN INTELLIGENCE:
- When an image/screenshot is provided or when active window context is present:
  The user may say 'i want to watch this movie', 'add this to watchlist', 'ye movie save kar', 'what is this', etc.
- NEVER ask 'What is the title?' if the title or content is visible on the screen or in the window title!
- Directly extract the exact title, category (Movie, Anime, Series, Task, Note), and a concise detail from screen and context.
- Automatically save it with <MEMORY_SAVE category=""..."" title=""..."" detail=""...""/> at the end of your response!
- In your short friendly response, confirm that you recognized it and saved it (e.g. 'Got it! Added Inception to your Movie watchlist.').

3. ACTION TAGS & CLEAN OUTPUT (ABSOLUTELY NO LEAKING PAYLOAD):
- NEVER output raw XML or payload tags in the middle of your spoken response.
- If saving: Append <MEMORY_SAVE category=""Movie|Anime|Series|Task|Note|Screen|General"" title=""Exact Title"" detail=""Quick synopsis or info""/> at the very end.
- If showing a specific existing card: Append <MEMORY_SHOW id=""id""/> at the very end.
- NEVER wrap action tags in markdown code blocks (e.g. ```xml). Just append the single tag at the end.

Respond in the user's language (English, Hindi, or Hinglish as prompted)." + memoryContext;

                // 2. Build Gemini Request Payload
                var contentsNode = new JsonArray();
                var partsNode = new JsonArray();

                // Add text part
                partsNode.Add(new JsonObject
                {
                    ["text"] = userPrompt
                });

                // Add image part if provided
                if (!string.IsNullOrEmpty(attachedBase64))
                {
                    partsNode.Add(new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = "image/jpeg",
                            ["data"] = attachedBase64
                        }
                    });
                }

                contentsNode.Add(new JsonObject
                {
                    ["parts"] = partsNode
                });

                var rootPayload = new JsonObject
                {
                    ["system_instruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = systemInstruction }
                        }
                    },
                    ["contents"] = contentsNode,
                    ["generationConfig"] = new JsonObject
                    {
                        ["temperature"] = 0.7,
                        ["maxOutputTokens"] = 512
                    }
                };

                string jsonContent = rootPayload.ToJsonString();
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
                var response = await _httpClient.PostAsync(url, httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    string err = await response.Content.ReadAsStringAsync();
                    return new AiResponseResult
                    {
                        Text = "Sorry, couldn't reach Gemini right now. Check your internet connection.",
                        IsError = true
                    };
                }

                string responseString = await response.Content.ReadAsStringAsync();
                var responseJson = JsonNode.Parse(responseString);
                string fullResponse = responseJson?["candidates"]?[0]?["content"]?[partsNode.Count > 0 ? "parts" : "parts"]?[0]?["text"]?.ToString() ?? "";

                // Parse tags and sanitize
                var result = new AiResponseResult();
                result.Text = ExtractCleanTextAndHandleTags(fullResponse, attachedImagePath, allMemories, out var matched);
                result.MatchedMemory = matched;

                return result;
            }
            catch (Exception ex)
            {
                return new AiResponseResult
                {
                    Text = $"Error: {ex.Message}",
                    IsError = true
                };
            }
        }

        private string ExtractCleanTextAndHandleTags(string rawText, string? attachedImagePath, List<MemoryItem> allMemories, out MemoryItem? cardItem)
        {
            cardItem = null;

            // 1. Robust Regex extraction for <MEMORY_SAVE ...>
            var saveMatch = Regex.Match(rawText, @"<MEMORY_SAVE\b([^>]*?)(?:/>|>.*?</MEMORY_SAVE>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (saveMatch.Success)
            {
                string tagContent = saveMatch.Value;
                string attrs = saveMatch.Groups[1].Value;

                string category = ExtractRegexAttr(attrs, "category") ?? "Note";
                string title = ExtractRegexAttr(attrs, "title") ?? "New Memory";
                string detail = ExtractRegexAttr(attrs, "detail") ?? "";

                if (string.IsNullOrWhiteSpace(detail) && tagContent.Contains("</MEMORY_SAVE>"))
                {
                    int bodyStart = tagContent.IndexOf('>') + 1;
                    int bodyEnd = tagContent.LastIndexOf('<');
                    if (bodyEnd > bodyStart) detail = tagContent.Substring(bodyStart, bodyEnd - bodyStart).Trim();
                }

                cardItem = AiMemoryDatabase.Instance.SaveMemory(category, title, detail, attachedImagePath);
            }

            // 2. Robust Regex extraction for <MEMORY_SHOW ...>
            var showMatch = Regex.Match(rawText, @"<MEMORY_SHOW\b([^>]*?)(?:/>|>.*?</MEMORY_SHOW>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (showMatch.Success && cardItem == null)
            {
                string attrs = showMatch.Groups[1].Value;
                string idStr = ExtractRegexAttr(attrs, "id") ?? "";
                if (int.TryParse(idStr, out int id))
                {
                    cardItem = allMemories.FirstOrDefault(m => m.Id == id);
                }
                if (cardItem == null && allMemories.Count > 0)
                {
                    cardItem = allMemories.First();
                }
            }

            // 3. Fallback: If no action tag, check if raw text specifically refers to an item in memory
            if (cardItem == null && allMemories.Count > 0)
            {
                foreach (var m in allMemories)
                {
                    if (!string.IsNullOrWhiteSpace(m.Title) && m.Title.Length >= 4 &&
                        rawText.Contains(m.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        cardItem = m;
                        break;
                    }
                }
            }

            // 4. STRIP ALL ACTION TAGS, CODE FENCES, AND MARKUP LEAKS COMPLETELY!
            string cleanText = rawText;
            cleanText = Regex.Replace(cleanText, @"```(?:xml|json)?\s*<MEMORY_[^>]*>.*?```", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleanText = Regex.Replace(cleanText, @"<MEMORY_SAVE\b[^>]*?(?:/>|>.*?</MEMORY_SAVE>|>)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleanText = Regex.Replace(cleanText, @"<MEMORY_SHOW\b[^>]*?(?:/>|>.*?</MEMORY_SHOW>|>)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleanText = Regex.Replace(cleanText, @"</?MEMORY_[^>]*>", "", RegexOptions.IgnoreCase); // Any stray tags
            cleanText = cleanText.Trim();

            return cleanText;
        }

        private string? ExtractRegexAttr(string text, string attrName)
        {
            var match = Regex.Match(text, $@"{attrName}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();

            var unquoted = Regex.Match(text, $@"{attrName}\s*=\s*([^\s>]+)", RegexOptions.IgnoreCase);
            if (unquoted.Success) return unquoted.Groups[1].Value.Trim();

            return null;
        }
    }
}
