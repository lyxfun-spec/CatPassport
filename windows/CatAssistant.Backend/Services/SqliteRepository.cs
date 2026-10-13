using CatAssistant.Backend.Models;
using Microsoft.Data.Sqlite;

namespace CatAssistant.Backend.Services;

public sealed class SqliteRepository
{
    private readonly string _connectionString;

    public SqliteRepository()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoloToy", "CatAssistant");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "cat-assistant.db") }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations (id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, last_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id INTEGER NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL, created_utc TEXT NOT NULL, FOREIGN KEY(conversation_id) REFERENCES conversations(id));
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var values = new Dictionary<string, string>();
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var command = db.CreateCommand(); command.CommandText = "SELECT key, value FROM settings";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values[reader.GetString(0)] = reader.GetString(1);
        return new AppSettings
        {
            AsrModel = values.GetValueOrDefault("asr_model", "qwen-audio-3.1-asr-flash"),
            ChatModel = values.GetValueOrDefault("chat_model", "qwen-plus"),
            LastBluetoothAddress = ulong.TryParse(values.GetValueOrDefault("last_address"), out var address) ? address : null,
            LastDeviceName = values.GetValueOrDefault("last_name"),
            ScreenTimeoutSeconds = uint.TryParse(values.GetValueOrDefault("screen_timeout"), out var timeout) ? timeout : 60,
            AutoReconnect = !values.TryGetValue("auto_reconnect", out var reconnect) || reconnect == "1"
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var values = new Dictionary<string, string>
        {
            ["asr_model"] = settings.AsrModel, ["chat_model"] = settings.ChatModel,
            ["screen_timeout"] = settings.ScreenTimeoutSeconds.ToString(),
            ["auto_reconnect"] = settings.AutoReconnect ? "1" : "0",
            ["last_address"] = settings.LastBluetoothAddress?.ToString() ?? "",
            ["last_name"] = settings.LastDeviceName ?? ""
        };
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        foreach (var item in values)
        {
            var command = db.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            command.Parameters.AddWithValue("$key", item.Key); command.Parameters.AddWithValue("$value", item.Value);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<long> NewConversationAsync(DateTimeOffset now)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "INSERT INTO conversations(started_utc,last_utc) VALUES($now,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<(long Id, DateTimeOffset LastAt)?> LoadLatestConversationAsync()
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync();
        var command = db.CreateCommand(); command.CommandText = "SELECT id,last_utc FROM conversations ORDER BY last_utc DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1))) : null;
    }

    public async Task AddMessageAsync(long conversationId, ChatMessage message)
    {
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "INSERT INTO messages(conversation_id,role,content,created_utc) VALUES($id,$role,$content,$now); UPDATE conversations SET last_utc=$now WHERE id=$id";
        command.Parameters.AddWithValue("$id", conversationId); command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$content", message.Content); command.Parameters.AddWithValue("$now", message.CreatedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(long conversationId)
    {
        var result = new List<ChatMessage>();
        await using var db = new SqliteConnection(_connectionString); await db.OpenAsync();
        var command = db.CreateCommand(); command.CommandText = "SELECT role,content,created_utc FROM messages WHERE conversation_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))));
        return result;
    }
}
