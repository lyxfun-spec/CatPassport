using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed record ChatStreamChunk(string? Delta = null, IReadOnlyList<CitationRecord>? Citations = null);

public interface IChatProvider
{
    IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        IReadOnlyList<ConversationMessage> history,
        string model,
        string apiKey,
        bool enableWebSearch,
        CancellationToken cancellationToken);
}

