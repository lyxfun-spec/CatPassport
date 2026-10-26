using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CatAssistant.Backend.Services;

/// <summary>DeepSeek Search API adapter for the provider-neutral search boundary.</summary>
public sealed class DeepSeekWebSearchClient : IWebSearchProvider, IDisposable
{
    private const string Endpoint = "https://api.deepseek.com/v1/tools/search";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient _http;

    public DeepSeekWebSearchClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };

    public async Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, string apiKey, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            query = request.Query,
            ori_question = string.IsNullOrWhiteSpace(request.OriginalQuestion) ? request.Query : request.OriginalQuestion,
            count = Math.Clamp(request.Count, 1, 10),
            need_content = false
        });

        HttpResponseMessage response;
        try { response = await _http.SendAsync(message, cancellationToken); }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSearchException("search_timeout", $"DeepSeek 搜索服务在 {RequestTimeout.TotalSeconds:0} 秒内没有返回结果。", error);
        }
        catch (HttpRequestException error)
        {
            throw new WebSearchException("search_failed", $"无法连接 DeepSeek 搜索服务：{error.Message}", error);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
            return Parse(body);
        }
    }

    internal static WebSearchOutcome Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new WebSearchException("search_failed", "DeepSeek 搜索服务返回了无法识别的响应。");
            var results = new List<WebSearchResult>();
            foreach (var item in items.EnumerateArray())
            {
                var url = ReadString(item, "url");
                if (!WebSearchSafety.IsSafeUrl(url)) continue;
                var title = ReadString(item, "title");
                results.Add(new WebSearchResult(
                    string.IsNullOrWhiteSpace(title) ? new Uri(url!).Host : title,
                    url!,
                    ReadString(item, "summary") ?? ReadString(item, "snippet") ?? "",
                    ReadString(item, "date"),
                    ReadString(item, "site_name"),
                    ReadDouble(item, "authority"),
                    ReadDouble(item, "rerank_score")));
            }
            return new WebSearchOutcome(results, "DeepSeek", null);
        }
        catch (JsonException error)
        {
            throw new WebSearchException("search_failed", "DeepSeek 搜索服务返回了无法解析的响应。", error);
        }
    }

    private static WebSearchException MapError(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                detail = ReadString(error, "message");
        }
        catch (JsonException) { }
        var message = status switch
        {
            HttpStatusCode.PaymentRequired => "DeepSeek 搜索账户余额不足。",
            HttpStatusCode.Unauthorized => "DeepSeek API Key 无效。",
            HttpStatusCode.Forbidden => "DeepSeek API Key 无权调用搜索服务。",
            HttpStatusCode.TooManyRequests => "DeepSeek 搜索请求过于频繁。",
            _ => $"DeepSeek 搜索服务返回 HTTP {(int)status}：{detail ?? "请求失败"}"
        };
        return new WebSearchException("search_failed", message);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static double? ReadDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) return number;
        return property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out number) ? number : null;
    }

    public void Dispose() => _http.Dispose();
}
