using CatAssistant.Backend.Models;
using Microsoft.Data.Sqlite;

namespace CatAssistant.Backend.Services;

public sealed partial class SqliteRepository
{
    private async Task IndexMessageAsync(string messageId)
    {
        if (!_ftsAvailable) return;
        try
        {
            await using var db = await OpenAsync();
            var delete = db.CreateCommand();
            delete.CommandText = "DELETE FROM messages_fts WHERE message_id=$id";
            delete.Parameters.AddWithValue("$id", messageId);
            await delete.ExecuteNonQueryAsync();

            var insert = db.CreateCommand();
            insert.CommandText = """
                INSERT INTO messages_fts(message_id,conversation_id,role,content)
                SELECT id,conversation_id,role,content FROM messages_v2
                WHERE id=$id AND status IN ('complete','interrupted')
                """;
            insert.Parameters.AddWithValue("$id", messageId);
            await insert.ExecuteNonQueryAsync();
        }
        catch (SqliteException) { _ftsAvailable = false; }
    }

    private async Task RemoveMessageFromIndexAsync(string messageId)
    {
        if (!_ftsAvailable) return;
        try
        {
            await using var db = await OpenAsync();
            var command = db.CreateCommand();
            command.CommandText = "DELETE FROM messages_fts WHERE message_id=$id";
            command.Parameters.AddWithValue("$id", messageId);
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException) { _ftsAvailable = false; }
    }

    public async Task<IReadOnlyList<HistorySearchHit>> SearchMessagesAsync(
        string conversationId,
        string query,
        int limit = 8,
        string? role = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("搜索内容不能为空。", nameof(query));
        limit = Math.Clamp(limit, 1, 20);
        if (role is not null and not ("user" or "assistant")) throw new ArgumentOutOfRangeException(nameof(role));
        if (_ftsAvailable)
        {
            try
            {
                var fts = await SearchMessagesFtsAsync(conversationId, query, limit, role, from, to);
                if (fts.Count >= limit) return fts;
                var fallback = await SearchMessagesLikeAsync(conversationId, query, limit, role, from, to);
                return fts.Concat(fallback)
                    .GroupBy(item => item.MessageId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Take(limit)
                    .ToArray();
            }
            catch (SqliteException) { _ftsAvailable = false; }
        }
        return await SearchMessagesLikeAsync(conversationId, query, limit, role, from, to);
    }

    private async Task<IReadOnlyList<HistorySearchHit>> SearchMessagesFtsAsync(
        string conversationId, string query, int limit, string? role, DateTimeOffset? from, DateTimeOffset? to)
    {
        var result = new List<HistorySearchHit>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT m.id,m.conversation_id,m.sequence,m.created_utc,m.role,m.status,
                   snippet(messages_fts,3,'[',']','…',24),bm25(messages_fts)
            FROM messages_fts
            JOIN messages_v2 m ON m.id=messages_fts.message_id
            WHERE messages_fts MATCH $query AND m.conversation_id=$conversation
              AND ($role IS NULL OR m.role=$role)
              AND ($from IS NULL OR m.created_utc >= $from)
              AND ($to IS NULL OR m.created_utc <= $to)
            ORDER BY bm25(messages_fts),m.sequence DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", FtsQuery(query));
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", from.HasValue ? Format(from.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", to.HasValue ? Format(to.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rank = reader.GetDouble(7);
            result.Add(new HistorySearchHit(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Parse(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), 1d / (1d + Math.Abs(rank)), "keyword"));
        }
        return result;
    }

    private async Task<IReadOnlyList<HistorySearchHit>> SearchMessagesLikeAsync(
        string conversationId, string query, int limit, string? role, DateTimeOffset? from, DateTimeOffset? to)
    {
        var result = new List<HistorySearchHit>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,conversation_id,sequence,created_utc,role,status,content
            FROM messages_v2
            WHERE conversation_id=$conversation AND content LIKE $query ESCAPE '\'
              AND status IN ('complete','interrupted')
              AND ($role IS NULL OR role=$role)
              AND ($from IS NULL OR created_utc >= $from)
              AND ($to IS NULL OR created_utc <= $to)
            ORDER BY sequence DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$query", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", from.HasValue ? Format(from.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", to.HasValue ? Format(to.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.GetString(6);
            result.Add(new HistorySearchHit(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Parse(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), Snippet(content, query), 0.5d, "keyword_fallback"));
        }
        return result;
    }

    public async Task<ConversationMessage?> GetMessageAsync(string conversationId, string messageId)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,role,source,status,sequence,content,created_utc,completed_utc,run_id FROM messages_v2 WHERE conversation_id=$conversation AND id=$id";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$id", messageId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadMessage(reader) : null;
    }

    public async Task<ConversationContextState> GetContextStateAsync(string conversationId)
    {
        await using var db = await OpenAsync();
        var insert = db.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO conversation_context_state(conversation_id) VALUES($id)";
        insert.Parameters.AddWithValue("$id", conversationId);
        await insert.ExecuteNonQueryAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT conversation_id,active_summary_id,compressed_through_sequence,search_watermark_sequence,export_watermark_sequence,version FROM conversation_context_state WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new KeyNotFoundException("对话不存在。");
        return new ConversationContextState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5));
    }

    public async Task<ContextSummaryRecord?> GetActiveSummaryAsync(string conversationId)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.conversation_id,s.mode,s.version,s.source_summary_id,s.start_sequence,s.end_sequence,s.content,s.estimated_tokens,s.state,s.created_utc
            FROM conversation_context_state c JOIN context_summaries s ON s.id=c.active_summary_id
            WHERE c.conversation_id=$id
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSummary(reader) : null;
    }

    public async Task<ContextSummaryRecord> ActivateSummaryAsync(
        string conversationId, string mode, string content, long startSequence, long endSequence, int estimatedTokens, string? sourceSummaryId)
    {
        if (endSequence < startSequence) throw new ArgumentOutOfRangeException(nameof(endSequence));
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        await using var db = await OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var versionCommand = db.CreateCommand();
        versionCommand.Transaction = (SqliteTransaction)transaction;
        versionCommand.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM context_summaries WHERE conversation_id=$id";
        versionCommand.Parameters.AddWithValue("$id", conversationId);
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());
        var supersede = db.CreateCommand();
        supersede.Transaction = (SqliteTransaction)transaction;
        supersede.CommandText = "UPDATE context_summaries SET state='superseded' WHERE conversation_id=$id AND state='active'";
        supersede.Parameters.AddWithValue("$id", conversationId);
        await supersede.ExecuteNonQueryAsync();
        var insert = db.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO context_summaries(id,conversation_id,mode,version,source_summary_id,start_sequence,end_sequence,content,estimated_tokens,state,created_utc)
            VALUES($id,$conversation,$mode,$version,$source,$start,$end,$content,$tokens,'active',$created)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$conversation", conversationId);
        insert.Parameters.AddWithValue("$mode", mode);
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$source", (object?)sourceSummaryId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$start", startSequence);
        insert.Parameters.AddWithValue("$end", endSequence);
        insert.Parameters.AddWithValue("$content", content);
        insert.Parameters.AddWithValue("$tokens", estimatedTokens);
        insert.Parameters.AddWithValue("$created", Format(now));
        await insert.ExecuteNonQueryAsync();
        var state = db.CreateCommand();
        state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = """
            INSERT INTO conversation_context_state(conversation_id,active_summary_id,compressed_through_sequence,version)
            VALUES($conversation,$summary,$end,1)
            ON CONFLICT(conversation_id) DO UPDATE SET active_summary_id=$summary,compressed_through_sequence=$end,version=version+1
            """;
        state.Parameters.AddWithValue("$conversation", conversationId);
        state.Parameters.AddWithValue("$summary", id);
        state.Parameters.AddWithValue("$end", endSequence);
        await state.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new ContextSummaryRecord(id, conversationId, mode, version, sourceSummaryId, startSequence, endSequence, content, estimatedTokens, "active", now);
    }

    public async Task RecordRequestDiagnosticAsync(
        string conversationId, string? runId, string mode, string model, string eventName,
        ContextBudgetAssessment assessment, ModelUsage? usage = null, string? detail = null)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO request_diagnostics(id,conversation_id,run_id,mode,model,event,estimated_input_tokens,input_budget_tokens,
                actual_input_tokens,actual_output_tokens,count_source,is_estimate,detail,created_utc)
            VALUES($id,$conversation,$run,$mode,$model,$event,$estimated,$budget,$actualInput,$actualOutput,$source,$isEstimate,$detail,$created)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$estimated", assessment.Input.Tokens);
        command.Parameters.AddWithValue("$budget", assessment.Profile.InputBudgetTokens);
        command.Parameters.AddWithValue("$actualInput", (object?)usage?.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualOutput", (object?)usage?.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", assessment.Input.Source);
        command.Parameters.AddWithValue("$isEstimate", assessment.Input.IsEstimate ? 1 : 0);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    public async Task SetRunUsageAsync(string runId, ModelUsage usage)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE runs SET usage_json=$usage WHERE id=$id";
        command.Parameters.AddWithValue("$usage", System.Text.Json.JsonSerializer.Serialize(usage));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateContextWatermarksAsync(string conversationId, long? search = null, long? export = null)
    {
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_context_state(conversation_id,search_watermark_sequence,export_watermark_sequence)
            VALUES($id,COALESCE($search,0),COALESCE($export,0))
            ON CONFLICT(conversation_id) DO UPDATE SET
                search_watermark_sequence=COALESCE($search,search_watermark_sequence),
                export_watermark_sequence=COALESCE($export,export_watermark_sequence),
                version=version+1
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("$export", (object?)export ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpsertMessageEmbeddingAsync(MessageEmbeddingRecord embedding)
    {
        var bytes = new byte[embedding.Vector.Length * sizeof(float)];
        Buffer.BlockCopy(embedding.Vector, 0, bytes, 0, bytes.Length);
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO message_embeddings(message_id,conversation_id,content_hash,model,dimensions,vector,updated_utc)
            VALUES($message,$conversation,$hash,$model,$dimensions,$vector,$updated)
            ON CONFLICT(message_id) DO UPDATE SET content_hash=$hash,model=$model,dimensions=$dimensions,vector=$vector,updated_utc=$updated
            """;
        command.Parameters.AddWithValue("$message", embedding.MessageId);
        command.Parameters.AddWithValue("$conversation", embedding.ConversationId);
        command.Parameters.AddWithValue("$hash", embedding.ContentHash);
        command.Parameters.AddWithValue("$model", embedding.Model);
        command.Parameters.AddWithValue("$dimensions", embedding.Vector.Length);
        command.Parameters.AddWithValue("$vector", bytes);
        command.Parameters.AddWithValue("$updated", Format(embedding.UpdatedAt));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<MessageEmbeddingRecord>> LoadMessageEmbeddingsAsync(string conversationId, string model)
    {
        var result = new List<MessageEmbeddingRecord>();
        await using var db = await OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = "SELECT message_id,conversation_id,content_hash,model,dimensions,vector,updated_utc FROM message_embeddings WHERE conversation_id=$conversation AND model=$model";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$model", model);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dimensions = reader.GetInt32(4);
            var bytes = (byte[])reader[5];
            if (bytes.Length != dimensions * sizeof(float)) continue;
            var vector = new float[dimensions];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            result.Add(new MessageEmbeddingRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), vector, Parse(reader.GetString(6))));
        }
        return result;
    }

    private static ContextSummaryRecord ReadSummary(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt32(8), reader.GetString(9), Parse(reader.GetString(10)));

    private static string FtsQuery(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"");
        return string.Join(" OR ", terms);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Snippet(string content, string query)
    {
        const int radius = 80;
        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = 0;
        var start = Math.Max(0, index - radius);
        var length = Math.Min(content.Length - start, radius * 2);
        return $"{(start > 0 ? "…" : "")}{content.Substring(start, length)}{(start + length < content.Length ? "…" : "")}";
    }
}
