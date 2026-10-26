using System.Diagnostics;
using System.Text.Json;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed record WebSearchToolExecution(
    string OutputJson,
    IReadOnlyList<CitationRecord> Citations,
    IReadOnlyList<ConversationSourceRecord> NewSources,
    string? Query,
    int QueryCount,
    int RawResultCount,
    int UniqueResultCount,
    long? ElapsedMilliseconds,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Success => ErrorCode is null;
}

/// <summary>
/// Owns search-tool validation, provider invocation, and trusted citation metadata.
/// Model providers only see the normalized JSON result and never choose source URLs.
/// Ordinals come from the conversation-level registry, not from a single tool call.
/// </summary>
public sealed class WebSearchToolExecutor(IWebSearchProvider provider, Func<string> apiKey, Func<bool> enabled)
{
    private static readonly string[] TimeRanges = ["OneDay", "OneWeek", "OneMonth", "OneYear", "NoLimit"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WebSearchToolExecution> ExecuteAsync(
        string argumentsJson,
        ChatSearchSession session,
        CancellationToken cancellationToken,
        string? operationId = null,
        string? toolCallId = null)
    {
        if (!enabled()) return Error("search_disabled", "联网搜索已在设置中关闭。", session, null);
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) return Error("search_not_configured", "尚未配置联网搜索服务 API Key。", session, null);

        Arguments? arguments;
        try { arguments = JsonSerializer.Deserialize<Arguments>(argumentsJson, JsonOptions); }
        catch (JsonException error) { return Error("invalid_arguments", error.Message, session, null); }
        var query = arguments?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return Error("invalid_arguments", "query 不能为空。", session, null);
        if (!session.CanSearch)
            return Error("search_limit_reached", $"本轮已经搜索 {session.QueryCount} 次，达到上限；请基于已有结果回答。", session, query);

        var count = Math.Clamp(arguments!.Count ?? 6, 1, 10);
        var timeRange = TimeRanges.FirstOrDefault(item => string.Equals(item, arguments.TimeRange, StringComparison.OrdinalIgnoreCase));
        session.BeginQuery();
        var stopwatch = Stopwatch.StartNew();

        WebSearchOutcome outcome;
        try
        {
            outcome = await provider.SearchAsync(
                new WebSearchRequest(query, count, timeRange, session.OriginalQuestion), key, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (WebSearchException error)
        {
            return Error(error.Code, error.Message, session, query, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception error)
        {
            return Error("search_failed", $"搜索服务暂时不可用：{error.Message}", session, query, stopwatch.ElapsedMilliseconds);
        }

        var ranked = WebSearchRanker.Rank(outcome.Results, query, session.OriginalQuestion);
        var merge = await session.MergeAsync(ranked, operationId, toolCallId, cancellationToken);
        var citations = merge.NewSources
            .Select(source => new CitationRecord(Guid.NewGuid().ToString("N"), "", source.Ordinal, source.Title, source.Url, source.Snippet, source.SiteName))
            .ToArray();
        var elapsed = outcome.ElapsedMilliseconds ?? stopwatch.ElapsedMilliseconds;
        var weather = WebSearchRanker.IsWeatherQuery(query, session.OriginalQuestion);
        var output = JsonSerializer.Serialize(new
        {
            query,
            originalQuestion = session.OriginalQuestion,
            engine = outcome.EngineType,
            queryCount = session.QueryCount,
            rawResultCount = session.RawResultCount,
            uniqueResultCount = session.UniqueResultCount,
            newResultCount = merge.NewResultCount,
            duplicateResultCount = merge.DuplicateResultCount,
            omittedBySourceLimit = merge.OmittedByLimit,
            sourceCount = session.SourceCount,
            elapsedMs = elapsed,
            count = merge.NewSources.Count,
            results = merge.NewSources.Select(source => new
            {
                sourceId = source.Id,
                ordinal = source.Ordinal,
                citation = $"[ref_{source.Ordinal}]",
                title = source.Title,
                url = source.Url,
                snippet = source.Snippet,
                date = source.PublishedTime,
                publishedTime = source.PublishedTime,
                site = source.SiteName,
                domain = source.Domain,
                authority = source.Authority,
                rerankScore = source.RerankScore,
                official = source.IsOfficial,
                weatherKind = source.WeatherKind
            }),
            note = Note(merge, weather)
        });
        return new WebSearchToolExecution(
            output, citations, merge.NewSources, query, session.QueryCount, session.RawResultCount, session.UniqueResultCount,
            elapsed, null, null);
    }

    private static string? Note(ChatSearchMerge merge, bool weather)
    {
        var notes = new List<string>();
        if (merge.NewSources.Count == 0 && merge.DuplicateResultCount == 0) notes.Add("没有找到结果；可以更换关键词或放宽时间范围后重试。");
        if (merge.NewSources.Count == 0 && merge.DuplicateResultCount > 0) notes.Add("未发现新增资料；重复资料已按规范化 URL 过滤。");
        if (merge.NewSources.Count > 0 && merge.DuplicateResultCount > 0) notes.Add($"已过滤 {merge.DuplicateResultCount} 条历史资料。");
        if (merge.OmittedByLimit > 0) notes.Add($"会话资料数量已达到 {ChatSourceRegistry.MaxConversationSources} 条上限。");
        if (weather) notes.Add("天气问题优先采用官方来源的实况，明确区分实况与预报；不要用多条自媒体结果投票覆盖官方来源。");
        return notes.Count == 0 ? null : string.Join(' ', notes);
    }

    private static WebSearchToolExecution Error(
        string code,
        string message,
        ChatSearchSession session,
        string? query,
        long? elapsedMilliseconds = null) =>
        new(
            JsonSerializer.Serialize(new
            {
                query,
                queryCount = session.QueryCount,
                rawResultCount = session.RawResultCount,
                uniqueResultCount = session.UniqueResultCount,
                sourceCount = session.SourceCount,
                results = Array.Empty<object>(),
                error = code,
                message
            }),
            [], [], query, session.QueryCount, session.RawResultCount, session.UniqueResultCount,
            elapsedMilliseconds, code, message);

    private sealed record Arguments(string Query, int? Count, string? TimeRange);
}
