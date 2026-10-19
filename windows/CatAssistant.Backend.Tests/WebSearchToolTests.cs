using System.Runtime.CompilerServices;
using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WebSearchToolTests
{
    [Fact]
    public async Task UsesExistingSearchProviderAndReturnsGroundedSources()
    {
        var provider = new SearchProvider();
        var tool = new WebSearchTool(provider, () => new AppSettings { ChatModel = "chat-model", WorkModel = "work-model" }, () => "test-key");
        var context = new WorkToolExecutionContext(Guid.NewGuid().ToString("N"), "run", Path.GetTempPath(), "call");

        var result = await tool.ExecuteAsync(context, "{\"query\":\"今日公开信息\"}", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(provider.SearchEnabled);
        Assert.Equal("work-model", provider.Model);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("检索结果", json.RootElement.GetProperty("answer").GetString());
        var source = Assert.Single(json.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal("https://example.com/source", source.GetProperty("url").GetString());
    }

    private sealed class SearchProvider : IChatProvider
    {
        public bool SearchEnabled { get; private set; }
        public string? Model { get; private set; }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            IReadOnlyList<ConversationMessage> history,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchEnabled = enableWebSearch;
            Model = model;
            Assert.Contains("今日公开信息", Assert.Single(history).Content);
            yield return new ChatStreamChunk(Citations:
            [new CitationRecord("source", "", 1, "来源", "https://example.com/source", "摘要", "示例")]);
            await Task.Yield();
            yield return new ChatStreamChunk(Delta: "检索结果");
        }

        public Task<string> GenerateTitleAsync(string userMessage, string assistantMessage, string model, string apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
