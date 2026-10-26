using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ChatSourceRegistryTests
{
    [Fact]
    public async Task DuplicateUrlUpdatesDetailsButKeepsOrdinalAndFirstOwnership()
    {
        var registry = new ChatSourceRegistry("chat-1");
        var first = Ranked("短标题", "https://example.com/a?utm_source=x", "短摘要", 1);
        await registry.ResolveAsync([first], "operation-1", "call-1", default);
        var original = Assert.Single(registry.Sources);

        var duplicate = Ranked("更完整的标题", "https://Example.com/a/", "更完整的摘要内容", 2);
        var resolution = await registry.ResolveAsync([duplicate], "operation-2", "call-2", default);

        var updated = Assert.Single(registry.Sources);
        Assert.Equal(1, updated.Ordinal);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("operation-1", updated.FirstOperationId);
        Assert.Equal("call-1", updated.FirstToolCallId);
        Assert.Equal("更完整的标题", updated.Title);
        Assert.Equal("更完整的摘要内容", updated.Snippet);
        Assert.Empty(resolution.NewSources);
        Assert.Single(resolution.UpdatedSources);
        Assert.Equal(1, resolution.DuplicateCount);
    }

    [Fact]
    public async Task NewSourcesContinueAfterPersistedOrdinals()
    {
        var existing = new ConversationSourceRecord(
            "src-existing", "chat-1", 7, "已有资料", "https://example.com/old", "https://example.com/old",
            "摘要", "Example", "example.com", "call-old", "operation-old",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var registry = new ChatSourceRegistry("chat-1", [existing]);

        var resolution = await registry.ResolveAsync(
            [Ranked("新资料", "https://example.com/new", "摘要", 1)], "operation-new", "call-new", default);
        var source = Assert.Single(resolution.NewSources);

        Assert.Equal(8, source.Ordinal);
        Assert.Equal(new[] { 7, 8 }, registry.Sources.Select(item => item.Ordinal));
    }

    [Fact]
    public async Task OrdinalFloorAvoidsOldPerMessageCitationNumbers()
    {
        var registry = new ChatSourceRegistry("chat-1", [], ordinalFloor: 4);

        var resolution = await registry.ResolveAsync(
            [Ranked("新资料", "https://example.com/new", "摘要", 1)], "operation-new", "call-new", default);

        Assert.Equal(5, Assert.Single(resolution.NewSources).Ordinal);
    }

    private static RankedWebSearchResult Ranked(string title, string url, string snippet, double score) =>
        new(new WebSearchResult(title, url, snippet, "2026-10-25", "Example"), false, "unknown", false, score);
}
