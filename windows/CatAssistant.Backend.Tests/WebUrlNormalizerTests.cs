using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WebUrlNormalizerTests
{
    [Theory]
    [InlineData("https://Example.COM/a/?utm_source=x&utm_medium=y#frag", "https://example.com/a")]
    [InlineData("http://example.com:80/a/?fbclid=1&keep=2", "http://example.com/a?keep=2")]
    [InlineData("https://example.com:443/a/", "https://example.com/a")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com/a?spm=1&ref=share", "https://example.com/a")]
    public void NormalizesHostPortsFragmentsTrackingAndTrailingSlashes(string input, string expected)
    {
        Assert.Equal(expected, WebUrlNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a")]
    [InlineData("not a url")]
    public void RejectsNonWebUrls(string input)
    {
        Assert.Null(WebUrlNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("https://Example.COM/a", "example.com")]
    [InlineData("https://weather.cma.cn/a", "weather.cma.cn")]
    public void ExtractsTheNormalizedHostAsDomain(string input, string expected)
    {
        Assert.Equal(expected, WebUrlNormalizer.DomainOf(input));
    }
}
