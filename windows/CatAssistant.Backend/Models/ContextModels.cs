namespace CatAssistant.Backend.Models;

public sealed record ModelContextProfile(
    string Model,
    int ContextWindowTokens,
    int OutputReserveTokens,
    int SafetyMarginTokens,
    double SoftThreshold = 0.70,
    double CompactThreshold = 0.80,
    double TargetUtilization = 0.55)
{
    public int InputBudgetTokens => Math.Max(1, ContextWindowTokens - OutputReserveTokens - SafetyMarginTokens);
}

public sealed record TokenEstimate(
    int Tokens,
    string Source,
    bool IsEstimate,
    int PayloadCharacters,
    int PayloadBytes);

public sealed record ContextBudgetAssessment(
    ModelContextProfile Profile,
    TokenEstimate Input,
    double Utilization,
    bool ShouldScheduleCompaction,
    bool MustCompactBeforeSend)
{
    public int RemainingInputTokens => Math.Max(0, Profile.InputBudgetTokens - Input.Tokens);
}

public sealed record ModelUsage(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    string Source = "provider");

public sealed record ContextSummaryRecord(
    string Id,
    string ConversationId,
    string Mode,
    int Version,
    string? SourceSummaryId,
    long StartSequence,
    long EndSequence,
    string Content,
    int EstimatedTokens,
    string State,
    DateTimeOffset CreatedAt);

public sealed record ConversationContextState(
    string ConversationId,
    string? ActiveSummaryId,
    long CompressedThroughSequence,
    long SearchWatermarkSequence,
    long ExportWatermarkSequence,
    int Version);

public sealed record HistorySearchHit(
    string MessageId,
    string ConversationId,
    long Sequence,
    DateTimeOffset CreatedAt,
    string Role,
    string Status,
    string Snippet,
    double Score,
    string MatchSource);

public sealed record RequestDiagnostic(
    string Id,
    string ConversationId,
    string? RunId,
    string Mode,
    string Model,
    string Event,
    int EstimatedInputTokens,
    int InputBudgetTokens,
    int? ActualInputTokens,
    int? ActualOutputTokens,
    string CountSource,
    bool IsEstimate,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record MessageEmbeddingRecord(
    string MessageId,
    string ConversationId,
    string ContentHash,
    string Model,
    float[] Vector,
    DateTimeOffset UpdatedAt);
