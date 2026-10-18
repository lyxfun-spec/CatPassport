namespace CatAssistant.Backend.Models;

public sealed record WorkWorkspacePaths(
    string Root,
    string Inputs,
    string Work,
    string Outputs);

public sealed record WorkContextItem(
    string Id,
    string ConversationId,
    string? RunId,
    long Ordinal,
    string Kind,
    string? Role,
    string? MessageId,
    string? ToolCallId,
    string? Name,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record WorkToolCallRecord(
    string Id,
    string ConversationId,
    string RunId,
    string RoundId,
    string BatchId,
    string ToolCallId,
    int Ordinal,
    string ToolName,
    string ArgumentsJson,
    string State,
    string? ResultJson,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMilliseconds);

public sealed record WorkApprovalRecord(
    string Id,
    string ConversationId,
    string RunId,
    string ToolCallId,
    string OperationJson,
    string Scope,
    string? TargetPath,
    string Capability,
    string State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ResolvedAt);

public sealed record WorkArtifactRecord(
    string Id,
    string ConversationId,
    string? RunId,
    string Path,
    string Label,
    DateTimeOffset CreatedAt);
