using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WorkProviderTests
{
    [Fact]
    public void ParsesNativeToolCallsAndKeepsProviderIds()
    {
        using var json = JsonDocument.Parse("""
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": "我先读取文件。",
                  "tool_calls": [
                    {"id":"call-a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"inputs/a.txt\"}"}},
                    {"id":"call-b","type":"function","function":{"name":"list_directory","arguments":"{\"path\":\"inputs\"}"}}
                  ]
                }
              }]
            }
            """);

        var response = DashScopeClient.ParseWorkResponse(json.RootElement);

        Assert.Equal("我先读取文件。", response.Content);
        Assert.Equal(new[] { "call-a", "call-b" }, response.ToolCalls.Select(call => call.ToolCallId));
        Assert.Equal(new[] { "read_file", "list_directory" }, response.ToolCalls.Select(call => call.Name));
    }

    [Fact]
    public void RebuildsAssistantToolCallAndToolResultMessages()
    {
        var conversationId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var context = new WorkContextItem[]
        {
            new("1", conversationId, "run", 1, "message", "user", "message", null, null, "{\"content\":\"读取资料\"}", now),
            new("2", conversationId, "run", 2, "tool_call", "assistant", null, null, "assistant_tool_calls", "{\"content\":null,\"toolCalls\":[{\"ToolCallId\":\"call-a\",\"Name\":\"read_file\",\"ArgumentsJson\":\"{\\\"path\\\":\\\"inputs/a.txt\\\"}\"}]}", now),
            new("3", conversationId, "run", 3, "tool_result", "tool", null, "call-a", "read_file", "{\"text\":\"内容\"}", now)
        };

        var messages = DashScopeClient.BuildWorkMessages(context);
        var encoded = JsonSerializer.Serialize(messages);
        using var json = JsonDocument.Parse(encoded);
        var items = json.RootElement;

        Assert.Equal("system", items[0].GetProperty("role").GetString());
        Assert.Equal("user", items[1].GetProperty("role").GetString());
        Assert.Equal("call-a", items[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-a", items[3].GetProperty("tool_call_id").GetString());
    }
}
