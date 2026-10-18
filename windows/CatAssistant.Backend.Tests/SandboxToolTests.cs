using System.Text.Json;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class SandboxToolTests
{
    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public async Task FileAndCommandToolsRunInsideSameOsBoundary()
    {
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        var conversationId = Guid.NewGuid().ToString("N");
        using var sandbox = new WindowsSandboxController();
        var workspace = Path.Combine(sandbox.GetPrivateStorageRoot(conversationId), "Workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "work"));
        var context = new WorkToolExecutionContext(conversationId, Guid.NewGuid().ToString("N"), workspace);
        try
        {
            var write = await new WriteFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/note.txt\",\"content\":\"hello 工作\"}", CancellationToken.None);
            Assert.True(write.Success, write.ErrorMessage);

            var read = await new ReadFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/note.txt\"}", CancellationToken.None);
            Assert.True(read.Success, read.ErrorMessage);
            using (var payload = JsonDocument.Parse(read.OutputJson))
                Assert.Equal("hello 工作", payload.RootElement.GetProperty("content").GetString());

            var list = await new ListDirectoryTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work\"}", CancellationToken.None);
            Assert.True(list.Success, list.ErrorMessage);
            Assert.Contains("note.txt", list.OutputJson);

            var command = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                "{\"command\":\"cd & type note.txt\",\"shell\":\"cmd\",\"working_directory\":\"work\"}", CancellationToken.None);
            Assert.True(command.Success, $"{command.ErrorMessage}\n{command.OutputJson}");
            Assert.Contains("hello", command.OutputJson);

            var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(outside, "secret");
            try
            {
                var denied = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                    JsonSerializer.Serialize(new { command = $"Get-Content -LiteralPath '{outside}'" }), CancellationToken.None);
                Assert.False(denied.Success);
                Assert.DoesNotContain("secret", denied.OutputJson);
            }
            finally { File.Delete(outside); }
        }
        finally
        {
            sandbox.DeleteProfile(conversationId);
        }
    }
}
