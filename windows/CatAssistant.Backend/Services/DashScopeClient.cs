using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed class DashScopeClient : IDisposable
{
    private const string AsrUrl = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
    private const string ChatUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<string> TranscribeAsync(byte[] wav, string model, string apiKey, CancellationToken cancellationToken)
    {
        EnsureKey(apiKey);
        using var request = CreateRequest(AsrUrl, apiKey);
        request.Headers.Add("X-DashScope-SSE", "disable");
        request.Content = JsonContent.Create(new
        {
            model,
            input = new { messages = new[] { new { role = "user", content = new[] { new { type = "input_audio", input_audio = new { data = "data:audio/wav;base64," + Convert.ToBase64String(wav) } } } } } },
            parameters = new { format = "wav", sample_rate = "16000" }
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (TryText(json.RootElement, out var text)) return text;
        throw new CloudException(CloudError.Service, "语音识别未返回文字。");
    }

    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<ChatMessage> history, string model, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureKey(apiKey);
        var messages = new List<object> { new { role = "system", content = "你是小猫助手，可爱、温暖、简洁，但不要声称自己是真实宠物。请使用简体中文纯文本回答，不使用 Markdown、Emoji、颜文字或生僻特殊符号。标点只使用常见中文标点或 ASCII 字符。" } };
        messages.AddRange(history.Select(message => (object)new { role = message.Role, content = message.Content }));
        using var request = CreateRequest(ChatUrl, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(new { model, messages, stream = true, stream_options = new { include_usage = true } });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
            {
                var value = content.GetString();
                if (!string.IsNullOrEmpty(value)) yield return value;
            }
        }
    }

    private static HttpRequestMessage CreateRequest(string uri, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static void EnsureKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new CloudException(CloudError.ApiKeyMissing, "尚未配置 DashScope API Key。");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => CloudError.ApiKeyInvalid,
            HttpStatusCode.Forbidden => CloudError.Permission,
            HttpStatusCode.NotFound => CloudError.Model,
            _ => CloudError.Service
        };
        var message = kind switch
        {
            CloudError.ApiKeyInvalid => "DashScope API Key 无效。",
            CloudError.Permission => "DashScope API Key 无权访问所选模型。",
            CloudError.Model => "配置的 DashScope 模型不存在。",
            _ => $"DashScope 服务返回 HTTP {(int)response.StatusCode}。"
        };
        throw new CloudException(kind, message);
    }

    private static bool TryText(JsonElement root, out string text)
    {
        text = "";
        if (!root.TryGetProperty("output", out var output)) return false;
        if (output.TryGetProperty("text", out var direct) && !string.IsNullOrWhiteSpace(direct.GetString())) { text = direct.GetString()!; return true; }
        if (output.TryGetProperty("output", out var nested) && nested.TryGetProperty("sentence", out var sentence) && sentence.TryGetProperty("text", out var sentenceText)) { text = sentenceText.GetString() ?? ""; return text.Length > 0; }
        if (output.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)) { text = content.GetString() ?? ""; return text.Length > 0; }
        return false;
    }

    public void Dispose() => _http.Dispose();
}

public enum CloudError { ApiKeyMissing, ApiKeyInvalid, Permission, Model, Network, Timeout, Service }
public sealed class CloudException(CloudError kind, string message) : Exception(message) { public CloudError Kind { get; } = kind; }
