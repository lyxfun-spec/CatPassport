using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class DeliverFilesToolTests
{
    [Fact]
    public async Task OnlyExistingRegularFilesUnderOutputsCanBeDelivered()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cat-delivery-tests-{Guid.NewGuid():N}");
        var outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(outputs);
        var repository = new SqliteRepository(Path.Combine(root, "assistant.db"));
        await repository.InitializeAsync();
        var conversation = await repository.CreateConversationAsync("work");
        var message = await repository.AddMessageAsync(conversation.Id, "user", "desktop", "complete", "制作文件");
        const string runId = "delivery-run";
        await repository.CreateRunAsync(runId, conversation.Id, message.Id, "work");
        var file = Path.Combine(outputs, "report.txt");
        await File.WriteAllTextAsync(file, "done");
        var tool = new DeliverFilesTool(repository);
        var context = new WorkToolExecutionContext(conversation.Id, runId, root, "call-deliver");
        try
        {
            var delivered = await tool.ExecuteAsync(context, "{\"files\":[{\"path\":\"report.txt\",\"label\":\"报告\"}]}", CancellationToken.None);
            Assert.True(delivered.Success);
            var artifact = Assert.Single(await repository.ListWorkArtifactsAsync(conversation.Id));
            Assert.Equal(file, artifact.Path);
            Assert.Equal("报告", artifact.Label);

            var rejected = await tool.ExecuteAsync(context, "{\"files\":[{\"path\":\"../assistant.db\"}]}", CancellationToken.None);
            Assert.False(rejected.Success);
            Assert.Equal("invalid_artifact", rejected.ErrorCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
