using CatAssistant.Backend.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task InitializePreservesLegacyWorkTablesAndCreatesCurrentSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-assistant-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await legacy.OpenAsync();
                var command = legacy.CreateCommand();
                command.CommandText = """
                    CREATE TABLE work_approvals (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, state TEXT NOT NULL);
                    INSERT INTO work_approvals VALUES ('approval-old','session-old','pending');
                    CREATE TABLE work_artifacts (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, relative_path TEXT NOT NULL);
                    INSERT INTO work_artifacts VALUES ('artifact-old','session-old','old.docx');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();

            await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
            await db.OpenAsync();
            Assert.Equal(1L, await ScalarAsync(db, "SELECT COUNT(*) FROM legacy_work_approvals WHERE id='approval-old'"));
            Assert.Equal(1L, await ScalarAsync(db, "SELECT COUNT(*) FROM legacy_work_artifacts WHERE id='artifact-old'"));
            Assert.Equal(1L, await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('work_approvals') WHERE name='conversation_id'"));
            Assert.Equal(1L, await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('work_artifacts') WHERE name='conversation_id'"));
        }
        finally
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    [Fact]
    public async Task RestartClosesUnfinishedToolCallsAndRestoresAReplayableResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-assistant-recovery-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            var user = await repository.AddMessageAsync(conversation.Id, "user", "desktop", "complete", "执行任务");
            var runId = Guid.NewGuid().ToString("N");
            await repository.CreateRunAsync(runId, conversation.Id, user.Id, "work");
            await repository.AddWorkContextItemAsync(conversation.Id, runId, "tool_call", "assistant",
                "{\"content\":null,\"toolCalls\":[{\"ToolCallId\":\"call-crash\",\"Name\":\"read_file\",\"ArgumentsJson\":\"{}\"}]}",
                name: "assistant_tool_calls");
            await repository.CreateWorkToolCallAsync(conversation.Id, runId, "round", "batch", "call-crash", 0, "read_file", "{}");

            var restarted = new SqliteRepository(path);
            await restarted.InitializeAsync();

            var call = Assert.Single(await restarted.ListWorkToolCallsAsync(conversation.Id));
            Assert.Equal("cancelled", call.State);
            var result = Assert.Single(await restarted.LoadWorkContextItemsAsync(conversation.Id), item => item.Kind == "tool_result");
            Assert.Equal("call-crash", result.ToolCallId);
            Assert.Contains("取消", result.PayloadJson);
        }
        finally
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static async Task<long> ScalarAsync(SqliteConnection db, string sql)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
