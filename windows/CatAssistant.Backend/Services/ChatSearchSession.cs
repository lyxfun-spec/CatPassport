using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed record ChatSearchMerge(
    IReadOnlyList<ConversationSourceRecord> NewSources,
    IReadOnlyList<ConversationSourceRecord> UpdatedSources,
    int NewResultCount,
    int DuplicateResultCount,
    int OmittedByLimit);

/// <summary>
/// One Chat run's search counters over the conversation-level source registry. Repeated searches
/// resolve against the registry, so ordinals stay stable across tool calls and turns.
/// </summary>
public sealed class ChatSearchSession(ChatSourceRegistry registry, string? originalQuestion = null)
{
    public const int MaxQueryCount = 3;

    public string? OriginalQuestion { get; } = originalQuestion;
    public ChatSourceRegistry Registry { get; } = registry;
    public int QueryCount { get; private set; }
    public int RawResultCount { get; private set; }
    public int NewResultCount { get; private set; }
    public int UniqueResultCount => NewResultCount;
    public int SourceCount => Registry.Sources.Count;
    public bool CanSearch => QueryCount < MaxQueryCount;

    public void BeginQuery()
    {
        if (!CanSearch) throw new InvalidOperationException("本轮搜索次数已达上限。");
        QueryCount++;
    }

    public async Task<ChatSearchMerge> MergeAsync(
        IReadOnlyList<RankedWebSearchResult> ranked,
        string? operationId,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        RawResultCount += ranked.Count;
        var resolution = await Registry.ResolveAsync(ranked, operationId, toolCallId, cancellationToken);
        NewResultCount += resolution.NewSources.Count;
        return new ChatSearchMerge(
            resolution.NewSources,
            resolution.UpdatedSources,
            resolution.NewSources.Count,
            resolution.DuplicateCount,
            resolution.OmittedByLimit);
    }
}
