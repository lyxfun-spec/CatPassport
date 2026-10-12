using System.Text;

namespace CatAssistant.Core.Protocol;

public static class DisplayText
{
    public static string Filter(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var n = rune.Value;
            if (n is 0x09 or 0x0A or 0x0D || n is >= 0x20 and <= 0x7E ||
                n is >= 0x3000 and <= 0x303F || n is >= 0x3400 and <= 0x4DBF ||
                n is >= 0x4E00 and <= 0x9FFF || n is >= 0xFF01 and <= 0xFF60)
                result.Append(rune);
        }
        return result.ToString();
    }
}
