using System.Globalization;
using System.Text.RegularExpressions;

namespace CatAssistant.Backend.Services;

public sealed record RankedWebSearchResult(
    WebSearchResult Result,
    bool IsOfficial,
    string WeatherKind,
    bool LocationMatch,
    double Score);

/// <summary>
/// Provider-neutral presentation ranking. Weather questions prefer official, same-day and
/// location-accurate sources instead of counting how many self-media results agree.
/// </summary>
public static class WebSearchRanker
{
    private static readonly string[] WeatherTerms =
        ["天气", "气温", "温度", "降雨", "降水", "下雨", "风速", "风力", "台风", "空气质量", "湿度", "weather", "forecast"];

    private static readonly string[] ObservationTerms =
        ["实况", "现在", "当前", "此刻", "实时", "观测", "监测", "now", "current", "currently", "observation"];

    private static readonly string[] ForecastTerms =
        ["预报", "预计", "未来", "明天", "后天", "一周", "趋势", "周报", "forecast", "tomorrow", "outlook"];

    private static readonly string[] ObservationIntentTerms =
        ["实况", "现在", "当前", "此刻", "实时", "此刻", "现在几点", "now", "current", "currently"];

    private static readonly string[] ForecastIntentTerms =
        ["预报", "预计", "未来", "明天", "后天", "一周", "趋势", "forecast", "tomorrow"];

    private static readonly string[] LocationStopTerms =
    [
        "今天", "今日", "明天", "明日", "后天", "现在", "当前", "实时", "实况", "未来", "一周", "本周", "周末",
        "天气", "气温", "温度", "降雨", "降水", "下雨", "风力", "风速", "空气", "质量", "湿度", "台风",
        "预报", "预计", "如何", "怎么样", "多少", "查询", "一下", "情况", "weather", "today", "tomorrow",
        "forecast", "current", "now", "temperature"
    ];

    private static readonly string[] OfficialWeatherHosts =
        ["cma.gov.cn", "weather.com.cn", "nmc.cn", "cma.cn"];

    private static readonly Regex TokenPattern = new(@"[\p{IsCJKUnifiedIdeographs}]{2,}|[A-Za-z]{2,}", RegexOptions.Compiled);

    public static bool IsWeatherQuery(string query, string? originalQuestion = null)
    {
        var combined = $"{originalQuestion} {query}";
        return ContainsAny(combined, WeatherTerms);
    }

    public static IReadOnlyList<RankedWebSearchResult> Rank(
        IEnumerable<WebSearchResult> results,
        string query,
        string? originalQuestion = null,
        DateOnly? today = null)
    {
        var weather = IsWeatherQuery(query, originalQuestion);
        var combined = $"{originalQuestion} {query}";
        var tokens = LocationTokens(combined);
        var prefersObservation = ContainsAny(combined, ObservationIntentTerms);
        var prefersForecast = ContainsAny(combined, ForecastIntentTerms);
        var localToday = today ?? DateOnly.FromDateTime(DateTime.Now);

        var ranked = results.Select((result, index) =>
        {
            var text = $"{result.Title} {result.Snippet} {result.SiteName}";
            var official = weather && IsOfficialWeatherHost(result.Url);
            var locationMatch = weather && tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
            var kind = ClassifyWeatherKind(text);
            var score = 0d;
            if (official) score += 80;
            if (locationMatch) score += 30;
            score += RecencyScore(result.PublishedTime, localToday);
            if (prefersObservation && kind is "observation" or "mixed") score += 20;
            if (prefersForecast && kind is "forecast" or "mixed") score += 20;
            score += Math.Clamp(result.Authority ?? 0, 0, 10) * 2;
            score += result.RerankScore ?? 0;
            return (Item: new RankedWebSearchResult(result, official, weather ? kind : "unknown", locationMatch, score), Index: index);
        }).ToArray();

        if (!weather) return ranked.Select(item => item.Item).ToArray();
        return ranked
            .OrderByDescending(item => item.Item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Item)
            .ToArray();
    }

    private static double RecencyScore(string? publishedTime, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(publishedTime)) return 0;
        if (!DateTimeOffset.TryParse(publishedTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) return 0;
        var date = DateOnly.FromDateTime(parsed.LocalDateTime);
        var age = today.DayNumber - date.DayNumber;
        return age switch
        {
            0 => 25,
            > 0 and <= 2 => 8,
            > 0 and <= 7 => 3,
            _ => 0
        };
    }

    private static bool IsOfficialWeatherHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return OfficialWeatherHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private static string ClassifyWeatherKind(string text)
    {
        var observation = ContainsAny(text, ObservationTerms);
        var forecast = ContainsAny(text, ForecastTerms);
        return observation && forecast ? "mixed" : observation ? "observation" : forecast ? "forecast" : "unknown";
    }

    private static IReadOnlyList<string> LocationTokens(string text)
    {
        foreach (var term in LocationStopTerms)
            text = text.Replace(term, " ", StringComparison.OrdinalIgnoreCase);

        var tokens = new List<string>();
        foreach (Match match in TokenPattern.Matches(text))
        {
            var token = match.Value;
            if (token.Length >= 2) tokens.Add(token);
            if (!IsCjk(token) || token.Length <= 2) continue;
            var trimmed = token.TrimEnd('市', '区', '县');
            if (trimmed.Length >= 2) tokens.Add(trimmed);
            tokens.Add(token[..2]);
        }
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsCjk(string value) => value.Length > 0 && value[0] >= '\u4e00' && value[0] <= '\u9fff';

    private static bool ContainsAny(string text, IEnumerable<string> terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}
