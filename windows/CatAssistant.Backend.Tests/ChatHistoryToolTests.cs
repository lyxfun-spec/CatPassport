using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ChatHistoryToolTests
{
    [Fact]
    public async Task ChatHistoryToolReturnsCurrentConversationDataAndContinuesSameRun()
    {
        var database = Path.Combine(Path.GetTempPath(), $"cat-chat-search-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteRepository(database);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            var old = await repository.AddMessageAsync(conversation.Id, "user", "desktop", "complete", "我们之前决定采用蓝色封面");
            var provider = new HistoryCallingProvider();
            await using var service = new ConversationService(repository, provider, () => new AppSettings(), () => "key", CancellationToken.None);
            await service.InitializeAsync();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += (_, update) => { if (update.Type == "message.completed" && update.ConversationId == conversation.Id) completed.TrySetResult(); };

            await service.SendAsync(conversation.Id, "我们之前定的封面是什么？", "desktop");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(2, provider.Turns.Count);
            var toolResult = Assert.Single(provider.Turns[1], item => item.Role == "tool");
            Assert.Equal("call-search", toolResult.ToolCallId);
            Assert.Contains(old.Id, toolResult.Content);
            using (var result = System.Text.Json.JsonDocument.Parse(toolResult.Content!))
                Assert.Contains("蓝色封面", result.RootElement.GetProperty("results")[0].GetProperty("Snippet").GetString());
            var answer = Assert.Single(await repository.LoadMessagesAsync(conversation.Id), item => item.Role == "assistant");
            Assert.Equal("找到原始记录：蓝色封面。", answer.Content);
        }
        finally
        {
            foreach (var candidate in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private sealed class HistoryCallingProvider : IChatProvider, IChatToolLoopProvider
    {
        private int _turn;
        public List<IReadOnlyList<ChatModelMessage>> Turns { get; } = [];

        public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
            IReadOnlyList<ChatModelMessage> messages, string model, string apiKey, bool enableWebSearch,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(messages.ToArray());
            if (Interlocked.Increment(ref _turn) == 1)
                yield return new ChatStreamChunk(ToolCall: new ChatToolCall("call-search", "search_conversation_history", "{\"query\":\"蓝色封面\",\"topK\":4}"));
            else yield return new ChatStreamChunk(Delta: "找到原始记录：蓝色封面。");
            await Task.CompletedTask;
        }

        public ContextBudgetAssessment AssessChatTurn(IReadOnlyList<ChatModelMessage> messages, string model, bool enableWebSearch) => Budget(model);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            IReadOnlyList<ConversationMessage> history, string model, string apiKey, bool enableWebSearch,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<string> GenerateTitleAsync(string userMessage, string assistantMessage, string model, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult("历史查询");

        private static ContextBudgetAssessment Budget(string model)
        {
            var profile = new ModelContextProfile(model, 32_000, 2_000, 1_000);
            return new ContextBudgetAssessment(profile, new TokenEstimate(500, "test", true, 500, 500), 500d / profile.InputBudgetTokens, false, false);
        }
    }
}
