using System.Text.Json;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ChatProviderTests
{
    [Fact]
    public void ChatPromptAddsSearchGuidanceOnlyWhenSearchIsAvailable()
    {
        var withoutSearch = DashScopeClient.BuildChatSystemPrompt(false);
        var withSearch = DashScopeClient.BuildChatSystemPrompt(true);

        Assert.Contains("Reply clearly and concisely in the user's language", withoutSearch);
        Assert.Contains("call request_work directly", withoutSearch);
        Assert.Contains("ask whether they want it", withoutSearch);
        Assert.Contains("calling the tool does not mean work has started", withoutSearch);
        Assert.DoesNotContain("web search", withoutSearch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use the web_search tool", withSearch);
        Assert.Contains("cite only the exact [ref_n] markers", withSearch);
    }

    [Fact]
    public void ChatToolsUseConciseEnglishDefinitions()
    {
        var payload = DashScopeClient.BuildChatTurnPayload([], "test-model", false);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var tools = json.RootElement.GetProperty("tools");
        var requestWork = tools[0].GetProperty("function");
        var history = tools[1].GetProperty("function");

        Assert.Equal("Create a Work task proposal for the user to approve.", requestWork.GetProperty("description").GetString());
        var workProperties = requestWork.GetProperty("parameters").GetProperty("properties");
        Assert.Contains("without adding unrequested operations", workProperties.GetProperty("prompt").GetProperty("description").GetString());
        Assert.Equal("Only the conversation context needed to complete the task.", workProperties.GetProperty("contextSummary").GetProperty("description").GetString());

        Assert.Equal("Search message history in the current Chat conversation, or read a specific message by ID with pagination.", history.GetProperty("description").GetString());
        var historyProperties = history.GetProperty("parameters").GetProperty("properties");
        Assert.Contains("messageId", historyProperties.GetProperty("query").GetProperty("description").GetString());
        Assert.Contains("Character offset", historyProperties.GetProperty("offset").GetProperty("description").GetString());
    }

    [Fact]
    public void ChatSearchIsAnApplicationToolAndNeverEnablesVendorSearch()
    {
        var payload = DashScopeClient.BuildChatTurnPayload([], "test-model", true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = json.RootElement;

        Assert.False(root.TryGetProperty("enable_search", out _));
        Assert.DoesNotContain(root.GetProperty("tools").EnumerateArray(), item =>
            item.GetProperty("function").GetProperty("name").GetString() == "vendor_web_search");
        Assert.Contains(root.GetProperty("tools").EnumerateArray(), item =>
            item.GetProperty("function").GetProperty("name").GetString() == "web_search");
        Assert.False(root.TryGetProperty("search_options", out _));
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public void DisabledChatSearchDoesNotExposeWebSearchTool()
    {
        var payload = DashScopeClient.BuildChatTurnPayload([], "test-model", false);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.DoesNotContain(json.RootElement.GetProperty("tools").EnumerateArray(), item =>
            item.GetProperty("function").GetProperty("name").GetString() == "web_search");
    }
}
