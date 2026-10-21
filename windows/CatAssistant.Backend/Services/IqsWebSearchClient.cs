using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatAssistant.Backend.Services;

public sealed record WebSearchResult(string Title, string Url, string Snippet, string? PublishedTime, string? SiteName);

public sealed record WebSearchOutcome(IReadOnlyList<WebSearchResult> Results, string EngineType, long? ElapsedMilliseconds);

public sealed class WebSearchException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class IqsWebSearchClient : IDisposable
{
    private const string Endpoint = "https://cloud-iqs.aliyuncs.com/search/unified";
    private const string EngineType = "CNAuto";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _http;

    public IqsWebSearchClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = RequestTimeout };

    public async Task<WebSearchOutcome> SearchAsync(string query, int count, string? timeRange, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            query,
            engineType = EngineType,
            timeRange,
            contents = new { mainText = false, markdownText = false, summary = false, rerankScore = true },
            advancedParams = new { numResults = count }
        }, options: JsonOptions);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSearchException("search_timeout", $"搜索服务在 {RequestTimeout.TotalSeconds:0} 秒内没有返回结果。", error);
        }
        catch (HttpRequestException error)
        {
            throw new WebSearchException("search_failed", $"无法连接搜索服务：{error.Message}", error);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
            return Parse(body);
        }
    }

    private static WebSearchOutcome Parse(string body)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException error) { throw new WebSearchException("search_failed", "搜索服务返回了无法解析的响应。", error); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGet(root, "pageItems", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new WebSearchException("search_failed", $"搜索服务返回了无法识别的响应：{Excerpt(body)}");

            var results = new List<WebSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var url = ReadString(item, "link");
                if (!IsSafeWebUrl(url) || !seen.Add(url!)) continue;
                var title = ReadString(item, "title") is { Length: > 0 } text ? text : new Uri(url!).Host;
                results.Add(new WebSearchResult(title, url!, ReadString(item, "snippet") ?? "", ReadString(item, "publishedTime"), ReadString(item, "hostname")));
            }

            long? elapsed = null;
            if (TryGet(root, "searchInformation", out var information) && information.ValueKind == JsonValueKind.Object &&
                TryGet(information, "searchTime", out var time) && time.TryGetInt64(out var milliseconds)) elapsed = milliseconds;
            return new WebSearchOutcome(results, EngineType, elapsed);
        }
    }

    private static WebSearchException MapError(HttpStatusCode statusCode, string body)
    {
        var detail = ReadString(body, "message") ?? ReadString(body, "Message") ?? ReadString(body, "msg");
        var code = ReadString(body, "code") ?? ReadString(body, "Code") ?? ((int)statusCode).ToString();
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "搜索服务 API Key 无效。",
            HttpStatusCode.Forbidden => "搜索服务 API Key 无权调用联网搜索。",
            HttpStatusCode.TooManyRequests => "搜索服务请求过于频繁。",
            _ => $"搜索服务返回 {code}：{detail ?? Excerpt(body)}"
        };
        return new WebSearchException("search_failed", message);
    }

    internal static bool IsSafeWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadString(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, name);
        }
        catch (JsonException) { return null; }
    }

    private static string Excerpt(string body) => body.Length <= 300 ? body : body[..300] + "…";

    public void Dispose() => _http.Dispose();
}
