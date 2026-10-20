using System.Text;
using System.Text.RegularExpressions;

namespace CatAssistant.Core.Protocol;

/// <summary>
/// 卡片是 8 行小屏、只认纯文本，markdown 的标记符号到了屏幕上就是杂点。
/// 桌面端与数据库继续保留原始 markdown，这里只处理发往卡片的那一份副本。
/// 遮蔽只删分隔符、不改正文，模型说出来的字一个都不会变。
/// </summary>
public static partial class CardMarkdown
{
    [GeneratedRegex(@"^[ \t]*(?:`{3,}|~{3,})")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^[ \t]*>[ \t]?")]
    private static partial Regex QuoteMarker();

    [GeneratedRegex(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$")]
    private static partial Regex ThematicBreak();

    // * / + 项目符号换成 -，卡片字库里有 -，但不能再出现星号。缩进与有序列表原样保留。
    [GeneratedRegex(@"^([ \t]*)(?:[*+])[ \t]+")]
    private static partial Regex AsteriskBullet();

    [GeneratedRegex(@"(?:!\[(?<alt>[^\]]*)\]\([^)]*\))|(?:\[(?<text>[^\]]*)\]\([^)]*\))|(?:\[(?<ref>[^\]]*)\]\[[^\]]*\])")]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!>])")]
    private static partial Regex Escape();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    /// <summary>整段遮蔽，用于非流式的一次性文本。</summary>
    public static string Mask(string value)
    {
        var inFence = false;
        return MaskLines(value, ref inFence);
    }

    internal static string MaskLines(string text, ref bool inFence)
    {
        if (text.Length == 0) return text;
        var kept = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var carriage = raw.Length > 0 && raw[^1] == '\r' ? "\r" : "";
            var line = carriage.Length > 0 ? raw[..^1] : raw;
            if (FenceLine().IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }
            // 代码块内部原样保留，否则 `a**b` 这类字面量会被当成强调符号破坏掉。
            kept.Add((inFence ? line : MaskLine(line)) + carriage);
        }
        return BlankRuns().Replace(string.Join('\n', kept), "\n\n");
    }

    private static string MaskLine(string line)
    {
        if (line.Length == 0) return line;
        if (ThematicBreak().IsMatch(line)) return "";
        var value = HeadingMarker().Replace(line, "");
        value = QuoteMarker().Replace(value, "");
        value = AsteriskBullet().Replace(value, "$1- ");
        value = Link().Replace(value, m =>
            m.Groups["alt"].Success ? m.Groups["alt"].Value
                : m.Groups["text"].Success ? m.Groups["text"].Value
                : m.Groups["ref"].Value);
        value = HtmlTag().Replace(value, "");
        value = Escape().Replace(value, "$1");
        return Inline(value);
    }

    /// <summary>
    /// 去掉强调与行内代码的分隔符，保留其中的文字。两处刻意手下留情：数字之间的单个
    /// <c>*</c>（2*3 是算式不是斜体）和单词内部的单个 <c>_</c>（user_name 是标识符）。
    /// </summary>
    private static string Inline(string value)
    {
        if (value.IndexOfAny(['`', '*', '_']) < 0) return value;
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            var marker = value[i];
            if (marker is not ('`' or '*' or '_'))
            {
                result.Append(value[i++]);
                continue;
            }
            var end = i;
            while (end < value.Length && value[end] == marker) end++;
            var run = end - i;

            if (marker == '`')
            {
                var close = value.IndexOf(new string('`', run), end, StringComparison.Ordinal);
                if (close >= 0)
                {
                    result.Append(value, end, close - end);
                    i = close + run;
                    continue;
                }
                i = end;
                continue;
            }
            if (run == 1 && marker == '_')
            {
                result.Append('_');
                i = end;
                continue;
            }
            if (run == 1 && marker == '*' && i > 0 && end < value.Length &&
                IsWordChar(value[i - 1]) && IsWordChar(value[end]))
            {
                result.Append('*');
                i = end;
                continue;
            }
            i = end;
        }
        return result.ToString();
    }

    private static bool IsWordChar(char value) => char.IsLetterOrDigit(value);
}

/// <summary>
/// 流式遮蔽。标题、列表、分隔线、围栏这些行级结构必须看到整行才判得准，所以这里只放行
/// 已经闭合的行，没收尾的最后一行留在缓冲里等下一个 delta 或 Flush。代价是卡片上的文字
/// 按行/段落出现而不是逐字出现，换来的是永不泄漏半个标记符号。
/// </summary>
public sealed class CardMarkdownStream
{
    private readonly StringBuilder _pending = new();
    private bool _inFence;

    public string Push(string delta)
    {
        if (delta.Length == 0) return "";
        _pending.Append(delta);
        var buffer = _pending.ToString();
        var lastBreak = buffer.LastIndexOf('\n');
        if (lastBreak < 0) return "";
        var ready = buffer[..(lastBreak + 1)];
        _pending.Remove(0, lastBreak + 1);
        return CardMarkdown.MaskLines(ready, ref _inFence);
    }

    public string Flush()
    {
        if (_pending.Length == 0) return "";
        var rest = _pending.ToString();
        _pending.Clear();
        return CardMarkdown.MaskLines(rest, ref _inFence);
    }
}
