using System.Text.Json;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Models;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WebSearchToolExecutorTests
{
    [Fact]
    public void DeepSeekAdapterMapsSearchResponseAndRejectsUnsafeUrls()
    {
        const string body = """
            {"results":[
              {"rerank_score":2.4,"title":"官方结果","url":"https://example.com/news","snippet":"短摘要","summary":"供模型使用的长摘要","content":"","site_name":"Example","site_icon":null,"date":"2026-10-25T00:00:00Z","authority":3},
              {"rerank_score":1,"title":"危险结果","url":"javascript:alert(1)","snippet":"bad","summary":"bad","content":"","site_name":null,"site_icon":null,"date":null,"authority":0}
            ]}
            """;

        var outcome = DeepSeekWebSearchClient.Parse(body);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("DeepSeek", outcome.EngineType);
        Assert.Equal("供模型使用的长摘要", result.Snippet);
        Assert.Equal("2026-10-25T00:00:00Z", result.PublishedTime);
        Assert.Equal(3, result.Authority);
        Assert.Equal(2.4, result.RerankScore);
    }

    [Fact]
    public async Task DeepSeekSearchSendsTheOriginalQuestion()
    {
        var handler = new StubHandler("""
            {"results":[{"title":"官方天气","url":"https://weather.cma.cn/a","summary":"晴","date":"2026-10-25","site_name":"中国气象局","authority":3,"rerank_score":1.2}]}
            """);
        using var client = new DeepSeekWebSearchClient(new HttpClient(handler));

        var outcome = await client.SearchAsync(
            new WebSearchRequest("杭州天气", 4, null, "杭州现在天气怎么样？"), "deepseek-key", CancellationToken.None);

        var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("杭州天气", sent.RootElement.GetProperty("query").GetString());
        Assert.Equal("杭州现在天气怎么样？", sent.RootElement.GetProperty("ori_question").GetString());
        var item = Assert.Single(outcome.Results);
        Assert.Equal("https://weather.cma.cn/a", item.Url);
        Assert.Equal(3, item.Authority);
        Assert.Equal(1.2, item.RerankScore);
    }

    [Fact]
    public async Task NormalizesProviderResultsAndAssignsTrustedCitationOrdinals()
    {
        var provider = new FakeProvider(new WebSearchOutcome(
            [new WebSearchResult("示例", "https://example.com/a", "摘要", "2026-10-25", "Example")],
            "fake", 12));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);
        var session = Session("原始问题");

        var result = await executor.ExecuteAsync("""{"query":"测试","count":3,"timeRange":"OneWeek"}""", session, default);

        var citation = Assert.Single(result.Citations);
        Assert.Equal(1, citation.Ordinal);
        Assert.Equal("https://example.com/a", citation.Url);
        var output = JsonDocument.Parse(result.OutputJson).RootElement;
        var item = Assert.Single(output.GetProperty("results").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sourceId").GetString()));
        Assert.Equal(1, item.GetProperty("ordinal").GetInt32());
        Assert.Equal("[ref_1]", item.GetProperty("citation").GetString());
        Assert.Equal(1, output.GetProperty("queryCount").GetInt32());
        Assert.Equal(1, output.GetProperty("rawResultCount").GetInt32());
        Assert.Equal(1, output.GetProperty("uniqueResultCount").GetInt32());
        Assert.Equal(1, output.GetProperty("sourceCount").GetInt32());
        Assert.Equal(new WebSearchRequest("测试", 3, "OneWeek", "原始问题"), provider.Request);
        Assert.Equal("secret", provider.ApiKey);
    }

    [Fact]
    public async Task DisabledSearchDoesNotCallProvider()
    {
        var provider = new FakeProvider(new WebSearchOutcome([], "fake", null));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => false);

        var result = await executor.ExecuteAsync("""{"query":"测试"}""", Session(), default);

        Assert.Null(provider.Request);
        Assert.Empty(result.Citations);
        Assert.Contains("search_disabled", result.OutputJson);
    }

    [Fact]
    public async Task RejectsUnsafeAndDuplicateProviderUrls()
    {
        var provider = new FakeProvider(new WebSearchOutcome(
            [
                new WebSearchResult("bad", "javascript:alert(1)", "", null, null),
                new WebSearchResult("first", "https://example.com/a", "", null, null),
                new WebSearchResult("duplicate", "https://example.com/a", "", null, null)
            ], "fake", null));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);

        var result = await executor.ExecuteAsync("""{"query":"测试"}""", Session(), default);

        Assert.Single(result.Citations);
        Assert.Equal("first", result.Citations[0].Title);
        Assert.DoesNotContain("javascript:", result.OutputJson);
        Assert.Contains("\"duplicateResultCount\":1", result.OutputJson);
    }

    [Fact]
    public async Task SameSearchDeduplicatesTrackingAndFragmentVariants()
    {
        var provider = new FakeProvider(new WebSearchOutcome(
            [
                new WebSearchResult("一", "https://Example.com/a?utm_source=x#top", "摘要", null, null),
                new WebSearchResult("一（重复）", "https://example.com/a/", "更完整摘要", null, null)
            ], "fake", null));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);

        var result = await executor.ExecuteAsync("""{"query":"测试"}""", Session(), default);

        var citation = Assert.Single(result.Citations);
        Assert.Equal(1, citation.Ordinal);
        Assert.Contains("\"duplicateResultCount\":1", result.OutputJson);
        Assert.Contains("\"newResultCount\":1", result.OutputJson);
        Assert.Single(JsonDocument.Parse(result.OutputJson).RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task RepeatedSearchesDeduplicateUrlsAndReuseCitationOrdinals()
    {
        var provider = new SequencedProvider(
            new WebSearchOutcome(
                [
                    new WebSearchResult("一", "https://example.com/1", "a", null, null),
                    new WebSearchResult("二", "https://example.com/2", "b", null, null)
                ], "fake", 5),
            new WebSearchOutcome(
                [
                    new WebSearchResult("二", "https://EXAMPLE.com/2", "b2", null, null),
                    new WebSearchResult("三", "https://example.com/3", "c", null, null)
                ], "fake", 7));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);
        var session = Session();

        var first = await executor.ExecuteAsync("""{"query":"第一次"}""", session, default);
        var second = await executor.ExecuteAsync("""{"query":"第二次"}""", session, default);

        Assert.Equal(2, first.UniqueResultCount);
        Assert.Equal(2, first.RawResultCount);
        Assert.Equal(3, second.UniqueResultCount);
        Assert.Equal(4, second.RawResultCount);
        Assert.Equal(2, second.QueryCount);
        Assert.Equal(new[] { 1, 2 }, first.Citations.Select(item => item.Ordinal));
        Assert.Equal(new[] { 3 }, second.Citations.Select(item => item.Ordinal));
        Assert.DoesNotContain("[ref_2]", second.OutputJson);
        Assert.Contains("\"duplicateResultCount\":1", second.OutputJson);
        Assert.Contains("\"uniqueResultCount\":3", second.OutputJson);
        Assert.Equal(3, session.Registry.Sources.Count);
        Assert.Equal(new[] { 1, 2, 3 }, session.Registry.Sources.Select(source => source.Ordinal));
    }

    [Fact]
    public async Task AllDuplicateResultsKeepTheSearchOperationButReturnNoNewSources()
    {
        var provider = new FakeProvider(new WebSearchOutcome(
            [new WebSearchResult("一", "https://example.com/1", "a", null, null)], "fake", 1));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);
        var session = Session();

        await executor.ExecuteAsync("""{"query":"第一次"}""", session, default);
        var duplicateOnly = await executor.ExecuteAsync("""{"query":"第二次"}""", session, default);

        Assert.Empty(duplicateOnly.Citations);
        Assert.Empty(duplicateOnly.NewSources);
        var note = JsonDocument.Parse(duplicateOnly.OutputJson).RootElement.GetProperty("note").GetString();
        Assert.Contains("未发现新增资料", note);
        Assert.Contains("\"duplicateResultCount\":1", duplicateOnly.OutputJson);
        Assert.Equal(2, session.QueryCount);
        Assert.Single(session.Registry.Sources);
    }

    [Fact]
    public async Task LimitsSearchesWithinARun()
    {
        var results = Enumerable.Range(1, 8)
            .Select(index => new WebSearchResult($"结果{index}", $"https://example.com/{index}", "", null, null))
            .ToArray();
        var provider = new SequencedProvider(
            new WebSearchOutcome(results, "fake", 1),
            new WebSearchOutcome(results.Select(item => item with { Url = item.Url + "?copy" }).ToArray(), "fake", 1),
            new WebSearchOutcome([new WebSearchResult("另一条", "https://example.com/other", "", null, null)], "fake", 1),
            new WebSearchOutcome([], "fake", 1));
        var executor = new WebSearchToolExecutor(provider, () => "secret", () => true);
        var session = Session();

        await executor.ExecuteAsync("""{"query":"一"}""", session, default);
        await executor.ExecuteAsync("""{"query":"二"}""", session, default);
        await executor.ExecuteAsync("""{"query":"三"}""", session, default);
        var blocked = await executor.ExecuteAsync("""{"query":"四"}""", session, default);

        Assert.Equal(ChatSearchSession.MaxQueryCount, session.QueryCount);
        Assert.Equal(17, session.Registry.Sources.Count);
        Assert.Equal("search_limit_reached", blocked.ErrorCode);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task ChatToolLoopPersistsOnlyProviderSuppliedCitations()
    {
        var database = Path.Combine(Path.GetTempPath(), $"cat-chat-web-search-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteRepository(database);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("chat");
            var search = new FakeProvider(new WebSearchOutcome(
                [new WebSearchResult("可信标题", "https://example.com/source", "可信摘要", null, "Example")], "fake", 1));
            var model = new SearchCallingModel();
            var executor = new WebSearchToolExecutor(search, () => "search-key", () => true);
            await using var service = new ConversationService(
                repository, model, () => new AppSettings(), () => "model-key", CancellationToken.None, webSearch: executor);
            await service.InitializeAsync();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += (_, update) => { if (update.Type == "message.completed" && update.ConversationId == conversation.Id) completed.TrySetResult(); };

            await service.SendAsync(conversation.Id, "查一下", "desktop");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var detail = await service.GetAsync(conversation.Id);
            var citation = Assert.Single(detail.Citations);
            Assert.Equal("https://example.com/source", citation.Url);
            Assert.Contains("[ref_1]", detail.Messages.Single(item => item.Role == "assistant").Content);
            var source = Assert.Single(detail.ConversationSources!);
            Assert.Equal(1, source.Ordinal);
            Assert.Equal("call-web", source.FirstToolCallId);
            var operation = Assert.Single(detail.ChatToolCalls!);
            Assert.Equal(source.FirstOperationId, operation.Id);
            Assert.Equal(source.Id, Assert.Single(operation.NewSourceIds!));
            var toolResult = Assert.Single(model.SecondTurn!, item => item.Role == "tool");
            Assert.Contains("\"citation\":\"[ref_1]\"", toolResult.Content);
        }
        finally
        {
            foreach (var candidate in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static ChatSearchSession Session(string? originalQuestion = null) =>
        new(new ChatSourceRegistry("test-session"), originalQuestion);

    private sealed class FakeProvider(WebSearchOutcome outcome) : IWebSearchProvider
    {
        public WebSearchRequest? Request { get; private set; }
        public string? ApiKey { get; private set; }

        public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, string apiKey, CancellationToken cancellationToken)
        {
            Request = request;
            ApiKey = apiKey;
            return Task.FromResult(outcome);
        }
    }

    private sealed class SequencedProvider(params WebSearchOutcome[] outcomes) : IWebSearchProvider
    {
        private int _index;
        public int Calls => _index;

        public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, string apiKey, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return Task.FromResult(outcomes[Math.Min(index, outcomes.Length - 1)]);
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SearchCallingModel : IChatProvider, IChatToolLoopProvider
    {
        private int _turn;
        public IReadOnlyList<ChatModelMessage>? SecondTurn { get; private set; }

        public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
            IReadOnlyList<ChatModelMessage> messages, string model, string apiKey, bool enableWebSearch,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _turn) == 1)
                yield return new ChatStreamChunk(ToolCall: new ChatToolCall("call-web", "web_search", "{\"query\":\"测试\"}"));
            else
            {
                SecondTurn = messages.ToArray();
                yield return new ChatStreamChunk(Delta: "结果见来源[ref_1]。");
            }
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
            Task.FromResult("联网查询");

        private static ContextBudgetAssessment Budget(string model)
        {
            var profile = new ModelContextProfile(model, 32_000, 2_000, 1_000);
            return new ContextBudgetAssessment(profile, new TokenEstimate(500, "test", true, 500, 500), 0.02, false, false);
        }
    }
}
