using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services.WorkTools;

public sealed class WebSearchTool(IqsWebSearchClient client, Func<string> apiKey, Func<bool> enabled) : IWorkTool
{
    private static readonly string[] TimeRanges = ["OneDay", "OneWeek", "OneMonth", "OneYear", "NoLimit"];

    public WorkToolDefinition Definition { get; } = new(
        "web_search",
        "Search the public web through an independent search API. Returns ranked results with title, url, snippet, site and publish time. Use it for current facts, prices, releases or anything after your training cutoff, then cite the urls you rely on.",
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"检索问题或关键词，控制在 30 字以内效果最好。\"},\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10,\"description\":\"需要的搜索结果条数，默认 6。\"},\"time_range\":{\"type\":\"string\",\"enum\":[\"OneDay\",\"OneWeek\",\"OneMonth\",\"OneYear\",\"NoLimit\"],\"description\":\"发布时间范围，默认 NoLimit。\"}},\"required\":[\"query\"],\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        if (string.IsNullOrWhiteSpace(arguments.Query)) return WorkToolResult.Error("invalid_arguments", "query 不能为空。");
        if (!enabled()) return WorkToolResult.Error("search_disabled", "联网搜索已在设置中关闭。");
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) return WorkToolResult.Error("search_not_configured", "尚未在设置中配置联网搜索 API Key。");
        var count = Math.Clamp(arguments.Count ?? 6, 1, 10);
        var timeRange = TimeRanges.FirstOrDefault(item => string.Equals(item, arguments.TimeRange, StringComparison.OrdinalIgnoreCase));

        WebSearchOutcome outcome;
        try
        {
            outcome = await client.SearchAsync(arguments.Query.Trim(), count, timeRange, key, cancellationToken);
        }
        catch (WebSearchException error)
        {
            return WorkToolResult.Error(error.Code, error.Message);
        }

        return WorkToolResult.Ok(new
        {
            query = arguments.Query.Trim(),
            engine = outcome.EngineType,
            count = outcome.Results.Count,
            elapsedMs = outcome.ElapsedMilliseconds,
            results = outcome.Results.Select(item => new
            {
                title = item.Title,
                url = item.Url,
                snippet = item.Snippet,
                publishedTime = item.PublishedTime,
                site = item.SiteName
            }).ToArray(),
            note = outcome.Results.Count == 0 ? "没有找到结果；可以更换关键词或放宽时间范围后重试。" : null
        });
    }

    private sealed record Arguments(string Query, int? Count, string? TimeRange);
}
