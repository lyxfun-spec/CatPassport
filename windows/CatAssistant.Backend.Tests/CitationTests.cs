using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class CitationTests
{
    [Fact]
    public void NativeStreamChunkYieldsSourcesByProviderIndexAndContent()
    {
        const string data = """
            {"output":{"choices":[{"message":{"role":"assistant","content":"降温[ref_3]"},"finish_reason":"null"}],
             "search_info":{"search_results":[
               {"index":3,"title":"三号","url":"https://example.com/3","site_name":"新浪网"},
               {"index":1,"title":"","url":"https://news.example.org/1"},
               {"index":2,"title":"脚本","url":"javascript:alert(1)"},
               {"title":"缺少编号","url":"https://example.com/x"}]}},
             "request_id":"r"}
            """;

        var chunks = DashScopeClient.ParseStreamData(data);

        var citations = Assert.Single(chunks, chunk => chunk.Citations is not null).Citations!;
        Assert.Equal(new[] { 3, 1 }, citations.Select(item => item.Ordinal));
        Assert.Equal("新浪网", citations[0].SourceName);
        Assert.Equal("news.example.org", citations[1].Title);
        Assert.Equal("降温[ref_3]", Assert.Single(chunks, chunk => chunk.Delta is not null).Delta);
    }

    [Fact]
    public void NativeStreamIgnoresEmptyContentAndReportsErrors()
    {
        Assert.Empty(DashScopeClient.ParseStreamData("""{"output":{"choices":[{"message":{"role":"assistant","content":""},"finish_reason":"stop"}]}}"""));
        var error = Assert.Throws<CloudException>(() => DashScopeClient.ParseStreamData("""{"code":"InternalError","message":"boom","request_id":"r"}"""));
        Assert.Equal(CloudError.Service, error.Kind);
    }

    [Fact]
    public void StripRemovesOnlyCompleteMarkers()
    {
        Assert.Equal("降温。[ref_x]", CitationMarkers.Strip("降温[ref_1][ref_12]。[ref_x]"));
    }

    [Fact]
    public void StreamFilterHandlesMarkersSplitAcrossChunks()
    {
        var filter = new CitationMarkerStreamFilter();
        var output = string.Concat(new[] { "降温[", "re", "f_1", "2][ref_", "3]。数组a[", "0] 结束[r" }.Select(filter.Push)) + filter.Flush();

        Assert.Equal("降温。数组a[0] 结束[r", output);
    }

    [Fact]
    public void StreamFilterDoesNotSwallowNewlines()
    {
        var filter = new CitationMarkerStreamFilter();

        Assert.Equal("第一行\n", filter.Push("第一行\n"));
        Assert.Equal("", filter.Flush());
    }
}
