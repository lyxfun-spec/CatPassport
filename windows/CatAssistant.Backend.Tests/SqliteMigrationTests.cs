using CatAssistant.Backend.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class SqliteMigrationTests
{
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
}
