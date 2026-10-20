using CatAssistant.Core.Protocol;
using Xunit;

namespace CatAssistant.Core.Tests;

public class CardMarkdownTests
{
    [Fact]
    public void DropsMarkersButKeepsEverySpokenWord()
    {
        var masked = CardMarkdown.Mask(
            "## 结果\n- **甲**：`ok`\n* 乙\n1. 丙\n> 引用\n详见[官网](https://example.com)\n");

        Assert.Equal("结果\n- 甲：ok\n- 乙\n1. 丙\n引用\n详见官网\n", masked);
    }

    [Fact]
    public void KeepsFencedCodeLiteralsIntact()
    {
        var masked = CardMarkdown.Mask("```c\nint a = 1; // b**c\n```\n后面\n");

        Assert.Equal("int a = 1; // b**c\n后面\n", masked);
    }

    [Fact]
    public void KeepsMultiplicationAndSnakeCaseBecauseTheyAreNotMarkup()
    {
        Assert.Equal("计算 2*3 等于 6，字段 user_name 保留",
            CardMarkdown.Mask("计算 2*3 等于 6，字段 user_name 保留"));
    }

    [Fact]
    public void DropsThematicBreaksAndHtmlButKeepsParagraphSeparation()
    {
        Assert.Equal("甲\n\n乙丙", CardMarkdown.Mask("甲\n---\n乙<br>丙"));
    }

    [Fact]
    public void CollapsesRunsOfBlankLines()
    {
        Assert.Equal("甲\n\n乙", CardMarkdown.Mask("甲\n\n\n\n乙"));
    }

    [Fact]
    public void StreamHoldsBackTheUnterminatedLine()
    {
        var stream = new CardMarkdownStream();

        Assert.Equal("", stream.Push("**重点"));
        Assert.Equal("重点\n", stream.Push("**\n后续"));
        Assert.Equal("后续", stream.Flush());
    }

    [Fact]
    public void StreamKeepsFenceStateAcrossDeltas()
    {
        var stream = new CardMarkdownStream();

        Assert.Equal("a**b\n", stream.Push("```\na**b\n"));
        Assert.Equal("结尾\n", stream.Push("```\n结尾\n"));
    }

    [Fact]
    public void MaskIsIdempotentOnPlainText()
    {
        const string plain = "今天天气不错，我们去公园散步。\n第二行普通文字。";

        Assert.Equal(plain, CardMarkdown.Mask(plain));
    }
}
