using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
                string configPath = System.IO.Path.Combine(folder, "ai_config.json");
                if (System.IO.File.Exists(configPath))
                {
                    string json = System.IO.File.ReadAllText(configPath);
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
                // 1. Detect if prompt is asking to recall past memories
                var relevantMemories = SearchContextMemories(userPrompt);

                // 2. Build system instructions
                string memoryContext = "";
                if (relevantMemories.Count > 0)
                {
                    memoryContext = "\nExisting User Saved Memories / Watchlist Items:\n" +
                        string.Join("\n", relevantMemories.Select(m => $"- [ID:{m.Id}|{m.Category}|{m.CreatedAt:yyyy-MM-dd}] Title: {m.Title} | Details: {m.Detail}"));
                }

                string systemInstruction = @"You are the personal AI companion integrated inside Windows Dynamic Island.
You are smart, concise, and helpful. You manage the user's personal memory vault (Movies, Anime, Series, Tasks, Notes, Screen Context).
Keep your direct spoken response brief and friendly (1-3 sentences maximum suitable for a compact Dynamic Island UI).

ACTION TAGS:
If the user wants to remember/save/add something (e.g. 'add to watchlist', 'remember this', 'save this movie/anime/note'):
Output your friendly response, followed by a memory save tag at the end in this format:
<MEMORY_SAVE category=""Movie|Anime|Series|Task|Note|Screen|General"" title=""Title Name"" detail=""Any details or synopsis""/>

If the user is asking about an item that exists in their memories, mention it naturally and include:
<MEMORY_SHOW id=""matched_id""/>

Respond in the user's language (English, Hindi, or Hinglish as prompted)." + memoryContext;

                // 3. Build Gemini Request Payload
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

                // Parse tags
                var result = new AiResponseResult();
                result.Text = ExtractCleanTextAndHandleTags(fullResponse, attachedImagePath, relevantMemories, out var matched);
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

        private List<MemoryItem> SearchContextMemories(string prompt)
        {
            string pLower = prompt.ToLowerInvariant();
            if (pLower.Contains("watch") || pLower.Contains("movie") || pLower.Contains("film") || pLower.Contains("cinema"))
            {
                return AiMemoryDatabase.Instance.GetMemoriesByCategory("Movie", 8);
            }
            if (pLower.Contains("anime") || pLower.Contains("manga"))
            {
                return AiMemoryDatabase.Instance.GetMemoriesByCategory("Anime", 8);
            }
            if (pLower.Contains("series") || pLower.Contains("show") || pLower.Contains("tv"))
            {
                return AiMemoryDatabase.Instance.GetMemoriesByCategory("Series", 8);
            }
            if (pLower.Contains("remember") || pLower.Contains("saved") || pLower.Contains("note") || pLower.Contains("kya tha") || pLower.Contains("bata"))
            {
                var search = AiMemoryDatabase.Instance.SearchMemories(prompt, 6);
                if (search.Count == 0) search = AiMemoryDatabase.Instance.GetRecentMemories(6);
                return search;
            }

            return new List<MemoryItem>();
        }

        private string ExtractCleanTextAndHandleTags(string rawText, string? attachedImagePath, List<MemoryItem> contextMemories, out MemoryItem? cardItem)
        {
            cardItem = null;

            // Check for <MEMORY_SAVE ... />
            if (rawText.Contains("<MEMORY_SAVE"))
            {
                int start = rawText.IndexOf("<MEMORY_SAVE");
                int end = rawText.IndexOf("/>", start);
                if (end > start)
                {
                    string tag = rawText.Substring(start, end - start + 2);
                    string category = ExtractAttribute(tag, "category") ?? "Note";
                    string title = ExtractAttribute(tag, "title") ?? "New Memory";
                    string detail = ExtractAttribute(tag, "detail") ?? "";

                    cardItem = AiMemoryDatabase.Instance.SaveMemory(category, title, detail, attachedImagePath);

                    rawText = rawText.Remove(start, end - start + 2).Trim();
                    return rawText;
                }
            }

            // Check for <MEMORY_SHOW id="..." />
            if (rawText.Contains("<MEMORY_SHOW"))
            {
                int start = rawText.IndexOf("<MEMORY_SHOW");
                int end = rawText.IndexOf("/>", start);
                if (end > start)
                {
                    string tag = rawText.Substring(start, end - start + 2);
                    string idStr = ExtractAttribute(tag, "id") ?? "";
                    if (int.TryParse(idStr, out int id))
                    {
                        cardItem = contextMemories.FirstOrDefault(m => m.Id == id);
                    }
                    if (cardItem == null && contextMemories.Count > 0)
                    {
                        cardItem = contextMemories.First();
                    }

                    rawText = rawText.Remove(start, end - start + 2).Trim();
                    return rawText;
                }
            }

            // Fallback: if we had a strong context match and the answer references it
            if (contextMemories.Count > 0 && (rawText.ToLowerInvariant().Contains(contextMemories[0].Title.ToLowerInvariant()) || contextMemories.Count == 1))
            {
                cardItem = contextMemories[0];
            }

            return rawText.Trim();
        }

        private string? ExtractAttribute(string tag, string attrName)
        {
            string pattern = $"{attrName}=\"";
            int idx = tag.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return null;
            idx += pattern.Length;
            int end = tag.IndexOf("\"", idx);
            if (end == -1) return null;
            return tag.Substring(idx, end - idx);
        }
    }
}
