using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WorkProviderTests
{
    [Fact]
    public void AccumulatesStreamedToolCallFragmentsAcrossChunks()
    {
        var accumulator = new DashScopeClient.WorkStreamAccumulator();
        var chunks = new[]
        {
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"我先"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"读取文件。","tool_calls":[{"index":0,"id":"call-a","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"in"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call-b","type":"function","function":{"name":"list_directory","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"puts/a.txt\"}"}}]},"finish_reason":"tool_calls"}]}"""
        };
        foreach (var chunk in chunks)
        {
            using var json = JsonDocument.Parse(chunk);
            accumulator.Append(json.RootElement);
        }

        var response = accumulator.ToResponse();

        Assert.Equal("我先读取文件。", response.Content);
        Assert.Equal(new[] { "call-a", "call-b" }, response.ToolCalls.Select(call => call.ToolCallId));
        Assert.Equal(new[] { "read_file", "list_directory" }, response.ToolCalls.Select(call => call.Name));
        Assert.Equal("""{"path":"inputs/a.txt"}""", response.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void IgnoresPrivateReasoningAndSurfacesStreamErrors()
    {
        var accumulator = new DashScopeClient.WorkStreamAccumulator();
        using var chunk = JsonDocument.Parse("""{"choices":[{"index":0,"delta":{"reasoning_content":"私密思维链","content":"可见部分"}}]}""");

        Assert.Equal("可见部分", accumulator.Append(chunk.RootElement));
        Assert.Equal("可见部分", accumulator.ToResponse().Content);

        using var error = JsonDocument.Parse("""{"code":"Throttling.RateQuota","message":"Too many requests."}""");
        var throttled = new DashScopeClient.WorkStreamAccumulator();
        Assert.Throws<CloudException>(() => throttled.Append(error.RootElement));
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

        var workspace = Path.Combine(Path.GetTempPath(), "work-provider", conversationId);
        var environment = new WorkEnvironmentInfo(
            workspace,
            Path.Combine(workspace, "inputs"),
            Path.Combine(workspace, "work"),
            Path.Combine(workspace, "outputs"),
            Path.Combine(workspace, "work", "python-packages"),
            @"C:\runtime\python.exe");
        var messages = DashScopeClient.BuildWorkMessages(context, environment);
        var encoded = JsonSerializer.Serialize(messages);
        using var json = JsonDocument.Parse(encoded);
        var items = json.RootElement;

        Assert.Equal("system", items[0].GetProperty("role").GetString());
        Assert.Equal("user", items[1].GetProperty("role").GetString());
        Assert.Equal("call-a", items[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-a", items[3].GetProperty("tool_call_id").GetString());
        var system = items[0].GetProperty("content").GetString();
        Assert.Contains("For longer tasks", system);
        Assert.Contains($"Windows workspace: {workspace}", system);
        Assert.Contains("Python: available.", system);
        Assert.Contains("place the verified files in outputs and call deliver_files", system);
        Assert.DoesNotContain("history has been compacted", system);
        Assert.DoesNotContain(@"C:\runtime\python.exe", system);
        Assert.DoesNotContain("PIP_TARGET", system);
        Assert.DoesNotContain("shell=\"cmd\"", system);
    }

    [Fact]
    public void AddsHistoryLocationsOnlyWhenWorkContextWasCompacted()
    {
        var conversationId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var context = new WorkContextItem[]
        {
            new("summary", conversationId, null, 1, "message", "system", null, null, "context_summary", "{\"content\":\"Earlier work summary\"}", now),
            new("message", conversationId, "run", 2, "message", "user", "message", null, null, "{\"content\":\"Continue\"}", now)
        };
        var workspace = Path.Combine(Path.GetTempPath(), "work-provider", conversationId);
        var environment = new WorkEnvironmentInfo(
            workspace,
            Path.Combine(workspace, "inputs"),
            Path.Combine(workspace, "work"),
            Path.Combine(workspace, "outputs"),
            Path.Combine(workspace, "work", "python-packages"),
            null);

        var messages = DashScopeClient.BuildWorkMessages(context, environment);
        var encoded = JsonSerializer.Serialize(messages);
        using var json = JsonDocument.Parse(encoded);
        var system = json.RootElement[0].GetProperty("content").GetString();

        Assert.Contains("history has been compacted", system);
        Assert.Contains("work/.context/history.jsonl", system);
        Assert.Contains("work/.context/search_history.py", system);
        Assert.Contains("Python: unavailable.", system);
    }
}
