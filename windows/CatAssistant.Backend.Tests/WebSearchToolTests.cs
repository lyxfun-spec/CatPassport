using System.Net;
using System.Text.Json;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WebSearchToolTests
{
    private static WorkToolExecutionContext Context() => new(Guid.NewGuid().ToString("N"), "run", Path.GetTempPath(), "call");

    [Fact]
    public async Task ReturnsRankedResultsFromIndependentSearchService()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {"requestId":"r1","pageItems":[
              {"title":"今日天气","link":"https://weather.example.com/a","snippet":"晴 26 度","publishedTime":"2026-10-22T08:00:00+08:00","hostname":"天气网"},
              {"title":"重复","link":"https://weather.example.com/a","snippet":"重复条目"},
              {"title":"脚本链接","link":"javascript:alert(1)","snippet":"非法协议"}
            ],"searchInformation":{"searchTime":412}}
            """));
        using var client = new IqsWebSearchClient(new HttpClient(handler));
        var tool = new WebSearchTool(client, () => "iqs-key", () => true);

        var result = await tool.ExecuteAsync(Context(), "{\"query\":\"北京天气\",\"count\":4,\"time_range\":\"OneDay\"}", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Bearer iqs-key", handler.Request!.Headers.Authorization?.ToString());
        Assert.Equal("https://cloud-iqs.aliyuncs.com/search/unified", handler.Request.RequestUri!.ToString());
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("北京天气", sent.RootElement.GetProperty("query").GetString());
        Assert.Equal("CNAuto", sent.RootElement.GetProperty("engineType").GetString());
        Assert.Equal("OneDay", sent.RootElement.GetProperty("timeRange").GetString());
        Assert.Equal(4, sent.RootElement.GetProperty("advancedParams").GetProperty("numResults").GetInt32());
        Assert.False(sent.RootElement.GetProperty("contents").GetProperty("mainText").GetBoolean());

        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("CNAuto", json.RootElement.GetProperty("engine").GetString());
        var item = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("https://weather.example.com/a", item.GetProperty("url").GetString());
        Assert.Equal("天气网", item.GetProperty("site").GetString());
    }

    [Fact]
    public async Task ReportsMissingKeyWithoutCallingTheService()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));
        using var client = new IqsWebSearchClient(new HttpClient(handler));
        var tool = new WebSearchTool(client, () => "", () => true);

        var result = await tool.ExecuteAsync(Context(), "{\"query\":\"任意\"}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("search_not_configured", result.ErrorCode);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ReportsDisabledSearchWithoutCallingTheService()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));
        using var client = new IqsWebSearchClient(new HttpClient(handler));
        var tool = new WebSearchTool(client, () => "iqs-key", () => false);

        var result = await tool.ExecuteAsync(Context(), "{\"query\":\"任意\"}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("search_disabled", result.ErrorCode);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task SurfacesUpstreamErrorCodeAndMessage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""
                {"code":"InvalidParameter","message":"engineType is not supported","requestId":"r2"}
                """, System.Text.Encoding.UTF8, "application/json")
        });
        using var client = new IqsWebSearchClient(new HttpClient(handler));
        var tool = new WebSearchTool(client, () => "iqs-key", () => true);

        var result = await tool.ExecuteAsync(Context(), "{\"query\":\"任意\"}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("search_failed", result.ErrorCode);
        Assert.Contains("InvalidParameter", result.ErrorMessage);
        Assert.Contains("engineType is not supported", result.ErrorMessage);
    }

    [Fact]
    public async Task ReturnsEmptyResultSetAsSuccess()
    {
        var handler = new StubHandler(_ => JsonResponse("""{"pageItems":[]}"""));
        using var client = new IqsWebSearchClient(new HttpClient(handler));
        var tool = new WebSearchTool(client, () => "iqs-key", () => true);

        var result = await tool.ExecuteAsync(Context(), "{\"query\":\"不存在的内容\"}", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("note").GetString()!);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
