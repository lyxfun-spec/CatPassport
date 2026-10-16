using System.Text.Json;
using CatAssistant.Backend.Ipc;
using CatAssistant.Backend.Models;
using Xunit;

namespace CatAssistant.Backend.Tests;

public class RpcServerTests
{
    [Fact]
    public async Task ReturnsVersionedResponseWithoutLeakingASecretField()
    {
        var input = new StringReader("{\"version\":1,\"id\":4,\"method\":\"state.get\"}\n");
        var output = new StringWriter();
        var snapshot = new BackendSnapshot("ok", "ready", false, false, null, 0, [], new PublicSettings("asr", "chat", 60, true, true), null, []);
        await using var server = new RpcServer(input, output, _ => Task.FromResult<object?>(snapshot));

        await server.RunAsync();

        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("response", response.RootElement.GetProperty("kind").GetString());
        Assert.Equal(4, response.RootElement.GetProperty("id").GetInt64());
        var settings = response.RootElement.GetProperty("result").GetProperty("settings");
        Assert.True(settings.GetProperty("hasApiKey").GetBoolean());
        Assert.False(settings.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task RejectsUnsupportedProtocolVersion()
    {
        var output = new StringWriter();
        await using var server = new RpcServer(new StringReader("{\"version\":9,\"id\":8,\"method\":\"state.get\"}\n"), output, _ => Task.FromResult<object?>(null));
        await server.RunAsync();
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("unsupported_version", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
