using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WebSearchRankerTests
{
    [Fact]
    public void WeatherQueryPrefersOfficialSameDayObservationOverPopularSelfMedia()
    {
        var today = new DateOnly(2026, 10, 25);
        var ranked = WebSearchRanker.Rank(
            [
                new WebSearchResult("杭州暴雨实况", "https://weibo.com/post/1", "杭州现在暴雨，转发很多。", "2026-10-25", "微博", RerankScore: 9),
                new WebSearchResult("杭州天气预报", "https://www.weather.com.cn/hangzhou", "杭州未来三天预报。", "2026-10-25", "中国天气网", Authority: 2, RerankScore: 4),
                new WebSearchResult("杭州天气实况", "https://weather.cma.cn/hangzhou", "杭州当前晴，22℃，实况。", "2026-10-25", "中国气象局", Authority: 3, RerankScore: 0.5)
            ],
            "杭州现在天气",
            "杭州现在天气怎么样？",
            today);

        Assert.Equal("杭州天气实况", ranked[0].Result.Title);
        Assert.True(ranked[0].IsOfficial);
        Assert.True(ranked[0].LocationMatch);
        Assert.Equal("observation", ranked[0].WeatherKind);
        Assert.Equal("forecast", ranked[1].WeatherKind);
        Assert.Equal("observation", ranked[2].WeatherKind);
    }

    [Fact]
    public void GeneralQueriesKeepProviderOrderAndPreserveProviderFields()
    {
        var ranked = WebSearchRanker.Rank(
            [
                new WebSearchResult("第一条", "https://example.com/1", "", null, null, Authority: 1, RerankScore: 0.2),
                new WebSearchResult("第二条", "https://example.com/2", "", null, null, Authority: 5, RerankScore: 3)
            ],
            "项目文档",
            null,
            new DateOnly(2026, 10, 25));

        Assert.Equal(new[] { "第一条", "第二条" }, ranked.Select(item => item.Result.Title));
        Assert.Equal(1, ranked[0].Result.Authority);
        Assert.Equal(3, ranked[1].Result.RerankScore);
        Assert.Equal("unknown", ranked[0].WeatherKind);
    }
}
