using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ContextManagementTests
{
    [Fact]
    public void ConservativeEstimatorCountsCjkAndJsonInsteadOfTreatingThemAsFree()
    {
        var estimate = new ConservativeTokenEstimator().Estimate("{\"内容\":\"上下文管理\",\"tool\":true}");

        Assert.True(estimate.Tokens >= 10);
        Assert.True(estimate.IsEstimate);
        Assert.Equal("conservative_utf8", estimate.Source);
        Assert.True(estimate.PayloadBytes > estimate.PayloadCharacters);
    }

    [Fact]
    public async Task CompactionActivatesVersionAtomicallyAndKeepsAuthoritativeMessages()
    {
        var path = TemporaryDatabase();
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            for (var index = 0; index < 12; index++)
                await repository.AddMessageAsync(conversation.Id, index % 2 == 0 ? "user" : "assistant", index % 2 == 0 ? "desktop" : "runtime", "complete", new string((char)('甲' + index), 600));
            var provider = new CompactingBudgetProvider();
            var manager = new ConversationContextManager(repository, provider, () => "key");

            var prepared = await manager.PrepareChatAsync(conversation.Id, await repository.LoadMessagesAsync(conversation.Id), "test-model", false, CancellationToken.None);

            var summary = await repository.GetActiveSummaryAsync(conversation.Id);
            Assert.NotNull(summary);
            Assert.Equal(4, summary!.EndSequence);
            Assert.Equal(12, (await repository.LoadMessagesAsync(conversation.Id)).Count);
            Assert.StartsWith("summary-", prepared.Messages[0].Id);
            Assert.Contains("Earlier conversation history has been compacted", prepared.Messages[0].Content);
            Assert.Contains("search_conversation_history", prepared.Messages[0].Content);
            Assert.Contains("保留：用户要求、已确认决定与未解决问题。", prepared.Messages[0].Content);
            Assert.Equal(9, prepared.Messages.Count);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task HybridSearchIsConversationScopedAndCanReadMessageByStableId()
    {
        var path = TemporaryDatabase();
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var first = await repository.CreateConversationAsync("chat");
            var second = await repository.CreateConversationAsync("chat");
            var target = await repository.AddMessageAsync(first.Id, "user", "desktop", "complete", "之前决定用蓝色封面并保留时间线");
            await repository.AddMessageAsync(second.Id, "user", "desktop", "complete", "另一个对话也提到了蓝色封面");
            var search = new HistorySearchService(repository);

            var hits = await search.SearchAsync(first.Id, "蓝色封面", 8);
            var loaded = await search.GetMessageAsync(first.Id, target.Id);

            Assert.Contains(hits, item => item.MessageId == target.Id);
            Assert.All(hits, item => Assert.Equal(first.Id, item.ConversationId));
            Assert.Equal(target.Content, loaded?.Content);
            Assert.Null(await search.GetMessageAsync(second.Id, target.Id));
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task WorkHistoryCopyCanBeDeletedAndRecreatedWithoutChangingAuthority()
    {
        var path = TemporaryDatabase();
        var root = Path.Combine(Path.GetTempPath(), $"cat-context-work-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            var message = await repository.AddMessageAsync(conversation.Id, "user", "desktop", "complete", "制作季度报告");
            using var workspaces = new WorkWorkspaceService(root);
            var workspace = await workspaces.EnsureAsync(conversation.Id);
            var exporter = new WorkHistoryExporter(repository);
            await exporter.ExportAsync(conversation.Id, workspace, CancellationToken.None);
            var history = Path.Combine(workspace.Work, ".context", "history.jsonl");
            File.Delete(history);

            await exporter.ExportAsync(conversation.Id, workspace, CancellationToken.None);

            Assert.True(File.Exists(history));
            var exported = JsonDocument.Parse((await File.ReadAllLinesAsync(history))[0]);
            Assert.Equal(message.Content, exported.RootElement.GetProperty("content").GetString());
            Assert.Equal(message.Content, (await repository.GetMessageAsync(conversation.Id, message.Id))?.Content);
        }
        finally
        {
            DeleteDatabase(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LargeToolResultIsStoredCompletelyAndModelReceivesBoundedReference()
    {
        var path = TemporaryDatabase();
        var root = Path.Combine(Path.GetTempPath(), $"cat-context-tool-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            using var workspaces = new WorkWorkspaceService(root);
            var workspace = await workspaces.EnsureAsync(conversation.Id);
            var exporter = new WorkHistoryExporter(repository);
            var original = JsonSerializer.Serialize(new { content = new string('数', 20_000) });

            var bounded = await exporter.ExternalizeToolResultAsync(
                workspace, "run-1", new WorkModelToolCall("call-1", "read_file", "{}"), new WorkToolResult(true, original), CancellationToken.None);

            using var reference = JsonDocument.Parse(bounded.OutputJson);
            var relative = reference.RootElement.GetProperty("path").GetString()!;
            Assert.True(bounded.OutputJson.Length < original.Length);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            DeleteDatabase(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CompactingBudgetProvider : IChatRequestBudgetProvider, IContextCompactionProvider
    {
        public ContextBudgetAssessment AssessChatRequest(IReadOnlyList<ConversationMessage> history, string model, bool enableWebSearch)
        {
            var compacted = history.Any(item => item.Role == "system");
            var profile = new ModelContextProfile(model, 1_200, 100, 100);
            var tokens = compacted ? 300 : 900;
            return new ContextBudgetAssessment(profile, new TokenEstimate(tokens, "test", true, tokens, tokens), tokens / 1_000d, !compacted, !compacted);
        }

        public Task<ContextCompactionResult> CompactAsync(string mode, string? existingSummary, IReadOnlyList<CompactionInputItem> items, string model, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new ContextCompactionResult("保留：用户要求、已确认决定与未解决问题。"));
    }

    private static string TemporaryDatabase() => Path.Combine(Path.GetTempPath(), $"cat-context-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}
