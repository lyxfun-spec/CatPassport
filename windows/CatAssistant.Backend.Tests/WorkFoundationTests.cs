using System.Runtime.CompilerServices;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WorkFoundationTests
{
    [Fact]
    public async Task WorkModelDefaultsToQwenMaxAndPersistsIndependently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"cat-assistant-settings-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteRepository(databasePath);
            await repository.InitializeAsync();
            var defaults = await repository.LoadSettingsAsync();
            Assert.Equal("qwen-plus", defaults.ChatModel);
            Assert.Equal("qwen3.8-max", defaults.WorkModel);

            defaults.ChatModel = "chat-model";
            defaults.WorkModel = "work-model";
            await repository.SaveSettingsAsync(defaults);
            var saved = await repository.LoadSettingsAsync();
            Assert.Equal("chat-model", saved.ChatModel);
            Assert.Equal("work-model", saved.WorkModel);
        }
        finally { DeleteDatabase(databasePath); }
    }

    [Fact]
    public async Task WorkspaceCreatesThreeDirectoriesAndKeepsDuplicateInputs()
    {
        var root = NewTemporaryDirectory();
        var sourceDirectory = NewTemporaryDirectory();
        try
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var source = Path.Combine(sourceDirectory, "brief.txt");
            await File.WriteAllTextAsync(source, "first");
            using var service = new WorkWorkspaceService(root);

            var paths = await service.EnsureAsync(conversationId);
            var first = await service.CopyInputAsync(conversationId, source);
            await File.WriteAllTextAsync(source, "second");
            var second = await service.CopyInputAsync(conversationId, source);

            Assert.True(Directory.Exists(paths.Inputs));
            Assert.True(Directory.Exists(paths.Work));
            Assert.True(Directory.Exists(Path.Combine(paths.Work, "python-packages")));
            Assert.True(Directory.Exists(paths.Outputs));
            Assert.Equal("brief.txt", Path.GetFileName(first));
            Assert.Equal("brief (2).txt", Path.GetFileName(second));
            Assert.Equal("first", await File.ReadAllTextAsync(first));
            Assert.Equal("second", await File.ReadAllTextAsync(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRejectsNonConversationPathsAndCancelledCopiesLeaveNoPartialFile()
    {
        var root = NewTemporaryDirectory();
        var sourceDirectory = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(sourceDirectory, "input.txt");
            await File.WriteAllTextAsync(source, "content");
            using var service = new WorkWorkspaceService(root);
            await Assert.ThrowsAsync<ArgumentException>(() => service.EnsureAsync("..\\outside"));
            var conversationId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CopyInputAsync(conversationId, source, cancellation.Token));

            var workspace = await service.EnsureAsync(conversationId);
            Assert.Empty(Directory.EnumerateFiles(workspace.Inputs));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkConversationCreatesPersistentWorkspaceButCannotRunYet()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"cat-assistant-work-{Guid.NewGuid():N}.db");
        var workspaceRoot = NewTemporaryDirectory();
        try
        {
            var repository = new SqliteRepository(databasePath);
            await repository.InitializeAsync();
            await using var service = new ConversationService(
                repository,
                new NoopProvider(),
                () => new AppSettings(),
                () => "test-key",
                CancellationToken.None,
                new WorkWorkspaceService(workspaceRoot));
            await service.InitializeAsync();

            var work = await service.CreateAsync("work");
            var paths = await service.GetWorkWorkspaceAsync(work.Id);
            var before = (await service.ListAsync()).Count;

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync("work", "制作演示文稿", "desktop"));

            Assert.True(Directory.Exists(paths.Inputs));
            Assert.True(Directory.Exists(paths.Work));
            Assert.True(Directory.Exists(paths.Outputs));
            Assert.Equal(before, (await service.ListAsync()).Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkExecutionHistoryApprovalsAndArtifactsArePersisted()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"cat-assistant-work-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteRepository(databasePath);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            var user = await repository.AddMessageAsync(conversation.Id, "user", "desktop", "complete", "整理资料");
            var runId = Guid.NewGuid().ToString("N");
            await repository.CreateRunAsync(runId, conversation.Id, user.Id, "work");

            await repository.AddWorkContextItemAsync(conversation.Id, runId, "message", "user", "{\"content\":\"整理资料\"}", messageId: user.Id);
            await repository.AddWorkContextItemAsync(conversation.Id, runId, "tool_call", "assistant", "{\"path\":\"D:\\\\资料\"}", toolCallId: "call-1", name: "list_directory");
            var context = await repository.LoadWorkContextItemsAsync(conversation.Id);

            var toolCall = await repository.CreateWorkToolCallAsync(conversation.Id, runId, "round-1", "batch-1", "call-1", 0, "list_directory", "{\"path\":\"D:\\\\资料\"}");
            await repository.UpdateWorkToolCallAsync(toolCall.Id, "waiting_approval");
            var approval = await repository.CreateWorkApprovalAsync(conversation.Id, runId, "call-1", "{\"summary\":\"读取资料目录\"}", "task_directory", @"D:\资料", "read");
            var pending = await repository.ListPendingWorkApprovalsAsync(conversation.Id);
            await repository.ResolveWorkApprovalAsync(approval.Id, "rejected");
            var artifact = await repository.AddWorkArtifactAsync(conversation.Id, runId, @"outputs\report.docx", "报告");
            var artifacts = await repository.ListWorkArtifactsAsync(conversation.Id);

            Assert.Equal(new long[] { 1, 2 }, context.Select(item => item.Ordinal));
            Assert.Equal(new[] { "message", "tool_call" }, context.Select(item => item.Kind));
            Assert.Equal(approval.Id, Assert.Single(pending).Id);
            Assert.Empty(await repository.ListPendingWorkApprovalsAsync(conversation.Id));
            Assert.Equal(artifact.Id, Assert.Single(artifacts).Id);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-assistant-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed class NoopProvider : IChatProvider
    {
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            IReadOnlyList<ConversationMessage> history,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<string> GenerateTitleAsync(string userMessage, string assistantMessage, string model, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult("标题");
    }
}
