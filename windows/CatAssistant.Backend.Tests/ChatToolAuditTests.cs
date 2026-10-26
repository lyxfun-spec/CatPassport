using System.Runtime.CompilerServices;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ChatToolAuditTests
{
    [Fact]
    public async Task FollowUpAnswersFromPersistedSearchAuditAndStatusStaysOutOfHistory()
    {
        var path = TempDatabasePath();
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            var search = new FakeSearchProvider(new WebSearchOutcome(
                [new WebSearchResult("可信标题", "https://example.com/source", "可信摘要", "2026-10-25", "Example")],
                "fake", 12));
            var executor = new WebSearchToolExecutor(search, () => "search-key", () => true);
            var model = new ScriptedToolProvider();
            await using var service = new ConversationService(
                repository, model, () => new AppSettings(), () => "model-key", CancellationToken.None, webSearch: executor);
            await service.InitializeAsync();

            await SendAndWaitAsync(service, conversation.Id, "杭州天气");

            var detail = await service.GetAsync(conversation.Id);
            var record = Assert.Single(detail.ChatToolCalls!);
            Assert.Equal("web_search", record.ToolName);
            Assert.Equal("completed", record.State);
            Assert.Equal("杭州天气", record.Query);
            Assert.Equal(1, record.QueryCount);
            Assert.Equal(1, record.RawResultCount);
            Assert.Equal(1, record.UniqueResultCount);
            Assert.Equal("我来搜索一下。".Length, record.AnchorOffset);
            Assert.Contains("https://example.com/source", record.ResultJson);
            var source = Assert.Single(detail.ConversationSources!);
            Assert.Equal(1, source.Ordinal);
            Assert.Equal("https://example.com/source", source.Url);
            Assert.Equal("example.com", source.Domain);
            Assert.Equal(record.Id, source.FirstOperationId);
            Assert.Equal("call-search", source.FirstToolCallId);
            Assert.Equal(source.Id, Assert.Single(record.NewSourceIds!));
            var assistant = Assert.Single(detail.Messages, message => message.Role == "assistant");
            Assert.Contains("我来搜索一下。\n\n今天杭州晴", assistant.Content);
            Assert.DoesNotContain("正在搜索资料", assistant.Content);
            Assert.Equal("杭州天气", Assert.Single(search.Requests).OriginalQuestion);

            await SendAndWaitAsync(service, conversation.Id, "你刚才搜索了几次？");

            var followUp = model.Turns[^1];
            var audit = Assert.Single(followUp, message => message.Role == "system" && message.Content?.Contains("Tool audit") == true);
            Assert.Contains("query=\"杭州天气\"", audit.Content);
            Assert.Contains("queryCount=1", audit.Content);
            Assert.Contains("rawResultCount=1", audit.Content);
            Assert.Contains("uniqueResultCount=1", audit.Content);
            var registry = Assert.Single(followUp, message => message.Role == "system" && message.Content?.Contains("Conversation source registry") == true);
            Assert.Contains("[ref_1]", registry.Content);
            Assert.Contains("https://example.com/source", registry.Content);
            Assert.DoesNotContain(followUp, message => message.Content?.Contains("正在搜索资料") == true);
            Assert.DoesNotContain((await service.GetAsync(conversation.Id)).Messages,
                message => message.Content.Contains("正在搜索资料"));
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task ToolRecordsSurviveRestartAndRestoreTheAuditForTheNextTurn()
    {
        var path = TempDatabasePath();
        try
        {
            string conversationId;
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            conversationId = (await repository.CreateConversationAsync("chat")).Id;
            var executor = new WebSearchToolExecutor(
                new FakeSearchProvider(new WebSearchOutcome(
                    [new WebSearchResult("可信标题", "https://example.com/source", "可信摘要", "2026-10-25", "Example")],
                    "fake", 3)),
                () => "search-key", () => true);
            await using (var first = new ConversationService(
                repository, new ScriptedToolProvider(), () => new AppSettings(), () => "model-key",
                CancellationToken.None, webSearch: executor))
            {
                await first.InitializeAsync();
                await SendAndWaitAsync(first, conversationId, "杭州天气");
            }

            var reopened = new SqliteRepository(path);
            await reopened.InitializeAsync();
            var captured = new CaptureProvider();
            await using var restarted = new ConversationService(
                reopened, captured, () => new AppSettings(), () => "model-key", CancellationToken.None);
            await restarted.InitializeAsync();

            var detail = await restarted.GetAsync(conversationId);
            var record = Assert.Single(detail.ChatToolCalls!);
            Assert.Equal("completed", record.State);
            Assert.Equal("杭州天气", record.Query);
            Assert.Equal(1, record.UniqueResultCount);
            var source = Assert.Single(detail.ConversationSources!);
            Assert.Equal(1, source.Ordinal);
            Assert.Equal(record.Id, source.FirstOperationId);

            await SendAndWaitAsync(restarted, conversationId, "上一次用了几个来源？");

            var followUp = Assert.Single(captured.Turns);
            var audit = Assert.Single(followUp, message => message.Role == "system" && message.Content?.Contains("Tool audit") == true);
            Assert.Contains("uniqueResultCount=1", audit.Content);
            var registry = Assert.Single(followUp, message => message.Role == "system" && message.Content?.Contains("Conversation source registry") == true);
            Assert.Contains("[ref_1]", registry.Content);
            Assert.Contains("https://example.com/source", registry.Content);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task DuplicateOnlySearchRemainsAsAnOperationAndKeepsOneStableSourceNumber()
    {
        var path = TempDatabasePath();
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            var executor = new WebSearchToolExecutor(
                new FakeSearchProvider(new WebSearchOutcome(
                    [new WebSearchResult("同一资料", "https://example.com/same", "摘要", "2026-10-25", "Example")],
                    "fake", 1)),
                () => "search-key", () => true);
            var model = new TwoSearchToolProvider();
            await using var service = new ConversationService(
                repository, model, () => new AppSettings(), () => "model-key", CancellationToken.None, webSearch: executor);
            await service.InitializeAsync();

            await SendAndWaitAsync(service, conversation.Id, "查两次");

            var detail = await service.GetAsync(conversation.Id);
            Assert.Equal(2, detail.ChatToolCalls!.Count);
            var first = detail.ChatToolCalls[0];
            var second = detail.ChatToolCalls[1];
            Assert.Equal("第一次", first.Query);
            Assert.Equal("第二次", second.Query);
            var source = Assert.Single(detail.ConversationSources!);
            Assert.Equal(1, source.Ordinal);
            Assert.Equal(first.Id, source.FirstOperationId);
            Assert.Equal("call-first", source.FirstToolCallId);
            Assert.Equal(source.Id, Assert.Single(first.NewSourceIds!));
            Assert.Empty(second.NewSourceIds!);
            var note = System.Text.Json.JsonDocument.Parse(second.ResultJson!).RootElement.GetProperty("note").GetString();
            Assert.Contains("未发现新增资料", note);
            Assert.DoesNotContain("https://example.com/same", second.ResultJson);
            var assistant = Assert.Single(detail.Messages, message => message.Role == "assistant");
            Assert.Contains("两次结果指向同一资料[ref_1]", assistant.Content);

            await SendAndWaitAsync(service, conversation.Id, "继续用上一轮的资料");

            var followUp = model.Turns[^1];
            var registry = Assert.Single(followUp, message => message.Role == "system" && message.Content?.Contains("Conversation source registry") == true);
            Assert.Contains("[ref_1]", registry.Content);
            Assert.DoesNotContain("[ref_2]", registry.Content);
            Assert.Contains("https://example.com/same", registry.Content);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task SearchFailureIsRecordedButTheModelStillAnswers()
    {
        var path = TempDatabasePath();
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            var executor = new WebSearchToolExecutor(new FailingSearchProvider(), () => "search-key", () => true);
            var model = new ScriptedToolProvider(answer: "搜索服务暂时失败，但我可以先回答。");
            await using var service = new ConversationService(
                repository, model, () => new AppSettings(), () => "model-key", CancellationToken.None, webSearch: executor);
            await service.InitializeAsync();

            await SendAndWaitAsync(service, conversation.Id, "杭州天气");

            var detail = await service.GetAsync(conversation.Id);
            var record = Assert.Single(detail.ChatToolCalls!);
            Assert.Equal("failed", record.State);
            Assert.Equal("search_failed", record.ErrorCode);
            Assert.Contains("search_failed", record.ResultJson);
            var assistant = Assert.Single(detail.Messages, message => message.Role == "assistant");
            Assert.Contains("搜索服务暂时失败，但我可以先回答。", assistant.Content);
            Assert.DoesNotContain("正在搜索资料", assistant.Content);
        }
        finally { DeleteDatabase(path); }
    }

    private static async Task SendAndWaitAsync(ConversationService service, string conversationId, string text)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, ConversationEvent update)
        {
            if (update.ConversationId != conversationId) return;
            if (update.Type == "message.completed") completed.TrySetResult();
            else if (update.Type == "run.failed") completed.TrySetException(new InvalidOperationException(update.Error ?? "run failed"));
        }
        service.Changed += Handler;
        try
        {
            await service.SendAsync(conversationId, text, "desktop");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { service.Changed -= Handler; }
    }

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"cat-chat-tools-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed class FakeSearchProvider(WebSearchOutcome outcome) : IWebSearchProvider
    {
        public List<WebSearchRequest> Requests { get; } = [];

        public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, string apiKey, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private sealed class FailingSearchProvider : IWebSearchProvider
    {
        public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, string apiKey, CancellationToken cancellationToken) =>
            throw new WebSearchException("search_failed", "搜索服务暂时不可用。");
    }

    private sealed class ScriptedToolProvider(string firstDelta = "我来搜索一下。", string answer = "今天杭州晴，来源[ref_1]。")
        : IChatProvider, IChatToolLoopProvider
    {
        private int _turn;

        public List<IReadOnlyList<ChatModelMessage>> Turns { get; } = [];

        public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
            IReadOnlyList<ChatModelMessage> messages,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(messages.ToArray());
            var turn = Interlocked.Increment(ref _turn);
            await Task.Yield();
            if (turn == 1)
            {
                yield return new ChatStreamChunk(Delta: firstDelta);
                yield return new ChatStreamChunk(ToolCall: new ChatToolCall("call-search", "web_search", """{"query":"杭州天气"}"""));
            }
            else if (turn == 2) yield return new ChatStreamChunk(Delta: answer);
            else yield return new ChatStreamChunk(Delta: "本轮共搜索 1 次，使用 1 个来源。");
        }

        public ContextBudgetAssessment AssessChatTurn(IReadOnlyList<ChatModelMessage> messages, string model, bool enableWebSearch) => Budget(model);

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
            Task.FromResult("联网查询");
    }

    private sealed class TwoSearchToolProvider : IChatProvider, IChatToolLoopProvider
    {
        private int _turn;

        public List<IReadOnlyList<ChatModelMessage>> Turns { get; } = [];

        public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
            IReadOnlyList<ChatModelMessage> messages,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(messages.ToArray());
            var turn = Interlocked.Increment(ref _turn);
            await Task.Yield();
            if (turn == 1) yield return new ChatStreamChunk(ToolCall: new ChatToolCall("call-first", "web_search", """{"query":"第一次"}"""));
            else if (turn == 2) yield return new ChatStreamChunk(ToolCall: new ChatToolCall("call-second", "web_search", """{"query":"第二次"}"""));
            else if (turn == 3) yield return new ChatStreamChunk(Delta: "两次结果指向同一资料[ref_1]。");
            else yield return new ChatStreamChunk(Delta: "已使用上一轮资料[ref_1]。");
        }

        public ContextBudgetAssessment AssessChatTurn(IReadOnlyList<ChatModelMessage> messages, string model, bool enableWebSearch) => Budget(model);

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
            Task.FromResult("两次搜索");
    }

    private sealed class CaptureProvider : IChatProvider, IChatToolLoopProvider
    {
        public List<IReadOnlyList<ChatModelMessage>> Turns { get; } = [];

        public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
            IReadOnlyList<ChatModelMessage> messages,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(messages.ToArray());
            await Task.Yield();
            yield return new ChatStreamChunk(Delta: "已恢复。");
        }

        public ContextBudgetAssessment AssessChatTurn(IReadOnlyList<ChatModelMessage> messages, string model, bool enableWebSearch) => Budget(model);

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
            Task.FromResult("已恢复");
    }

    private static ContextBudgetAssessment Budget(string model)
    {
        var profile = new ModelContextProfile(model, 32_000, 2_000, 1_000);
        return new ContextBudgetAssessment(profile, new TokenEstimate(500, "test", true, 500, 500), 0.02, false, false);
    }
}
