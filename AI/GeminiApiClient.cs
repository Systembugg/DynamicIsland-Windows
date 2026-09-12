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
        public bool MemoryDeleted { get; set; } = false;
    }

    public class ChatTurn
    {
        public string Role { get; set; } = "user"; // "user" or "model"
        public string Text { get; set; } = "";
    }

    public class GeminiApiClient
    {
        private static GeminiApiClient? _instance;
        public static GeminiApiClient Instance => _instance ??= new GeminiApiClient();

        private readonly HttpClient _httpClient;
        private string _apiKey = "";

        // Candidate models in order of priority (all tested and verified working on your key)
        private static readonly string[] CandidateModels = new[]
        {
            "gemini-3.6-flash",
            "gemini-3.5-flash",
            "gemini-flash-latest"
        };

        // Multi-Turn Ongoing Conversation Memory (Maintains dialogue context like Siri / ChatGPT)
        private readonly List<ChatTurn> _conversationHistory = new();
        private readonly object _historyLock = new();

        public GeminiApiClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            LoadApiKey();
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
            }
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
                var allMemories = AiMemoryDatabase.Instance.GetAllMemories(50);

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
                    foreach (var m in noteItems.Take(10)) vaultSb.AppendLine($"* [ID:{m.Id}] \"{m.Title}\" | Info: {m.Detail} (Saved: {m.CreatedAt:MMM d, yyyy})");
                }
                else vaultSb.AppendLine("(No general notes saved yet)");

                string memoryContext = vaultSb.ToString();

                string systemInstruction = @"You are the ultra-smart Personal AI Companion built into the Windows Dynamic Island (like Apple Intelligence Siri + ChatGPT).
You understand English, Hindi, and natural Hinglish fluently. You are witty, conversational, helpful, and concise.

MULTI-TURN CONVERSATION & INTELLIGENCE:
1. ONGOING CONTEXT AWARENESS:
- You remember the previous turns of this conversation! When the user says 'isme lead actor kaun hai', 'aur iska director?', 'aur konsi hai?', 'pehli wali hata de', 'what about that?', understand their references immediately based on previous dialogue!

2. ACCURATE MEMORY VAULT RECALL:
- Refer to the USER'S PERSISTENT MEMORY VAULT below.
- When the user asks what is saved, asks for their watchlist, or asks what movies/shows they have:
  ALWAYS list ALL items in that category clearly!
  Example: 'Aapki watchlist me 2 movies hain: 1. Ice Cream Man (2026), 2. The End of Oak Street.'
  NEVER miss items or falsely claim there is only 1 item if multiple exist.

3. VISION & ACTIVE WINDOW INTELLIGENCE:
- When screen context or active window title is provided, and the user asks to save or asks what is on screen:
  NEVER ask 'What is the title?'. Directly extract the exact title, category (Movie, Anime, Series, Task, Note), and a concise detail.
  Confirm naturally in your response, and append the action tag.

4. AUTONOMOUS ACTIONS (MUST BE APPENDED AT THE VERY END OF YOUR RESPONSE):
- To save an item: <ACTION:SAVE category=""Movie|Anime|Series|Task|Note|General"" title=""Exact Title"" detail=""Year, genre, or concise info""/>
- To delete an item from watchlist: <ACTION:DELETE id=""ID"" title=""Title""/>
- To display a rich visual card of an item: <ACTION:SHOW id=""ID""/>
- Format rule: NEVER show raw action tags in the middle of your speech. Put them strictly at the end. NEVER use markdown code fences around them.

Respond in natural, conversational tone matching the user's language." + memoryContext;

                // 2. Build Multi-Turn Request Payload
                var contentsNode = new JsonArray();

                lock (_historyLock)
                {
                    // Keep up to 14 past turns for deep conversational context
                    int skip = Math.Max(0, _conversationHistory.Count - 14);
                    foreach (var pastTurn in _conversationHistory.Skip(skip))
                    {
                        var pastParts = new JsonArray { new JsonObject { ["text"] = pastTurn.Text } };
                        contentsNode.Add(new JsonObject
                        {
                            ["role"] = pastTurn.Role,
                            ["parts"] = pastParts
                        });
                    }
                }

                // Add current turn
                var currentParts = new JsonArray();
                currentParts.Add(new JsonObject { ["text"] = userPrompt });

                if (!string.IsNullOrEmpty(attachedBase64))
                {
                    currentParts.Add(new JsonObject
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
                    ["role"] = "user",
                    ["parts"] = currentParts
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
                        ["maxOutputTokens"] = 600
                    }
                };

                string jsonContent = rootPayload.ToJsonString();

                // 3. Multi-Model Failover Execution
                string fullResponse = "";
                string lastError = "";

                foreach (var modelName in CandidateModels)
                {
                    try
                    {
                        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_apiKey}";

                        var response = await _httpClient.PostAsync(url, httpContent);
                        if (response.IsSuccessStatusCode)
                        {
                            string responseString = await response.Content.ReadAsStringAsync();
                            var responseJson = JsonNode.Parse(responseString);
                            fullResponse = responseJson?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(fullResponse))
                            {
                                System.Diagnostics.Debug.WriteLine($"[Gemini] Success using model: {modelName}");
                                break;
                            }
                        }
                        else
                        {
                            string err = await response.Content.ReadAsStringAsync();
                            lastError = $"{modelName}: HTTP {response.StatusCode} - {err}";
                            System.Diagnostics.Debug.WriteLine($"[Gemini] Failover from {modelName}: {err}");
                        }
                    }
                    catch (Exception mEx)
                    {
                        lastError = $"{modelName}: {mEx.Message}";
                        System.Diagnostics.Debug.WriteLine($"[Gemini] Error with {modelName}: {mEx.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(fullResponse))
                {
                    return new AiResponseResult
                    {
                        Text = "Sorry, couldn't get a response from Gemini right now. Please try again.",
                        IsError = true
                    };
                }

                // 4. Parse Autonomous Actions and Sanitize Output
                var result = new AiResponseResult();
                result.Text = ExtractCleanTextAndHandleActions(fullResponse, attachedImagePath, allMemories, out var matchedCard, out bool wasDeleted);
                result.MatchedMemory = matchedCard;
                result.MemoryDeleted = wasDeleted;

                // 5. Save to Multi-turn Conversation History
                lock (_historyLock)
                {
                    _conversationHistory.Add(new ChatTurn { Role = "user", Text = userPrompt });
                    _conversationHistory.Add(new ChatTurn { Role = "model", Text = result.Text });

                    // Prune history to 24 turns
                    if (_conversationHistory.Count > 24)
                    {
                        _conversationHistory.RemoveRange(0, _conversationHistory.Count - 24);
                    }
                }

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

        private string ExtractCleanTextAndHandleActions(string rawText, string? attachedImagePath, List<MemoryItem> allMemories, out MemoryItem? cardItem, out bool memoryDeleted)
        {
            cardItem = null;
            memoryDeleted = false;

            // 1. Check for SAVE action: <ACTION:SAVE ...> or <MEMORY_SAVE ...>
            var saveMatch = Regex.Match(rawText, @"<(?:ACTION:SAVE|MEMORY_SAVE)\b([^>]*?)(?:/>|>.*?</(?:ACTION:SAVE|MEMORY_SAVE)>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (saveMatch.Success)
            {
                string tagContent = saveMatch.Value;
                string attrs = saveMatch.Groups[1].Value;

                string category = ExtractRegexAttr(attrs, "category") ?? "Note";
                string title = ExtractRegexAttr(attrs, "title") ?? "Saved Item";
                string detail = ExtractRegexAttr(attrs, "detail") ?? "";

                if (string.IsNullOrWhiteSpace(detail) && (tagContent.Contains("</ACTION:SAVE>") || tagContent.Contains("</MEMORY_SAVE>")))
                {
                    int bodyStart = tagContent.IndexOf('>') + 1;
                    int bodyEnd = tagContent.LastIndexOf('<');
                    if (bodyEnd > bodyStart) detail = tagContent.Substring(bodyStart, bodyEnd - bodyStart).Trim();
                }

                cardItem = AiMemoryDatabase.Instance.SaveMemory(category, title, detail, attachedImagePath);
            }

            // 2. Check for DELETE action: <ACTION:DELETE ...>
            var deleteMatch = Regex.Match(rawText, @"<ACTION:DELETE\b([^>]*?)(?:/>|>.*?</ACTION:DELETE>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (deleteMatch.Success)
            {
                string attrs = deleteMatch.Groups[1].Value;
                string idStr = ExtractRegexAttr(attrs, "id") ?? "";
                string title = ExtractRegexAttr(attrs, "title") ?? "";

                if (int.TryParse(idStr, out int delId))
                {
                    memoryDeleted = AiMemoryDatabase.Instance.DeleteMemory(delId);
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    memoryDeleted = AiMemoryDatabase.Instance.DeleteMemoryByTitle(title);
                }
            }

            // 3. Check for SHOW action: <ACTION:SHOW ...> or <MEMORY_SHOW ...>
            var showMatch = Regex.Match(rawText, @"<(?:ACTION:SHOW|MEMORY_SHOW)\b([^>]*?)(?:/>|>.*?</(?:ACTION:SHOW|MEMORY_SHOW)>|>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (showMatch.Success && cardItem == null)
            {
                string attrs = showMatch.Groups[1].Value;
                string idStr = ExtractRegexAttr(attrs, "id") ?? "";
                if (int.TryParse(idStr, out int showId))
                {
                    cardItem = allMemories.FirstOrDefault(m => m.Id == showId);
                }
                if (cardItem == null)
                {
                    string title = ExtractRegexAttr(attrs, "title") ?? "";
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        cardItem = allMemories.FirstOrDefault(m => m.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (cardItem == null && allMemories.Count > 0)
                {
                    cardItem = allMemories.First();
                }
            }

            // 4. Fallback: If no card yet, check if raw response specifically speaks about an item from memory
            if (cardItem == null && allMemories.Count > 0 && !memoryDeleted)
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

            // 5. Complete Payload & Markup Stripping (Absolute Zero Leaks!)
            string cleanText = rawText;
            cleanText = Regex.Replace(cleanText, @"```(?:xml|json)?\s*<(?:ACTION|MEMORY)[^>]*>.*?```", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleanText = Regex.Replace(cleanText, @"<(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)\b[^>]*?(?:/>|>.*?</(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)>|>)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleanText = Regex.Replace(cleanText, @"</?(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)[^>]*>", "", RegexOptions.IgnoreCase);
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
