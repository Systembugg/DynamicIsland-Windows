using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DynamicIsland.AI
{
    public class MemoryItem
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Category { get; set; } = "Note"; // Movie, Anime, Series, Note, Task, Screen, General
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Keywords { get; set; } = "";
        public string? ImagePath { get; set; }

        public string DisplayCategoryIcon => Category.ToLowerInvariant() switch
        {
            "movie" or "film" or "cinema" => "🎬",
            "anime" or "manga" => "⛩️",
            "series" or "show" or "tv" => "📺",
            "task" or "reminder" or "todo" => "⏰",
            "screen" or "screenshot" or "image" => "📸",
            "code" or "dev" => "💻",
            _ => "📝"
        };

        public string DisplaySubtitle => $"{Category} • {CreatedAt.ToString("MMM d, h:mm tt")}";
    }

    public class AiMemoryDatabase
    {
        private static AiMemoryDatabase? _instance;
        public static AiMemoryDatabase Instance => _instance ??= new AiMemoryDatabase();

        private readonly string _dbPath;
        private readonly string _connectionString;

        public AiMemoryDatabase()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "DynamicIsland");
            Directory.CreateDirectory(folder);

            _dbPath = Path.Combine(folder, "ai_memory.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS Memories (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CreatedAt TEXT NOT NULL,
                        Category TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Detail TEXT,
                        Keywords TEXT,
                        ImagePath TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_category ON Memories(Category);
                ";

                using var command = new SqliteCommand(createTableQuery, connection);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiMemoryDatabase] Init Error: {ex.Message}");
            }
        }

        public MemoryItem SaveMemory(string category, string title, string detail, string? imagePath = null, string? keywords = null)
        {
            var item = new MemoryItem
            {
                CreatedAt = DateTime.Now,
                Category = string.IsNullOrWhiteSpace(category) ? "Note" : category.Trim(),
                Title = string.IsNullOrWhiteSpace(title) ? "Saved Note" : title.Trim(),
                Detail = detail ?? "",
                Keywords = keywords ?? "",
                ImagePath = imagePath
            };

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // Check for existing duplicate in same category
                string checkQuery = @"
                    SELECT Id, CreatedAt, Category, Title, Detail, Keywords, ImagePath
                    FROM Memories
                    WHERE LOWER(Category) = LOWER(@category) AND (LOWER(Title) = LOWER(@title) OR LOWER(Title) LIKE @titleLike)
                    ORDER BY Id DESC LIMIT 1;
                ";
                using (var checkCmd = new SqliteCommand(checkQuery, connection))
                {
                    checkCmd.Parameters.AddWithValue("@category", item.Category);
                    checkCmd.Parameters.AddWithValue("@title", item.Title);
                    checkCmd.Parameters.AddWithValue("@titleLike", $"%{item.Title}%");
                    using var r = checkCmd.ExecuteReader();
                    if (r.Read())
                    {
                        var existing = ReadItem(r);
                        // If existing found and has same or close title, update details if new detail is richer
                        if (!string.IsNullOrWhiteSpace(item.Detail) && item.Detail.Length > existing.Detail.Length)
                        {
                            existing.Detail = item.Detail;
                            if (!string.IsNullOrEmpty(item.ImagePath)) existing.ImagePath = item.ImagePath;
                            string updateSql = "UPDATE Memories SET Detail = @detail, ImagePath = COALESCE(@imagePath, ImagePath) WHERE Id = @id;";
                            using var uCmd = new SqliteCommand(updateSql, connection);
                            uCmd.Parameters.AddWithValue("@detail", existing.Detail);
                            uCmd.Parameters.AddWithValue("@imagePath", (object?)existing.ImagePath ?? DBNull.Value);
                            uCmd.Parameters.AddWithValue("@id", existing.Id);
                            uCmd.ExecuteNonQuery();
                        }
                        return existing;
                    }
                }

                string query = @"
                    INSERT INTO Memories (CreatedAt, Category, Title, Detail, Keywords, ImagePath)
                    VALUES (@createdAt, @category, @title, @detail, @keywords, @imagePath);
                    SELECT last_insert_rowid();
                ";

                using var cmd = new SqliteCommand(query, connection);
                cmd.Parameters.AddWithValue("@createdAt", item.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@category", item.Category);
                cmd.Parameters.AddWithValue("@title", item.Title);
                cmd.Parameters.AddWithValue("@detail", item.Detail);
                cmd.Parameters.AddWithValue("@keywords", item.Keywords);
                cmd.Parameters.AddWithValue("@imagePath", (object?)item.ImagePath ?? DBNull.Value);

                item.Id = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiMemoryDatabase] Save Error: {ex.Message}");
            }

            return item;
        }

        public List<MemoryItem> GetAllMemories(int limit = 35)
        {
            var results = new List<MemoryItem>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                string query = @"
                    SELECT Id, CreatedAt, Category, Title, Detail, Keywords, ImagePath
                    FROM Memories
                    ORDER BY Id DESC
                    LIMIT @limit;
                ";

                using var cmd = new SqliteCommand(query, connection);
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadItem(reader));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiMemoryDatabase] GetAll Error: {ex.Message}");
            }

            return results;
        }

        public List<MemoryItem> SearchMemories(string searchQuery, int limit = 10)
        {
            var results = new List<MemoryItem>();
            if (string.IsNullOrWhiteSpace(searchQuery)) return GetRecentMemories(limit);

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var words = searchQuery.Split(new[] { ' ', ',', '.', '?', '!', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Where(w => w.Length > 2)
                                       .Take(5)
                                       .ToList();

                var sb = new System.Text.StringBuilder();
                sb.Append("SELECT Id, CreatedAt, Category, Title, Detail, Keywords, ImagePath FROM Memories WHERE ");

                using var cmd = new SqliteCommand();
                cmd.Connection = connection;

                if (words.Count == 0)
                {
                    sb.Append("Title LIKE @term OR Detail LIKE @term OR Category LIKE @term OR Keywords LIKE @term");
                    cmd.Parameters.AddWithValue("@term", $"%{searchQuery.Trim()}%");
                }
                else
                {
                    var clauses = new List<string>();
                    for (int i = 0; i < words.Count; i++)
                    {
                        string pName = $"@w{i}";
                        clauses.Add($"(Title LIKE {pName} OR Detail LIKE {pName} OR Category LIKE {pName} OR Keywords LIKE {pName})");
                        cmd.Parameters.AddWithValue(pName, $"%{words[i]}%");
                    }
                    sb.Append(string.Join(" OR ", clauses));
                }

                sb.Append(" ORDER BY Id DESC LIMIT @limit;");
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.CommandText = sb.ToString();

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadItem(reader));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiMemoryDatabase] Search Error: {ex.Message}");
            }

            if (results.Count == 0) return GetRecentMemories(limit);
            return results;
        }

        public List<MemoryItem> GetRecentMemories(int limit = 10)
        {
            return GetAllMemories(limit);
        }

        public List<MemoryItem> GetMemoriesByCategory(string category, int limit = 15)
        {
            var results = new List<MemoryItem>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                string query = @"
                    SELECT Id, CreatedAt, Category, Title, Detail, Keywords, ImagePath
                    FROM Memories
                    WHERE Category LIKE @cat
                    ORDER BY Id DESC
                    LIMIT @limit;
                ";

                using var cmd = new SqliteCommand(query, connection);
                cmd.Parameters.AddWithValue("@cat", $"%{category}%");
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadItem(reader));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiMemoryDatabase] GetByCategory Error: {ex.Message}");
            }

            return results;
        }

        private MemoryItem ReadItem(SqliteDataReader reader)
        {
            return new MemoryItem
            {
                Id = reader.GetInt32(0),
                CreatedAt = DateTime.TryParse(reader.GetString(1), out var dt) ? dt : DateTime.Now,
                Category = reader.GetString(2),
                Title = reader.GetString(3),
                Detail = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Keywords = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ImagePath = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }
    }
}
