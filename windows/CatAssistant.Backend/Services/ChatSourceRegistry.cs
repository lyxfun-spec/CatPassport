using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed record ChatSourceResolution(
    IReadOnlyList<ConversationSourceRecord> NewSources,
    IReadOnlyList<ConversationSourceRecord> UpdatedSources,
    int DuplicateCount,
    int OmittedByLimit);

/// <summary>
/// Conversation-level source registry. Ordinals are assigned on first discovery and never
/// reordered, reused or reassigned; duplicate URLs are silently filtered.
/// </summary>
public sealed class ChatSourceRegistry
{
    public const int MaxConversationSources = 200;

    private readonly string _conversationId;
    private readonly SqliteRepository? _repository;
    private readonly Dictionary<string, ConversationSourceRecord> _byNormalizedUrl = new(StringComparer.Ordinal);
    private readonly List<ConversationSourceRecord> _sources = [];
    private int _nextOrdinal = 1;

    public ChatSourceRegistry(
        string conversationId,
        IEnumerable<ConversationSourceRecord> existing,
        SqliteRepository? repository = null,
        int ordinalFloor = 0)
    {
        _conversationId = conversationId;
        _repository = repository;
        foreach (var source in existing.OrderBy(item => item.Ordinal))
        {
            _sources.Add(source);
            _byNormalizedUrl[source.NormalizedUrl] = source;
        }
        _nextOrdinal = Math.Max(
            _sources.Count == 0 ? 1 : _sources.Max(item => item.Ordinal) + 1,
            Math.Max(0, ordinalFloor) + 1);
    }

    public ChatSourceRegistry(string conversationId) : this(conversationId, [], null)
    {
    }

    public IReadOnlyList<ConversationSourceRecord> Sources => _sources;

    public ConversationSourceRecord? FindByOrdinal(int ordinal) =>
        _sources.FirstOrDefault(item => item.Ordinal == ordinal);

    public async Task<ChatSourceResolution> ResolveAsync(
        IReadOnlyList<RankedWebSearchResult> ranked,
        string? operationId,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        var newSources = new List<ConversationSourceRecord>();
        var updatedSources = new List<ConversationSourceRecord>();
        var duplicates = 0;
        var omitted = 0;

        foreach (var item in ranked)
        {
            var result = item.Result;
            if (!WebSearchSafety.IsSafeUrl(result.Url)) continue;
            var normalized = WebUrlNormalizer.Normalize(result.Url);
            if (normalized is null) continue;

            if (_byNormalizedUrl.TryGetValue(normalized, out var existing))
            {
                duplicates++;
                var improved = Improve(existing, result, item);
                if (improved != existing)
                {
                    var updated = improved with { UpdatedAt = DateTimeOffset.UtcNow };
                    if (_repository is not null)
                        await _repository.UpdateConversationSourceDetailsAsync(updated, cancellationToken);
                    ReplaceInMemory(updated);
                    updatedSources.Add(updated);
                }
                continue;
            }

            if (_sources.Count >= MaxConversationSources)
            {
                omitted++;
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var source = new ConversationSourceRecord(
                Guid.NewGuid().ToString("N"),
                _conversationId,
                _nextOrdinal,
                string.IsNullOrWhiteSpace(result.Title) ? WebUrlNormalizer.DomainOf(normalized) : result.Title,
                result.Url,
                normalized,
                result.Snippet,
                result.SiteName,
                WebUrlNormalizer.DomainOf(normalized),
                toolCallId ?? "",
                operationId ?? "",
                now,
                now,
                result.PublishedTime,
                result.Authority,
                result.RerankScore,
                item.IsOfficial,
                item.WeatherKind);

            if (_repository is not null)
            {
                var stored = await _repository.AddConversationSourceAsync(source, cancellationToken);
                if (stored.Id != source.Id)
                {
                    duplicates++;
                    _byNormalizedUrl[stored.NormalizedUrl] = stored;
                    if (!_sources.Any(existingSource => existingSource.Id == stored.Id)) _sources.Add(stored);
                    continue;
                }
                source = stored;
            }
            _nextOrdinal = Math.Max(_nextOrdinal, source.Ordinal + 1);
            _byNormalizedUrl[source.NormalizedUrl] = source;
            if (!_sources.Any(existingSource => existingSource.Id == source.Id)) _sources.Add(source);
            newSources.Add(source);
        }

        return new ChatSourceResolution(newSources, updatedSources, duplicates, omitted);
    }

    private static ConversationSourceRecord Improve(
        ConversationSourceRecord existing,
        WebSearchResult result,
        RankedWebSearchResult ranked) => existing with
    {
        Title = PreferLonger(existing.Title, result.Title) ?? existing.Title,
        Snippet = PreferLonger(existing.Snippet, result.Snippet),
        SiteName = string.IsNullOrWhiteSpace(existing.SiteName) ? result.SiteName : existing.SiteName,
        Domain = string.IsNullOrWhiteSpace(existing.Domain) ? WebUrlNormalizer.DomainOf(existing.NormalizedUrl) : existing.Domain,
        PublishedTime = existing.PublishedTime ?? result.PublishedTime,
        Authority = existing.Authority ?? result.Authority,
        RerankScore = existing.RerankScore ?? result.RerankScore,
        IsOfficial = existing.IsOfficial || ranked.IsOfficial,
        WeatherKind = string.IsNullOrWhiteSpace(existing.WeatherKind) || existing.WeatherKind == "unknown"
            ? ranked.WeatherKind
            : existing.WeatherKind
    };

    private void ReplaceInMemory(ConversationSourceRecord updated)
    {
        var index = _sources.FindIndex(item => item.Id == updated.Id);
        if (index >= 0) _sources[index] = updated;
        _byNormalizedUrl[updated.NormalizedUrl] = updated;
    }

    private static string? PreferLonger(string? existing, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return candidate;
        return candidate.Length > existing.Length ? candidate : existing;
    }
}
