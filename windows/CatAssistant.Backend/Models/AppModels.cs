using CatAssistant.Backend.Services;

namespace CatAssistant.Backend.Models;

public sealed class AppSettings
{
    public string AsrModel { get; set; } = "qwen-audio-3.1-asr-flash";
    public string ChatModel { get; set; } = "qwen-plus";
    public string WorkModel { get; set; } = "qwen3.8-max";
    public ulong? LastBluetoothAddress { get; set; }
    public string? LastDeviceName { get; set; }
    public uint ScreenTimeoutSeconds { get; set; } = 60;
    public bool AutoReconnect { get; set; } = true;
    public bool WebSearchEnabled { get; set; } = true;
}

public sealed record PublicSettings(
    string AsrModel,
    string ChatModel,
    string WorkModel,
    uint ScreenTimeoutSeconds,
    bool AutoReconnect,
    bool WebSearchEnabled,
    bool HasApiKey,
    bool HasSearchApiKey);

public sealed record ConversationSummary(
    string Id,
    string Mode,
    string Title,
    string TitleSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsPinned,
    string? OriginConversationId = null);

public sealed record ConversationMessage(
    string Id,
    string ConversationId,
    string Role,
    string Source,
    string Status,
    long Sequence,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? RunId);

public sealed record CitationRecord(
    string Id,
    string MessageId,
    int Ordinal,
    string Title,
    string Url,
    string? Snippet,
    string? SourceName);

public sealed record ConversationDetail(
    ConversationSummary Conversation,
    IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<CitationRecord> Citations,
    string? ActiveRunId,
    IReadOnlyList<WorkRequestRecord>? WorkRequests = null);

public sealed record WorkRequestRecord(
    string Id,
    string SourceConversationId,
    string SourceMessageId,
    string SourceRunId,
    string ToolCallId,
    string Title,
    string Prompt,
    string ContextSummary,
    string State,
    string? TargetConversationId,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DeviceState(
    string Status,
    string Detail,
    bool IsConnected,
    bool HasPairedDevice,
    ulong? PairedDeviceAddress,
    string? PairedDeviceName,
    uint MaxPduSize,
    IReadOnlyList<CatDevice> Devices,
    string? ActiveConversationId,
    string? ActiveConversationTitle,
    bool FollowDesktop);

public sealed record BootstrapState(
    IReadOnlyList<ConversationSummary> Conversations,
    string? ActiveConversationId,
    ConversationDetail? ActiveConversation,
    DeviceState Device,
    PublicSettings Settings,
    IReadOnlyList<WorkApprovalRecord>? PendingApprovals = null,
    IReadOnlyList<WorkToolCallRecord>? WorkToolCalls = null,
    IReadOnlyList<WorkArtifactRecord>? WorkArtifacts = null);

public sealed record ConversationEvent(
    string Type,
    string ConversationId,
    string? MessageId = null,
    string? RunId = null,
    string? Delta = null,
    ConversationMessage? Message = null,
    ConversationSummary? Conversation = null,
    string? Status = null,
    string? Error = null,
    WorkApprovalRecord? Approval = null,
    WorkArtifactRecord? Artifact = null,
    WorkRequestRecord? WorkRequest = null);

// Retained for the provider boundary and legacy database compatibility.
public sealed record ChatMessage(string Role, string Content, DateTimeOffset CreatedAt);
public sealed record BackendSnapshot(
    string Status,
    string Detail,
    bool IsConnected,
    bool HasPairedDevice,
    ulong? PairedDeviceAddress,
    uint MaxPduSize,
    IReadOnlyList<CatDevice> Devices,
    PublicSettings Settings,
    string? ConversationId,
    IReadOnlyList<ConversationMessage> Messages);
