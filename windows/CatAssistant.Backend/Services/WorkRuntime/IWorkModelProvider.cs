using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed record WorkToolDefinition(string Name, string Description, string ParametersJson);

public sealed record WorkModelToolCall(string ToolCallId, string Name, string ArgumentsJson);

public sealed record WorkModelResponse(string? Content, IReadOnlyList<WorkModelToolCall> ToolCalls);

public interface IWorkModelProvider
{
    Task<WorkModelResponse> CompleteAsync(
        IReadOnlyList<WorkContextItem> context,
        IReadOnlyList<WorkToolDefinition> tools,
        string model,
        string apiKey,
        CancellationToken cancellationToken);
}
