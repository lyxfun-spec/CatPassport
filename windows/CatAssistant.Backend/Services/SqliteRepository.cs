using CatAssistant.Backend.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CatAssistant.Backend.Services;

public sealed partial class SqliteRepository
{
    private const string WorkRequestSelect = "SELECT id,source_conversation_id,source_message_id,source_run_id,tool_call_id,title,prompt,context_summary,state,target_conversation_id,error_message,created_utc,updated_utc,anchor_offset FROM work_requests";
    private const string ConversationSourceSelect = "SELECT id,conversation_id,ordinal,title,url,normalized_url,snippet,site_name,domain,first_tool_call_id,first_operation_id,first_seen_utc,updated_utc,published_time,authority,rerank_score,is_official,weather_kind FROM conversation_sources";
    // A conversation keeps this title and title_source='fallback' until a model-generated title lands, so
    // 'fallback' is the only durable marker for "still needs a title".
    internal const string UntitledTitle = "新对话";
    private readonly string _connectionString;
    private bool _ftsAvailable;

    public SqliteRepository(string? databasePath = null)
    {
        if (databasePath is null)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoloToy", "CatAssistant");
            Directory.CreateDirectory(directory);
            databasePath = Path.Combine(directory, "cat-assistant.db");
        }
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
    }

    public string DatabasePath { get; }

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
                source_run_id TEXT NOT NULL,
                tool_call_id TEXT NOT NULL,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                context_summary TEXT NOT NULL,
                state TEXT NOT NULL,
                target_conversation_id TEXT NULL,
                error_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                anchor_offset INTEGER NULL
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
            CREATE TABLE IF NOT EXISTS chat_tool_calls (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                tool_call_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('running','completed','failed','cancelled')),
                query TEXT NULL,
                query_count INTEGER NOT NULL DEFAULT 0,
                raw_result_count INTEGER NOT NULL DEFAULT 0,
                unique_result_count INTEGER NOT NULL DEFAULT 0,
                result_json TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                elapsed_ms INTEGER NULL,
                anchor_offset INTEGER NULL,
                new_source_ids_json TEXT NULL,
                created_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                UNIQUE(run_id, tool_call_id),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS conversation_sources (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                normalized_url TEXT NOT NULL,
                snippet TEXT NULL,
                site_name TEXT NULL,
                domain TEXT NOT NULL,
                first_tool_call_id TEXT NOT NULL,
                first_operation_id TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                published_time TEXT NULL,
                authority REAL NULL,
                rerank_score REAL NULL,
                is_official INTEGER NOT NULL DEFAULT 0,
                weather_kind TEXT NULL,
                UNIQUE(conversation_id, ordinal),
                UNIQUE(conversation_id, normalized_url),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS context_summaries (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                mode TEXT NOT NULL CHECK(mode IN ('chat','work')),
                version INTEGER NOT NULL,
                source_summary_id TEXT NULL,
                start_sequence INTEGER NOT NULL,
                end_sequence INTEGER NOT NULL,
                content TEXT NOT NULL,
                estimated_tokens INTEGER NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('validated','active','superseded','invalid')),
                created_utc TEXT NOT NULL,
                UNIQUE(conversation_id, version),
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(source_summary_id) REFERENCES context_summaries(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_context_state (
                conversation_id TEXT PRIMARY KEY,
                active_summary_id TEXT NULL,
                compressed_through_sequence INTEGER NOT NULL DEFAULT 0,
                search_watermark_sequence INTEGER NOT NULL DEFAULT 0,
                export_watermark_sequence INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(active_summary_id) REFERENCES context_summaries(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS request_diagnostics (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                run_id TEXT NULL,
                mode TEXT NOT NULL CHECK(mode IN ('chat','work','compaction')),
                model TEXT NOT NULL,
                event TEXT NOT NULL,
                estimated_input_tokens INTEGER NOT NULL,
                input_budget_tokens INTEGER NOT NULL,
                actual_input_tokens INTEGER NULL,
                actual_output_tokens INTEGER NULL,
                count_source TEXT NOT NULL,
                is_estimate INTEGER NOT NULL,
                detail TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS message_embeddings (
                message_id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                model TEXT NOT NULL,
                dimensions INTEGER NOT NULL,
                vector BLOB NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY(message_id) REFERENCES messages_v2(id) ON DELETE CASCADE,
                FOREIGN KEY(conversation_id) REFERENCES conversations_v2(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_conversations_v2_updated ON conversations_v2(updated_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_v2_conversation ON messages_v2(conversation_id, sequence);
            CREATE INDEX IF NOT EXISTS idx_work_context_items_conversation ON work_context_items(conversation_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_work_tool_calls_run ON work_tool_calls(run_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_work_approvals_conversation ON work_approvals(conversation_id, state, requested_utc);
            CREATE INDEX IF NOT EXISTS idx_work_artifacts_conversation ON work_artifacts(conversation_id, created_utc);
            CREATE INDEX IF NOT EXISTS idx_chat_tool_calls_conversation ON chat_tool_calls(conversation_id, created_utc);
            CREATE INDEX IF NOT EXISTS idx_chat_tool_calls_run ON chat_tool_calls(run_id, created_utc);
            CREATE INDEX IF NOT EXISTS idx_conversation_sources_conversation ON conversation_sources(conversation_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_context_summaries_conversation ON context_summaries(conversation_id, version DESC);
            CREATE INDEX IF NOT EXISTS idx_request_diagnostics_conversation ON request_diagnostics(conversation_id, created_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_message_embeddings_conversation ON message_embeddings(conversation_id);
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(4, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(5, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(6, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(7, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        await EnsureConversationPinColumnAsync(db);
        await EnsureWorkRequestColumnsAsync(db);
        await EnsureChatToolCallColumnsAsync(db);
        await InitializeSearchIndexAsync(db);
        await RecoverInterruptedRunsAsync(db);
    }

    private async Task InitializeSearchIndexAsync(SqliteConnection db)
    {
        try
        {
            var command = db.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                    message_id UNINDEXED,
                    conversation_id UNINDEXED,
                    role UNINDEXED,
                    content,
                    tokenize='unicode61'
                );
                DELETE FROM messages_fts WHERE message_id NOT IN (
                    SELECT id FROM messages_v2 WHERE status IN ('complete','interrupted')
                );
                INSERT INTO messages_fts(message_id,conversation_id,role,content)
                SELECT m.id,m.conversation_id,m.role,m.content FROM messages_v2 m
                WHERE m.status IN ('complete','interrupted')
                  AND NOT EXISTS (SELECT 1 FROM messages_fts f WHERE f.message_id=m.id);
                """;
            await command.ExecuteNonQueryAsync();
            _ftsAvailable = true;
        }
        catch (SqliteException)
        {
            // Search is derived data. The application remains usable and SearchMessagesAsync
            // transparently falls back to a bounded LIKE query.
            _ftsAvailable = false;
        }
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var values = new Dictionary<string, string>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values[reader.GetString(0)] = reader.GetString(1);
        var hasModelEndpoint = values.TryGetValue("model_api_url", out var modelEndpoint);
        return new AppSettings
        {
            AsrModel = values.GetValueOrDefault("asr_model", "qwen-audio-3.1-asr-flash"),
            ChatModel = hasModelEndpoint ? values.GetValueOrDefault("chat_model", "deepseek-flash") : "deepseek-flash",
            WorkModel = hasModelEndpoint ? values.GetValueOrDefault("work_model", "deepseek-v4-pro") : "deepseek-v4-pro",
            ModelApiUrl = hasModelEndpoint ? modelEndpoint! : "https://api.deepseek.com/chat/completions",
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
            ["work_model"] = settings.WorkModel,
            ["model_api_url"] = settings.ModelApiUrl,
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

    public async Task<ConversationSummary> CreateConversationAsync(
        string mode = "chat",
        string? originConversationId = null,
        string? title = null,
        string titleSource = "fallback")
    {
        if (mode is not ("chat" or "work")) throw new ArgumentOutOfRangeException(nameof(mode));
        if (titleSource is not ("fallback" or "generated" or "user")) throw new ArgumentOutOfRangeException(nameof(titleSource));
        var now = DateTimeOffset.UtcNow;
        var normalizedTitle = title is null ? UntitledTitle : NormalizeTitle(title);
        var conversation = new ConversationSummary(Guid.NewGuid().ToString("N"), mode, normalizedTitle, titleSource, now, now, false, originConversationId);
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
        command.CommandText = "SELECT id,mode,title,title_source,created_utc,updated_utc,pinned_utc,origin_conversation_id FROM conversations_v2 WHERE archived_utc IS NULL ORDER BY pinned_utc IS NULL, pinned_utc DESC, updated_utc DESC";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<ConversationSummary?> GetConversationAsync(string id)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,mode,title,title_source,created_utc,updated_utc,pinned_utc,origin_conversation_id FROM conversations_v2 WHERE id=$id AND archived_utc IS NULL";
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
        var workRequests = await ListWorkRequestsForConversationAsync(id);
        var chatToolCalls = await ListChatToolCallsAsync(id);
        var conversationSources = await ListConversationSourcesAsync(id);
        return new ConversationDetail(conversation, messages, citations, activeRunId, workRequests, chatToolCalls, conversationSources);
    }

    public Task<WorkRequestRecord> CreateWorkRequestAsync(
        string sourceConversationId,
        string sourceMessageId,
        string sourceRunId,
        string toolCallId,
        string title,
        string prompt,
        string contextSummary,
        int? anchorOffset = null) =>
        UpsertWorkRequestAsync(sourceConversationId, sourceMessageId, sourceRunId, toolCallId, "proposed",
            NormalizeTitle(title),
            NormalizeWorkRequestText(prompt, nameof(prompt), 12_000),
            NormalizeWorkRequestText(contextSummary, nameof(contextSummary), 12_000),
            errorMessage: null,
            anchorOffset: anchorOffset);

    public Task<WorkRequestRecord> CreateFailedWorkRequestAsync(
        string sourceConversationId,
        string sourceMessageId,
        string sourceRunId,
        string toolCallId,
        string errorMessage,
        int? anchorOffset = null) =>
        // An empty prompt marks the row as unparseable so the desktop client can hide "retry".
        UpsertWorkRequestAsync(sourceConversationId, sourceMessageId, sourceRunId, toolCallId, "failed",
            "Work 提议解析失败", string.Empty, string.Empty, errorMessage, anchorOffset);

    private async Task<WorkRequestRecord> UpsertWorkRequestAsync(
        string sourceConversationId,
        string sourceMessageId,
        string sourceRunId,
        string toolCallId,
        string state,
        string title,
        string prompt,
        string contextSummary,
        string? errorMessage,
        int? anchorOffset)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)) throw new ArgumentException("工具调用 ID 不能为空。", nameof(toolCallId));
        var now = DateTimeOffset.UtcNow;
        await using var db = await OpenAsync();

        // A model that proposes the same tool call twice reuses one row, otherwise a rejected or
        // failed proposal could never be re-proposed: the insert would be swallowed by the unique key.
        var existing = await GetWorkRequestByToolCallAsync(db, sourceConversationId, toolCallId);
        if (existing is not null)
        {
            if (existing.State is "approved" or "started") return existing;
            var update = db.CreateCommand();
            update.CommandText = """
                UPDATE work_requests
                SET source_message_id=$message,source_run_id=$run,title=$title,prompt=$prompt,context_summary=$summary,
                    state=$state,error_message=$error,anchor_offset=$offset,updated_utc=$updated
                WHERE id=$id AND state NOT IN ('approved','started')
                """;
            update.Parameters.AddWithValue("$message", sourceMessageId);
            update.Parameters.AddWithValue("$run", sourceRunId);
            update.Parameters.AddWithValue("$title", title);
            update.Parameters.AddWithValue("$prompt", prompt);
            update.Parameters.AddWithValue("$summary", contextSummary);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            update.Parameters.AddWithValue("$offset", (object?)anchorOffset ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", Format(now));
            update.Parameters.AddWithValue("$id", existing.Id);
            await update.ExecuteNonQueryAsync();
            return await GetWorkRequestAsync(existing.Id) ?? throw new InvalidOperationException("无法保存 Work 请求。");
        }

        var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO work_requests(
                id,source_conversation_id,source_message_id,source_run_id,tool_call_id,title,prompt,context_summary,state,target_conversation_id,error_message,created_utc,updated_utc,anchor_offset)
            VALUES($id,$conversation,$message,$run,$tool,$title,$prompt,$summary,$state,NULL,$error,$created,$updated,$offset)
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$conversation", sourceConversationId);
        insert.Parameters.AddWithValue("$message", sourceMessageId);
        insert.Parameters.AddWithValue("$run", sourceRunId);
        insert.Parameters.AddWithValue("$tool", toolCallId);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$prompt", prompt);
        insert.Parameters.AddWithValue("$summary", contextSummary);
        insert.Parameters.AddWithValue("$state", state);
        insert.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
        insert.Parameters.AddWithValue("$offset", (object?)anchorOffset ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created", Format(now));
        insert.Parameters.AddWithValue("$updated", Format(now));
        await insert.ExecuteNonQueryAsync();
        return await GetWorkRequestByToolCallAsync(db, sourceConversationId, toolCallId)
            ?? throw new InvalidOperationException("无法保存 Work 请求。");
    }

    public async Task<WorkRequestRecord?> GetWorkRequestAsync(string id)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = WorkRequestSelect + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWorkRequest(reader) : null;
    }

    public async Task<IReadOnlyList<WorkRequestRecord>> ListWorkRequestsForConversationAsync(string conversationId)
    {
        var result = new List<WorkRequestRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = WorkRequestSelect + " WHERE source_conversation_id=$id OR target_conversation_id=$id ORDER BY created_utc";
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadWorkRequest(reader));
        return result;
    }

    public async Task<IReadOnlyList<WorkRequestRecord>> ListWorkRequestsByStateAsync(string state)
    {
        if (state is not ("proposed" or "approved" or "rejected" or "started" or "failed"))
            throw new ArgumentOutOfRangeException(nameof(state));
        var result = new List<WorkRequestRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = WorkRequestSelect + " WHERE state=$state ORDER BY created_utc";
        command.Parameters.AddWithValue("$state", state);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadWorkRequest(reader));
        return result;
    }

    public async Task<(ConversationSummary Conversation, bool Created)> CreateWorkConversationForRequestAsync(string requestId)
    {
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var requestCommand = db.CreateCommand();
        requestCommand.Transaction = (SqliteTransaction)transaction;
        requestCommand.CommandText = WorkRequestSelect + " WHERE id=$id";
        requestCommand.Parameters.AddWithValue("$id", requestId);
        WorkRequestRecord request;
        await using (var reader = await requestCommand.ExecuteReaderAsync())
        {
            request = await reader.ReadAsync() ? ReadWorkRequest(reader) : throw new KeyNotFoundException("Work 请求不存在。");
        }
        if (request.State != "approved") throw new InvalidOperationException("只有已批准的 Work 请求可以创建对话。");

        if (request.TargetConversationId is { } existingId)
        {
            var existing = await GetConversationAsync(db, (SqliteTransaction)transaction, existingId)
                ?? throw new InvalidOperationException("此前创建的 Work 对话已不存在。");
            await transaction.CommitAsync();
            return (existing, false);
        }

        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationSummary(
            Guid.NewGuid().ToString("N"), "work", NormalizeTitle(request.Title), "generated", now, now, false, request.SourceConversationId);
        var insert = db.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO conversations_v2(id,mode,title,title_source,created_utc,updated_utc,origin_conversation_id)
            VALUES($id,'work',$title,'generated',$created,$updated,$origin)
            """;
        insert.Parameters.AddWithValue("$id", conversation.Id);
        insert.Parameters.AddWithValue("$title", conversation.Title);
        insert.Parameters.AddWithValue("$created", Format(now));
        insert.Parameters.AddWithValue("$updated", Format(now));
        insert.Parameters.AddWithValue("$origin", request.SourceConversationId);
        await insert.ExecuteNonQueryAsync();

        var link = db.CreateCommand();
        link.Transaction = (SqliteTransaction)transaction;
        link.CommandText = """
            UPDATE work_requests SET target_conversation_id=$target,updated_utc=$updated
            WHERE id=$id AND state='approved' AND target_conversation_id IS NULL
            """;
        link.Parameters.AddWithValue("$target", conversation.Id);
        link.Parameters.AddWithValue("$updated", Format(now));
        link.Parameters.AddWithValue("$id", requestId);
        if (await link.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Work 请求状态已经变化，请刷新后重试。");

        await transaction.CommitAsync();
        return (conversation, true);
    }

    public async Task<WorkRequestRecord> UpdateWorkRequestAsync(
        string id,
        string expectedState,
        string state,
        string? targetConversationId = null,
        string? errorMessage = null)
    {
        if (expectedState is not ("proposed" or "approved" or "started" or "failed")) throw new ArgumentOutOfRangeException(nameof(expectedState));
        if (state is not ("approved" or "rejected" or "started" or "failed")) throw new ArgumentOutOfRangeException(nameof(state));
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            UPDATE work_requests
            SET state=$state,target_conversation_id=COALESCE($target,target_conversation_id),error_message=$error,updated_utc=$updated
            WHERE id=$id AND state=$expected
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$target", (object?)targetConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expected", expectedState);
        if (await command.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("Work 请求状态已经变化，请刷新后重试。");
        return await GetWorkRequestAsync(id) ?? throw new KeyNotFoundException("Work 请求不存在。");
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
        await IndexMessageAsync(message.Id);
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
        await IndexMessageAsync(messageId);
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
        await RemoveMessageFromIndexAsync(messageId);
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

    public async Task<bool> HasWorkContextItemAsync(string conversationId, string name)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM work_context_items WHERE conversation_id=$conversation AND name=$name)";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
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

    public async Task<IReadOnlyList<WorkToolCallRecord>> ListWorkToolCallsAsync(string conversationId)
    {
        var result = new List<WorkToolCallRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,conversation_id,run_id,round_id,batch_id,tool_call_id,ordinal,tool_name,arguments_json,state,
                   result_json,error_code,error_message,created_utc,started_utc,completed_utc,duration_ms
            FROM work_tool_calls WHERE conversation_id=$conversation ORDER BY created_utc,ordinal
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new WorkToolCallRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : Parse(reader.GetString(14)), reader.IsDBNull(15) ? null : Parse(reader.GetString(15)),
                reader.IsDBNull(16) ? null : reader.GetInt64(16)));
        return result;
    }

    public async Task<ChatToolCallRecord> CreateChatToolCallAsync(
        string conversationId,
        string runId,
        string toolCallId,
        string toolName,
        string argumentsJson,
        string? query,
        int? anchorOffset)
    {
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("工具名不能为空。", nameof(toolName));
        EnsureJson(argumentsJson, nameof(argumentsJson));
        var now = DateTimeOffset.UtcNow;
        var record = new ChatToolCallRecord(
            Guid.NewGuid().ToString("N"), conversationId, runId, toolCallId, toolName, argumentsJson,
            "running", query, 0, 0, 0, null, null, null, null, now, null, anchorOffset);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO chat_tool_calls(
                id,conversation_id,run_id,tool_call_id,tool_name,arguments_json,state,query,query_count,
                raw_result_count,unique_result_count,result_json,error_code,error_message,elapsed_ms,anchor_offset,created_utc)
            VALUES($id,$conversation,$run,$tool,$name,$arguments,'running',$query,0,0,0,NULL,NULL,NULL,NULL,$offset,$created)
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$tool", toolCallId);
        command.Parameters.AddWithValue("$name", toolName);
        command.Parameters.AddWithValue("$arguments", argumentsJson);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$offset", (object?)anchorOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(now));
        await command.ExecuteNonQueryAsync();
        return record;
    }

    public async Task UpdateChatToolCallAsync(
        string id,
        string state,
        string? query = null,
        int queryCount = 0,
        int rawResultCount = 0,
        int uniqueResultCount = 0,
        string? resultJson = null,
        string? errorCode = null,
        string? errorMessage = null,
        long? elapsedMilliseconds = null,
        IReadOnlyList<string>? newSourceIds = null)
    {
        if (state is not ("running" or "completed" or "failed" or "cancelled"))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (resultJson is not null) EnsureJson(resultJson, nameof(resultJson));
        var now = Format(DateTimeOffset.UtcNow);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            UPDATE chat_tool_calls
            SET state=$state,
                query=COALESCE($query,query),
                query_count=$queryCount,
                raw_result_count=$rawCount,
                unique_result_count=$uniqueCount,
                result_json=$result,
                error_code=$errorCode,
                error_message=$errorMessage,
                elapsed_ms=$elapsed,
                new_source_ids_json=$newSources,
                completed_utc=CASE WHEN $state IN ('completed','failed','cancelled') THEN $now ELSE completed_utc END
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$queryCount", queryCount);
        command.Parameters.AddWithValue("$rawCount", rawResultCount);
        command.Parameters.AddWithValue("$uniqueCount", uniqueResultCount);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$elapsed", (object?)elapsedMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$newSources", newSourceIds is null ? DBNull.Value : JsonSerializer.Serialize(newSourceIds));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync() == 0) throw new KeyNotFoundException("Chat 工具调用不存在。");
    }

    public async Task CancelRunningChatToolCallsAsync(string runId, string state, string message)
    {
        if (state is not ("failed" or "cancelled")) throw new ArgumentOutOfRangeException(nameof(state));
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            UPDATE chat_tool_calls
            SET state=$state,error_code='interrupted',error_message=$message,completed_utc=$now
            WHERE run_id=$run AND state='running'
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ChatToolCallRecord>> ListChatToolCallsAsync(string conversationId)
    {
        var result = new List<ChatToolCallRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,conversation_id,run_id,tool_call_id,tool_name,arguments_json,state,query,query_count,
                   raw_result_count,unique_result_count,result_json,error_code,error_message,elapsed_ms,
                   created_utc,completed_utc,anchor_offset,new_source_ids_json
            FROM chat_tool_calls WHERE conversation_id=$conversation ORDER BY created_utc,id
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new ChatToolCallRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14), Parse(reader.GetString(15)),
                reader.IsDBNull(16) ? null : Parse(reader.GetString(16)), reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(18))));
        return result;
    }

    public async Task<ConversationSourceRecord> AddConversationSourceAsync(
        ConversationSourceRecord source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.NormalizedUrl)) throw new ArgumentException("规范化 URL 不能为空。", nameof(source));
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var existing = await GetConversationSourceByUrlAsync(db, (SqliteTransaction)transaction, source.ConversationId, source.NormalizedUrl);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var ordinalCommand = db.CreateCommand();
        ordinalCommand.Transaction = (SqliteTransaction)transaction;
        ordinalCommand.CommandText = "SELECT COALESCE(MAX(ordinal),0)+1 FROM conversation_sources WHERE conversation_id=$conversation";
        ordinalCommand.Parameters.AddWithValue("$conversation", source.ConversationId);
        var ordinal = Math.Max(source.Ordinal, Convert.ToInt32(await ordinalCommand.ExecuteScalarAsync(cancellationToken)));

        var insert = db.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO conversation_sources(
                id,conversation_id,ordinal,title,url,normalized_url,snippet,site_name,domain,
                first_tool_call_id,first_operation_id,first_seen_utc,updated_utc,
                published_time,authority,rerank_score,is_official,weather_kind)
            VALUES($id,$conversation,$ordinal,$title,$url,$normalized,$snippet,$site,$domain,
                $toolCall,$operation,$firstSeen,$updated,$published,$authority,$rerank,$official,$weather)
            """;
        insert.Parameters.AddWithValue("$id", source.Id);
        insert.Parameters.AddWithValue("$conversation", source.ConversationId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$title", source.Title);
        insert.Parameters.AddWithValue("$url", source.Url);
        insert.Parameters.AddWithValue("$normalized", source.NormalizedUrl);
        insert.Parameters.AddWithValue("$snippet", (object?)source.Snippet ?? DBNull.Value);
        insert.Parameters.AddWithValue("$site", (object?)source.SiteName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$domain", source.Domain);
        insert.Parameters.AddWithValue("$toolCall", source.FirstToolCallId);
        insert.Parameters.AddWithValue("$operation", source.FirstOperationId);
        insert.Parameters.AddWithValue("$firstSeen", Format(source.FirstSeenAt));
        insert.Parameters.AddWithValue("$updated", Format(source.UpdatedAt));
        insert.Parameters.AddWithValue("$published", (object?)source.PublishedTime ?? DBNull.Value);
        insert.Parameters.AddWithValue("$authority", (object?)source.Authority ?? DBNull.Value);
        insert.Parameters.AddWithValue("$rerank", (object?)source.RerankScore ?? DBNull.Value);
        insert.Parameters.AddWithValue("$official", source.IsOfficial ? 1 : 0);
        insert.Parameters.AddWithValue("$weather", (object?)source.WeatherKind ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return source with { Ordinal = ordinal };
    }

    public async Task UpdateConversationSourceDetailsAsync(ConversationSourceRecord source, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            UPDATE conversation_sources
            SET title=$title,snippet=$snippet,site_name=$site,domain=$domain,updated_utc=$updated,
                published_time=$published,authority=$authority,rerank_score=$rerank,
                is_official=$official,weather_kind=$weather
            WHERE id=$id AND conversation_id=$conversation
            """;
        command.Parameters.AddWithValue("$title", source.Title);
        command.Parameters.AddWithValue("$snippet", (object?)source.Snippet ?? DBNull.Value);
        command.Parameters.AddWithValue("$site", (object?)source.SiteName ?? DBNull.Value);
        command.Parameters.AddWithValue("$domain", source.Domain);
        command.Parameters.AddWithValue("$updated", Format(source.UpdatedAt));
        command.Parameters.AddWithValue("$published", (object?)source.PublishedTime ?? DBNull.Value);
        command.Parameters.AddWithValue("$authority", (object?)source.Authority ?? DBNull.Value);
        command.Parameters.AddWithValue("$rerank", (object?)source.RerankScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$official", source.IsOfficial ? 1 : 0);
        command.Parameters.AddWithValue("$weather", (object?)source.WeatherKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", source.Id);
        command.Parameters.AddWithValue("$conversation", source.ConversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSourceRecord>> ListConversationSourcesAsync(string conversationId)
    {
        var result = new List<ConversationSourceRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = ConversationSourceSelect + " WHERE conversation_id=$conversation ORDER BY ordinal";
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadConversationSource(reader));
        return result;
    }

    public async Task<int> GetMaxCitationOrdinalAsync(string conversationId)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(c.ordinal),0)
            FROM citations c JOIN messages_v2 m ON m.id=c.message_id
            WHERE m.conversation_id=$conversation
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<ConversationSourceRecord?> GetConversationSourceByUrlAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        string conversationId,
        string normalizedUrl)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ConversationSourceSelect + " WHERE conversation_id=$conversation AND normalized_url=$url";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$url", normalizedUrl);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversationSource(reader) : null;
    }

    private static ConversationSourceRecord ReadConversationSource(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8), reader.GetString(9), reader.GetString(10), Parse(reader.GetString(11)), Parse(reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetDouble(14),
        reader.IsDBNull(15) ? null : reader.GetDouble(15), reader.GetInt64(16) != 0, reader.IsDBNull(17) ? null : reader.GetString(17));

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
        var unfinishedTools = db.CreateCommand();
        unfinishedTools.Transaction = (SqliteTransaction)transaction;
        unfinishedTools.CommandText = """
            UPDATE work_tool_calls
            SET state='cancelled',
                result_json=COALESCE(result_json,'{"error":"应用重启前工具未形成可靠终态，已按取消恢复。"}'),
                error_code=COALESCE(error_code,'interrupted'),
                error_message=COALESCE(error_message,'应用退出时工具仍未完成。'),
                completed_utc=COALESCE(completed_utc,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            WHERE state IN ('pending','waiting_approval','running');

            WITH missing AS (
                SELECT c.id,c.conversation_id,c.run_id,c.tool_call_id,c.tool_name,c.result_json,c.created_utc,
                       ROW_NUMBER() OVER (PARTITION BY c.conversation_id ORDER BY c.created_utc,c.ordinal,c.id) AS position
                FROM work_tool_calls c
                WHERE NOT EXISTS (
                    SELECT 1 FROM work_context_items i
                    WHERE i.conversation_id=c.conversation_id AND i.run_id=c.run_id AND i.kind='tool_result' AND i.tool_call_id=c.tool_call_id
                )
            ), base AS (
                SELECT conversation_id,COALESCE(MAX(ordinal),0) AS maximum
                FROM work_context_items GROUP BY conversation_id
            )
            INSERT INTO work_context_items(id,conversation_id,run_id,ordinal,kind,role,message_id,tool_call_id,name,payload_json,created_utc)
            SELECT lower(hex(randomblob(16))),m.conversation_id,m.run_id,COALESCE(b.maximum,0)+m.position,
                   'tool_result','tool',NULL,m.tool_call_id,m.tool_name,
                   COALESCE(m.result_json,'{"error":"工具结果不可用。"}'),strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM missing m LEFT JOIN base b ON b.conversation_id=m.conversation_id;
            """;
        await unfinishedTools.ExecuteNonQueryAsync();
        var unfinishedChatTools = db.CreateCommand();
        unfinishedChatTools.Transaction = (SqliteTransaction)transaction;
        unfinishedChatTools.CommandText = """
            UPDATE chat_tool_calls
            SET state='failed',
                error_code=COALESCE(error_code,'interrupted'),
                error_message=COALESCE(error_message,'应用退出时工具仍未完成。'),
                completed_utc=COALESCE(completed_utc,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            WHERE state='running';
            """;
        await unfinishedChatTools.ExecuteNonQueryAsync();
        var runs = db.CreateCommand();
        runs.Transaction = (SqliteTransaction)transaction;
        runs.CommandText = """
            UPDATE runs SET state='interrupted',finish_reason='interrupted',completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE state IN ('starting','searching','generating') AND completed_utc IS NULL;
            """;
        await runs.ExecuteNonQueryAsync();
        var approvals = db.CreateCommand();
        approvals.Transaction = (SqliteTransaction)transaction;
        approvals.CommandText = """
            UPDATE work_approvals SET state='cancelled',resolved_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE state='pending';
            """;
        await approvals.ExecuteNonQueryAsync();
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
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)), Parse(reader.GetString(5)), !reader.IsDBNull(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static async Task<ConversationSummary?> GetConversationAsync(SqliteConnection db, SqliteTransaction transaction, string id)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,mode,title,title_source,created_utc,updated_utc,pinned_utc,origin_conversation_id FROM conversations_v2 WHERE id=$id AND archived_utc IS NULL";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadConversation(reader) : null;
    }

    private static WorkRequestRecord ReadWorkRequest(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        Parse(reader.GetString(11)), Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : reader.GetInt32(13));

    private static async Task<WorkRequestRecord?> GetWorkRequestByToolCallAsync(SqliteConnection db, string sourceConversationId, string toolCallId)
    {
        var command = db.CreateCommand();
        command.CommandText = WorkRequestSelect + " WHERE source_conversation_id=$conversation AND tool_call_id=$tool";
        command.Parameters.AddWithValue("$conversation", sourceConversationId);
        command.Parameters.AddWithValue("$tool", toolCallId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWorkRequest(reader) : null;
    }

    private static ConversationMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
        Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9));

    private static string NormalizeTitle(string title)
    {
        title = string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) throw new ArgumentException("标题不能为空。", nameof(title));
        return title.Length <= 80 ? title : title[..80];
    }

    private static string NormalizeWorkRequestText(string value, string parameterName, int maximumLength)
    {
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("Work 请求内容不能为空。", parameterName);
        if (value.Length > maximumLength) throw new ArgumentException($"Work 请求内容不能超过 {maximumLength} 个字符。", parameterName);
        return value;
    }

    internal static string FallbackTitle(string content)
    {
        var title = string.Join(' ', content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) return UntitledTitle;
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

    private static async Task EnsureWorkRequestColumnsAsync(SqliteConnection db)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = db.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(work_requests)";
        await using (var reader = await inspect.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        foreach (var (name, definition) in new[]
        {
            ("source_run_id", "TEXT NOT NULL DEFAULT ''"),
            ("tool_call_id", "TEXT NOT NULL DEFAULT ''"),
            ("error_message", "TEXT NULL"),
            ("updated_utc", "TEXT NULL"),
            ("anchor_offset", "INTEGER NULL")
        })
        {
            if (columns.Contains(name)) continue;
            var alter = db.CreateCommand();
            alter.CommandText = $"ALTER TABLE work_requests ADD COLUMN {name} {definition}";
            await alter.ExecuteNonQueryAsync();
        }
        var backfill = db.CreateCommand();
        backfill.CommandText = "UPDATE work_requests SET updated_utc=created_utc WHERE updated_utc IS NULL";
        await backfill.ExecuteNonQueryAsync();
        var index = db.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_work_requests_tool_call ON work_requests(source_conversation_id,tool_call_id) WHERE tool_call_id<>''";
        await index.ExecuteNonQueryAsync();
    }

    private static async Task EnsureChatToolCallColumnsAsync(SqliteConnection db)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = db.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(chat_tool_calls)";
        await using (var reader = await inspect.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        if (columns.Count == 0) return;
        if (!columns.Contains("new_source_ids_json"))
        {
            var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE chat_tool_calls ADD COLUMN new_source_ids_json TEXT NULL";
            await alter.ExecuteNonQueryAsync();
        }
    }
}
