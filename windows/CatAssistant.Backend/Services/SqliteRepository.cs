using CatAssistant.Backend.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CatAssistant.Backend.Services;

public sealed class SqliteRepository
{
    private readonly string _connectionString;

    public SqliteRepository(string? databasePath = null)
    {
        if (databasePath is null)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoloToy", "CatAssistant");
            Directory.CreateDirectory(directory);
            databasePath = Path.Combine(directory, "cat-assistant.db");
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var command = db.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            -- Legacy demo data is intentionally retained and not imported.
            CREATE TABLE IF NOT EXISTS conversations (id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, last_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id INTEGER NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL, created_utc TEXT NOT NULL, FOREIGN KEY(conversation_id) REFERENCES conversations(id));

            CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations_v2 (
                id TEXT PRIMARY KEY,
                mode TEXT NOT NULL CHECK(mode IN ('chat','work')),
                title TEXT NOT NULL,
                title_source TEXT NOT NULL CHECK(title_source IN ('fallback','generated','user')),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                archived_utc TEXT NULL,
                origin_conversation_id TEXT NULL,
                runtime_metadata TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS messages_v2 (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('user','assistant','system')),
                source TEXT NOT NULL CHECK(source IN ('desktop','device','runtime')),
                status TEXT NOT NULL CHECK(status IN ('complete','streaming','interrupted','failed')),
                sequence INTEGER NOT NULL,
                content TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                run_id TEXT NULL,
                metadata TEXT NULL,
                UNIQUE(conversation_id, sequence),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                runtime TEXT NOT NULL,
                state TEXT NOT NULL,
                trigger_message_id TEXT NOT NULL,
                assistant_message_id TEXT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                finish_reason TEXT NULL,
                error_code TEXT NULL,
                usage_json TEXT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS citations (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                snippet TEXT NULL,
                source_name TEXT NULL,
                FOREIGN KEY(message_id) REFERENCES messages_v2(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS app_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work_requests (
                id TEXT PRIMARY KEY,
                source_conversation_id TEXT NOT NULL,
                source_message_id TEXT NOT NULL,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                context_summary TEXT NOT NULL,
                state TEXT NOT NULL,
                target_conversation_id TEXT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS work_context_items (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NULL,
                ordinal INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('message','tool_call','tool_result')),
                role TEXT NULL CHECK(role IS NULL OR role IN ('user','assistant','system','tool')),
                message_id TEXT NULL,
                tool_call_id TEXT NULL,
                name TEXT NULL,
                payload_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(conversation_id, ordinal),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE SET NULL,
                FOREIGN KEY(message_id) REFERENCES messages_v2(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS work_tool_calls (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                round_id TEXT NOT NULL,
                batch_id TEXT NOT NULL,
                tool_call_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                tool_name TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('pending','waiting_approval','running','completed','failed','rejected','cancelled')),
                result_json TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                created_utc TEXT NOT NULL,
                started_utc TEXT NULL,
                completed_utc TEXT NULL,
                duration_ms INTEGER NULL,
                UNIQUE(run_id, tool_call_id),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS work_approvals (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                tool_call_id TEXT NOT NULL,
                operation_json TEXT NOT NULL,
                scope TEXT NOT NULL CHECK(scope IN ('single','task_directory')),
                target_path TEXT NULL,
                capability TEXT NOT NULL CHECK(capability IN ('read','write','execute')),
                state TEXT NOT NULL CHECK(state IN ('pending','approved','rejected','cancelled','expired')),
                requested_utc TEXT NOT NULL,
                resolved_utc TEXT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS work_artifacts (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NULL,
                path TEXT NOT NULL,
                label TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS idx_conversations_v2_updated ON conversations_v2(updated_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_v2_conversation ON messages_v2(conversation_id, sequence);
            CREATE INDEX IF NOT EXISTS idx_work_context_items_conversation ON work_context_items(conversation_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_work_tool_calls_run ON work_tool_calls(run_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_work_approvals_conversation ON work_approvals(conversation_id, state, requested_utc);
            CREATE INDEX IF NOT EXISTS idx_work_artifacts_conversation ON work_artifacts(conversation_id, created_utc);
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        await EnsureConversationPinColumnAsync(db);
        await RecoverInterruptedRunsAsync(db);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var values = new Dictionary<string, string>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values[reader.GetString(0)] = reader.GetString(1);
        return new AppSettings
        {
            AsrModel = values.GetValueOrDefault("asr_model", "qwen-audio-3.1-asr-flash"),
            ChatModel = values.GetValueOrDefault("chat_model", "qwen-plus"),
            LastBluetoothAddress = ulong.TryParse(values.GetValueOrDefault("last_address"), out var address) ? address : null,
            LastDeviceName = values.GetValueOrDefault("last_name"),
            ScreenTimeoutSeconds = uint.TryParse(values.GetValueOrDefault("screen_timeout"), out var timeout) ? timeout : 60,
            AutoReconnect = !values.TryGetValue("auto_reconnect", out var reconnect) || reconnect == "1",
            WebSearchEnabled = !values.TryGetValue("web_search", out var search) || search == "1"
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var values = new Dictionary<string, string>
        {
            ["asr_model"] = settings.AsrModel,
            ["chat_model"] = settings.ChatModel,
            ["screen_timeout"] = settings.ScreenTimeoutSeconds.ToString(),
            ["auto_reconnect"] = settings.AutoReconnect ? "1" : "0",
            ["web_search"] = settings.WebSearchEnabled ? "1" : "0",
            ["last_address"] = settings.LastBluetoothAddress?.ToString() ?? "",
            ["last_name"] = settings.LastDeviceName ?? ""
        };
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        foreach (var item in values)
        {
            var command = db.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            command.Parameters.AddWithValue("$key", item.Key);
            command.Parameters.AddWithValue("$value", item.Value);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<ConversationSummary> CreateConversationAsync(string mode = "chat", string? originConversationId = null)
    {
        if (mode is not ("chat" or "work")) throw new ArgumentOutOfRangeException(nameof(mode));
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationSummary(Guid.NewGuid().ToString("N"), mode, "新对话", "fallback", now, now, false);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations_v2(id,mode,title,title_source,created_utc,updated_utc,origin_conversation_id)
            VALUES($id,$mode,$title,$source,$created,$updated,$origin)
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$mode", conversation.Mode);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$source", conversation.TitleSource);
        command.Parameters.AddWithValue("$created", Format(now));
        command.Parameters.AddWithValue("$updated", Format(now));
        command.Parameters.AddWithValue("$origin", (object?)originConversationId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return conversation;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync()
    {
        var result = new List<ConversationSummary>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,mode,title,title_source,created_utc,updated_utc,pinned_utc FROM conversations_v2 WHERE archived_utc IS NULL ORDER BY pinned_utc IS NULL, pinned_utc DESC, updated_utc DESC";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<ConversationSummary?> GetConversationAsync(string id)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,mode,title,title_source,created_utc,updated_utc,pinned_utc FROM conversations_v2 WHERE id=$id AND archived_utc IS NULL";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversation(reader) : null;
    }

    public async Task<ConversationDetail?> GetConversationDetailAsync(string id, string? activeRunId = null)
    {
        var conversation = await GetConversationAsync(id);
        if (conversation is null) return null;
        var messages = await LoadMessagesAsync(id);
        var citations = await LoadCitationsAsync(messages.Select(message => message.Id).ToArray());
        return new ConversationDetail(conversation, messages, citations, activeRunId);
    }

    public async Task RenameConversationAsync(string id, string title, string source = "user")
    {
        title = NormalizeTitle(title);
        if (source is not ("fallback" or "generated" or "user")) throw new ArgumentOutOfRangeException(nameof(source));
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE conversations_v2 SET title=$title,title_source=$source,updated_utc=$now WHERE id=$id AND archived_utc IS NULL";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("对话不存在。");
    }

    public async Task<bool> TrySetGeneratedTitleAsync(string id, string title)
    {
        title = NormalizeTitle(title);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE conversations_v2 SET title=$title,title_source='generated',updated_utc=$now WHERE id=$id AND archived_utc IS NULL AND title_source<>'user'";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task SetConversationPinnedAsync(string id, bool pinned)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE conversations_v2 SET pinned_utc=$pinned WHERE id=$id AND archived_utc IS NULL";
        command.Parameters.AddWithValue("$pinned", pinned ? Format(DateTimeOffset.UtcNow) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("对话不存在。");
    }

    public async Task DeleteConversationAsync(string id)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE conversations_v2 SET archived_utc=$now WHERE id=$id AND archived_utc IS NULL";
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("对话不存在。");
    }

    public async Task<ConversationMessage> AddMessageAsync(string conversationId, string role, string source, string status, string content, string? runId = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var sequenceCommand = db.CreateCommand();
        sequenceCommand.Transaction = (SqliteTransaction)transaction;
        sequenceCommand.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM messages_v2 WHERE conversation_id=$id";
        sequenceCommand.Parameters.AddWithValue("$id", conversationId);
        var sequence = (long)(await sequenceCommand.ExecuteScalarAsync() ?? 1L);
        var message = new ConversationMessage(Guid.NewGuid().ToString("N"), conversationId, role, source, status, sequence, content, now, status == "streaming" ? null : now, runId);
        var insert = db.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO messages_v2(id,conversation_id,role,source,status,sequence,content,created_utc,completed_utc,run_id)
            VALUES($id,$conversation,$role,$source,$status,$sequence,$content,$created,$completed,$run)
            """;
        insert.Parameters.AddWithValue("$id", message.Id);
        insert.Parameters.AddWithValue("$conversation", conversationId);
        insert.Parameters.AddWithValue("$role", role);
        insert.Parameters.AddWithValue("$source", source);
        insert.Parameters.AddWithValue("$status", status);
        insert.Parameters.AddWithValue("$sequence", sequence);
        insert.Parameters.AddWithValue("$content", content);
        insert.Parameters.AddWithValue("$created", Format(now));
        insert.Parameters.AddWithValue("$completed", message.CompletedAt is null ? DBNull.Value : Format(message.CompletedAt.Value));
        insert.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
        var touch = db.CreateCommand();
        touch.Transaction = (SqliteTransaction)transaction;
        touch.CommandText = "UPDATE conversations_v2 SET updated_utc=$now WHERE id=$id";
        touch.Parameters.AddWithValue("$now", Format(now));
        touch.Parameters.AddWithValue("$id", conversationId);
        if (await touch.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("对话不存在。");
        await transaction.CommitAsync();
        return message;
    }

    public async Task UpdateMessageAsync(string messageId, string content, string status)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE messages_v2 SET content=$content,status=$status,completed_utc=CASE WHEN $status='streaming' THEN NULL ELSE $now END WHERE id=$id";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", messageId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ReplaceCitationsAsync(string messageId, IReadOnlyList<CitationRecord> citations)
    {
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var delete = db.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM citations WHERE message_id=$message";
        delete.Parameters.AddWithValue("$message", messageId);
        await delete.ExecuteNonQueryAsync();
        foreach (var citation in citations)
        {
            var insert = db.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO citations(id,message_id,ordinal,title,url,snippet,source_name) VALUES($id,$message,$ordinal,$title,$url,$snippet,$source)";
            insert.Parameters.AddWithValue("$id", citation.Id);
            insert.Parameters.AddWithValue("$message", messageId);
            insert.Parameters.AddWithValue("$ordinal", citation.Ordinal);
            insert.Parameters.AddWithValue("$title", citation.Title);
            insert.Parameters.AddWithValue("$url", citation.Url);
            insert.Parameters.AddWithValue("$snippet", (object?)citation.Snippet ?? DBNull.Value);
            insert.Parameters.AddWithValue("$source", (object?)citation.SourceName ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task RemoveMessageIfEmptyAsync(string messageId)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "DELETE FROM messages_v2 WHERE id=$id AND length(content)=0";
        command.Parameters.AddWithValue("$id", messageId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ConversationMessage>> LoadMessagesAsync(string conversationId)
    {
        var result = new List<ConversationMessage>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,role,source,status,sequence,content,created_utc,completed_utc,run_id FROM messages_v2 WHERE conversation_id=$id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadMessage(reader));
        return result;
    }

    public async Task CreateRunAsync(string runId, string conversationId, string triggerMessageId)
        => await CreateRunAsync(runId, conversationId, triggerMessageId, "chat");

    public async Task CreateRunAsync(string runId, string conversationId, string triggerMessageId, string runtime)
    {
        if (runtime is not ("chat" or "work")) throw new ArgumentOutOfRangeException(nameof(runtime));
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "INSERT INTO runs(id,conversation_id,runtime,state,trigger_message_id,started_utc) VALUES($id,$conversation,$runtime,'starting',$trigger,$now)";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$runtime", runtime);
        command.Parameters.AddWithValue("$trigger", triggerMessageId);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<WorkContextItem> AddWorkContextItemAsync(
        string conversationId,
        string? runId,
        string kind,
        string? role,
        string payloadJson,
        string? messageId = null,
        string? toolCallId = null,
        string? name = null)
    {
        if (kind is not ("message" or "tool_call" or "tool_result")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (role is not null and not ("user" or "assistant" or "system" or "tool")) throw new ArgumentOutOfRangeException(nameof(role));
        EnsureJson(payloadJson, nameof(payloadJson));
        var now = DateTimeOffset.UtcNow;
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var sequence = db.CreateCommand();
        sequence.Transaction = (SqliteTransaction)transaction;
        sequence.CommandText = "SELECT COALESCE(MAX(ordinal),0)+1 FROM work_context_items WHERE conversation_id=$conversation";
        sequence.Parameters.AddWithValue("$conversation", conversationId);
        var ordinal = (long)(await sequence.ExecuteScalarAsync() ?? 1L);
        var item = new WorkContextItem(Guid.NewGuid().ToString("N"), conversationId, runId, ordinal, kind, role, messageId, toolCallId, name, payloadJson, now);
        var insert = db.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO work_context_items(id,conversation_id,run_id,ordinal,kind,role,message_id,tool_call_id,name,payload_json,created_utc)
            VALUES($id,$conversation,$run,$ordinal,$kind,$role,$message,$toolCall,$name,$payload,$created)
            """;
        insert.Parameters.AddWithValue("$id", item.Id);
        insert.Parameters.AddWithValue("$conversation", conversationId);
        insert.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
        insert.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$toolCall", (object?)toolCallId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$created", Format(now));
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return item;
    }

    public async Task<IReadOnlyList<WorkContextItem>> LoadWorkContextItemsAsync(string conversationId)
    {
        var result = new List<WorkContextItem>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,run_id,ordinal,kind,role,message_id,tool_call_id,name,payload_json,created_utc FROM work_context_items WHERE conversation_id=$conversation ORDER BY ordinal";
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new WorkContextItem(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), Parse(reader.GetString(10))));
        return result;
    }

    public async Task<IReadOnlyList<ConversationMessage>> LoadMessagesWithoutWorkContextAsync(string conversationId)
    {
        var result = new List<ConversationMessage>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT m.id,m.conversation_id,m.role,m.source,m.status,m.sequence,m.content,m.created_utc,m.completed_utc,m.run_id
            FROM messages_v2 m
            LEFT JOIN work_context_items w ON w.message_id=m.id
            WHERE m.conversation_id=$conversation AND w.id IS NULL
              AND ((m.role='user' AND m.status='complete') OR (m.role='assistant' AND m.status IN ('complete','interrupted')))
            ORDER BY m.sequence
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadMessage(reader));
        return result;
    }

    public async Task AttachAssistantToRunAsync(string runId, string messageId)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE runs SET assistant_message_id=$message,state='generating' WHERE id=$id";
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task FinishRunAsync(string runId, string state, string finishReason, string? errorCode = null)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE runs SET state=$state,finish_reason=$reason,error_code=$error,completed_utc=$now WHERE id=$id AND completed_utc IS NULL";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", finishReason);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<WorkToolCallRecord> CreateWorkToolCallAsync(
        string conversationId,
        string runId,
        string roundId,
        string batchId,
        string toolCallId,
        int ordinal,
        string toolName,
        string argumentsJson)
    {
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("工具名不能为空。", nameof(toolName));
        EnsureJson(argumentsJson, nameof(argumentsJson));
        var now = DateTimeOffset.UtcNow;
        var record = new WorkToolCallRecord(
            Guid.NewGuid().ToString("N"), conversationId, runId, roundId, batchId, toolCallId, ordinal,
            toolName, argumentsJson, "pending", null, null, null, now, null, null, null);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO work_tool_calls(id,conversation_id,run_id,round_id,batch_id,tool_call_id,ordinal,tool_name,arguments_json,state,created_utc)
            VALUES($id,$conversation,$run,$round,$batch,$toolCall,$ordinal,$tool,$arguments,'pending',$created)
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$round", roundId);
        command.Parameters.AddWithValue("$batch", batchId);
        command.Parameters.AddWithValue("$toolCall", toolCallId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$tool", toolName);
        command.Parameters.AddWithValue("$arguments", argumentsJson);
        command.Parameters.AddWithValue("$created", Format(now));
        await command.ExecuteNonQueryAsync();
        return record;
    }

    public async Task UpdateWorkToolCallAsync(
        string id,
        string state,
        string? resultJson = null,
        string? errorCode = null,
        string? errorMessage = null,
        long? durationMilliseconds = null)
    {
        if (state is not ("waiting_approval" or "running" or "completed" or "failed" or "rejected" or "cancelled"))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (resultJson is not null) EnsureJson(resultJson, nameof(resultJson));
        var now = Format(DateTimeOffset.UtcNow);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            UPDATE work_tool_calls
            SET state=$state,
                result_json=$result,
                error_code=$errorCode,
                error_message=$errorMessage,
                started_utc=CASE WHEN $state='running' AND started_utc IS NULL THEN $now ELSE started_utc END,
                completed_utc=CASE WHEN $state IN ('completed','failed','rejected','cancelled') THEN $now ELSE completed_utc END,
                duration_ms=$duration
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$duration", (object?)durationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("工具调用不存在。");
    }

    public async Task<WorkApprovalRecord> CreateWorkApprovalAsync(
        string conversationId,
        string runId,
        string toolCallId,
        string operationJson,
        string scope,
        string? targetPath,
        string capability)
    {
        if (scope is not ("single" or "task_directory")) throw new ArgumentOutOfRangeException(nameof(scope));
        if (capability is not ("read" or "write" or "execute")) throw new ArgumentOutOfRangeException(nameof(capability));
        EnsureJson(operationJson, nameof(operationJson));
        var now = DateTimeOffset.UtcNow;
        var approval = new WorkApprovalRecord(Guid.NewGuid().ToString("N"), conversationId, runId, toolCallId, operationJson, scope, targetPath, capability, "pending", now, null);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO work_approvals(id,conversation_id,run_id,tool_call_id,operation_json,scope,target_path,capability,state,requested_utc)
            VALUES($id,$conversation,$run,$toolCall,$operation,$scope,$path,$capability,'pending',$requested)
            """;
        command.Parameters.AddWithValue("$id", approval.Id);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$toolCall", toolCallId);
        command.Parameters.AddWithValue("$operation", operationJson);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$path", (object?)targetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$capability", capability);
        command.Parameters.AddWithValue("$requested", Format(now));
        await command.ExecuteNonQueryAsync();
        return approval;
    }

    public async Task ResolveWorkApprovalAsync(string id, string state)
    {
        if (state is not ("approved" or "rejected" or "cancelled" or "expired")) throw new ArgumentOutOfRangeException(nameof(state));
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE work_approvals SET state=$state,resolved_utc=$now WHERE id=$id AND state='pending'";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new InvalidOperationException("审批不存在或已经处理。");
    }

    public async Task<IReadOnlyList<WorkApprovalRecord>> ListPendingWorkApprovalsAsync(string conversationId)
    {
        var result = new List<WorkApprovalRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,run_id,tool_call_id,operation_json,scope,target_path,capability,state,requested_utc,resolved_utc FROM work_approvals WHERE conversation_id=$conversation AND state='pending' ORDER BY requested_utc";
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new WorkApprovalRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetString(8), Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10))));
        return result;
    }

    public async Task<WorkArtifactRecord> AddWorkArtifactAsync(string conversationId, string? runId, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("成果路径不能为空。", nameof(path));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("成果名称不能为空。", nameof(label));
        var artifact = new WorkArtifactRecord(Guid.NewGuid().ToString("N"), conversationId, runId, path, label.Trim(), DateTimeOffset.UtcNow);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "INSERT INTO work_artifacts(id,conversation_id,run_id,path,label,created_utc) VALUES($id,$conversation,$run,$path,$label,$created)";
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$label", artifact.Label);
        command.Parameters.AddWithValue("$created", Format(artifact.CreatedAt));
        await command.ExecuteNonQueryAsync();
        return artifact;
    }

    public async Task<IReadOnlyList<WorkArtifactRecord>> ListWorkArtifactsAsync(string conversationId)
    {
        var result = new List<WorkArtifactRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,run_id,path,label,created_utc FROM work_artifacts WHERE conversation_id=$conversation ORDER BY created_utc";
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new WorkArtifactRecord(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5))));
        return result;
    }

    public async Task<string?> GetAppStateAsync(string key)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task SetAppStateAsync(string key, string? value)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        if (value is null)
        {
            command.CommandText = "DELETE FROM app_state WHERE key=$key";
        }
        else
        {
            command.CommandText = "INSERT INTO app_state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            command.Parameters.AddWithValue("$value", value);
        }
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<CitationRecord>> LoadCitationsAsync(IReadOnlyCollection<string> messageIds)
    {
        if (messageIds.Count == 0) return [];
        var result = new List<CitationRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        var names = messageIds.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"SELECT id,message_id,ordinal,title,url,snippet,source_name FROM citations WHERE message_id IN ({string.Join(',', names)}) ORDER BY message_id,ordinal";
        var position = 0;
        foreach (var messageId in messageIds) command.Parameters.AddWithValue(names[position++], messageId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new CitationRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    private static async Task RecoverInterruptedRunsAsync(SqliteConnection db)
    {
        await using var transaction = await db.BeginTransactionAsync();
        var messages = db.CreateCommand();
        messages.Transaction = (SqliteTransaction)transaction;
        messages.CommandText = """
            UPDATE messages_v2 SET status='interrupted',completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE status='streaming' AND length(content)>0;
            DELETE FROM messages_v2 WHERE status='streaming' AND length(content)=0;
            """;
        await messages.ExecuteNonQueryAsync();
        var runs = db.CreateCommand();
        runs.Transaction = (SqliteTransaction)transaction;
        runs.CommandText = """
            UPDATE runs SET state='interrupted',finish_reason='interrupted',completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE state IN ('starting','searching','generating') AND completed_utc IS NULL;
            """;
        await runs.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync();
        return db;
    }

    private static ConversationSummary ReadConversation(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)), Parse(reader.GetString(5)), !reader.IsDBNull(6));

    private static ConversationMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
        Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9));

    private static string NormalizeTitle(string title)
    {
        title = string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) throw new ArgumentException("标题不能为空。", nameof(title));
        return title.Length <= 80 ? title : title[..80];
    }

    internal static string FallbackTitle(string content)
    {
        var title = string.Join(' ', content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) return "新对话";
        return title.Length <= 28 ? title : title[..28] + "…";
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O");
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value);

    private static void EnsureJson(string value, string parameterName)
    {
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException error) { throw new ArgumentException("值必须是有效 JSON。", parameterName, error); }
    }

    private static async Task EnsureConversationPinColumnAsync(SqliteConnection db)
    {
        var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info(conversations_v2)";
        var hasPinnedUtc = false;
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                if (reader.GetString(1) == "pinned_utc") hasPinnedUtc = true;
        }
        if (!hasPinnedUtc)
        {
            var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE conversations_v2 ADD COLUMN pinned_utc TEXT NULL";
            await alter.ExecuteNonQueryAsync();
        }
        var index = db.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_conversations_v2_pinned ON conversations_v2(pinned_utc DESC, updated_utc DESC)";
        await index.ExecuteNonQueryAsync();
    }
}
