using System.Text.RegularExpressions;

namespace CatAssistant.Backend.Services;

public static partial class CitationMarkers
{
    public static string Strip(string value) => value.Contains("[ref_", StringComparison.Ordinal) ? Marker().Replace(value, "") : value;

    [GeneratedRegex(@"\[ref_\d+\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\[(?:r(?:e(?:f(?:_\d{0,6})?)?)?)?\z")]
    internal static partial Regex PartialMarkerAtEnd();
}

public sealed class CitationMarkerStreamFilter
{
    private string _pending = "";

    public string Push(string delta)
    {
        var text = _pending + delta;
        var partial = CitationMarkers.PartialMarkerAtEnd().Match(text);
        _pending = partial.Success ? partial.Value : "";
        return CitationMarkers.Strip(partial.Success ? text[..partial.Index] : text);
    }

    public string Flush()
    {
        var rest = _pending;
        _pending = "";
        return rest;
    }
}
