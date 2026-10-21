using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed record ChatToolCall(string ToolCallId, string Name, string ArgumentsJson);
public sealed record ChatModelMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record ChatStreamChunk(
    string? Delta = null,
    IReadOnlyList<CitationRecord>? Citations = null,
    ChatToolCall? ToolCall = null,
    ModelUsage? Usage = null);

public interface IChatRequestBudgetProvider
{
    ContextBudgetAssessment AssessChatRequest(
        IReadOnlyList<ConversationMessage> history,
        string model,
        bool enableWebSearch);
}

public sealed record CompactionInputItem(long Sequence, string Role, string Content, string? State = null);
public sealed record ContextCompactionResult(string Content, ModelUsage? Usage = null);

public interface IContextCompactionProvider
{
    Task<ContextCompactionResult> CompactAsync(
        string mode,
        string? existingSummary,
        IReadOnlyList<CompactionInputItem> items,
        string model,
        string apiKey,
        CancellationToken cancellationToken);
}

public interface IChatToolLoopProvider
{
    IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
        IReadOnlyList<ChatModelMessage> messages,
        string model,
        string apiKey,
        bool enableWebSearch,
        CancellationToken cancellationToken);

    ContextBudgetAssessment AssessChatTurn(
        IReadOnlyList<ChatModelMessage> messages,
        string model,
        bool enableWebSearch);
}

public interface IChatProvider
{
    IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        IReadOnlyList<ConversationMessage> history,
        string model,
        string apiKey,
        bool enableWebSearch,
        CancellationToken cancellationToken);

    Task<string> GenerateTitleAsync(
        string userMessage,
        string assistantMessage,
        string model,
        string apiKey,
        CancellationToken cancellationToken);
}

