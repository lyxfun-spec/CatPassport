namespace CatAssistant.Backend.Services;

/// <summary>
/// Canonical URL key for the conversation-level source registry. Host casing, default ports,
/// fragments, trailing slashes and common campaign parameters must not create duplicate sources.
/// </summary>
public static class WebUrlNormalizer
{
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "yclid", "msclkid", "mc_cid", "mc_eid", "igshid", "mkt_tok",
        "spm", "scm", "nsukey", "share_token", "share_source", "from_source", "ref", "ref_src",
        "source", "si", "wickedid", "oly_anon_id", "oly_enc_id", "vero_id", "trk", "trkcampaign"
    };

    public static string? Normalize(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;

        var host = uri.IdnHost.ToLowerInvariant();
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var path = uri.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";
        var query = FilterQuery(uri.Query);
        return $"{uri.Scheme}://{authority}{path}{(query.Length == 0 ? "" : "?" + query)}";
    }

    public static string DomainOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : "";

    private static string FilterQuery(string query)
    {
        var value = query.TrimStart('?');
        if (value.Length == 0) return "";
        return string.Join('&', value
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsTracking(pair)));
    }

    private static bool IsTracking(string pair)
    {
        var key = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParameters.Contains(key);
    }
}
