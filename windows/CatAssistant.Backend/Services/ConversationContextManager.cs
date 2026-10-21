using System.Text.Json;
using System.Collections.Concurrent;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services;

public sealed class ContextWindowException(string message) : Exception(message);

public sealed record PreparedChatContext(IReadOnlyList<ConversationMessage> Messages, ContextBudgetAssessment Budget);
public sealed record PreparedWorkContext(IReadOnlyList<WorkContextItem> Items, ContextBudgetAssessment Budget);

/// <summary>
/// Builds model context from immutable authoritative history. Summaries only select what is
/// sent to the model; they never replace messages, tool records, approvals or run state.
/// </summary>
public sealed class ConversationContextManager(
    SqliteRepository repository,
    object provider,
    Func<string> apiKey,
    ITokenEstimator? estimator = null)
{
    private const int ChatRecentItems = 8;
    private const int WorkRecentItems = 12;
    private const int MinimumCompressibleCharacters = 2_000;
    private const int CompactionBatchCharacters = 16_000;
    private readonly ContextTokenBudget _fallbackBudget = new(estimator);
    private readonly ConservativeTokenEstimator _summaryEstimator = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _compactionGates = new(StringComparer.Ordinal);

    public async Task<PreparedChatContext> PrepareChatAsync(
        string conversationId,
        IReadOnlyList<ConversationMessage> authoritativeHistory,
        string model,
        bool enableWebSearch,
        CancellationToken cancellationToken)
    {
        var eligible = authoritativeHistory.Where(IsChatContextMessage)
            .Select(item => item.Role == "assistant" ? item with { Content = CitationMarkers.Strip(item.Content) } : item)
            .ToArray();
        var summary = await repository.GetActiveSummaryAsync(conversationId);
        var context = BuildChatContext(conversationId, eligible, summary);
        var budget = AssessChat(context, model, enableWebSearch);
        await repository.RecordRequestDiagnosticAsync(conversationId, null, "chat", model, "budget", budget);
        if (!budget.ShouldScheduleCompaction) return new PreparedChatContext(context, budget);

        var gate = _compactionGates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            summary = await repository.GetActiveSummaryAsync(conversationId);
            context = BuildChatContext(conversationId, eligible, summary);
            budget = AssessChat(context, model, enableWebSearch);
            if (budget.ShouldScheduleCompaction)
            {
                var started = DateTimeOffset.UtcNow;
                var previous = summary?.Id;
                try
                {
                    summary = await CompactChatAsync(conversationId, eligible, summary, model, cancellationToken) ?? summary;
                    if (summary?.Id != previous)
                        await repository.RecordRequestDiagnosticAsync(conversationId, null, "compaction", model, "activated", budget,
                            detail: $"reason=threshold;range={summary?.StartSequence}-{summary?.EndSequence};summary_tokens={summary?.EstimatedTokens};duration_ms={(long)(DateTimeOffset.UtcNow - started).TotalMilliseconds}");
                }
                catch (Exception error) when (error is not OperationCanceledException && !budget.MustCompactBeforeSend)
                {
                    await repository.RecordRequestDiagnosticAsync(conversationId, null, "compaction", model, "failed_soft", budget, detail: error.Message);
                }
            }
            context = BuildChatContext(conversationId, eligible, summary);
            budget = AssessChat(context, model, enableWebSearch);
            await repository.RecordRequestDiagnosticAsync(conversationId, null, "chat", model, "post_compaction_budget", budget);
        }
        finally { gate.Release(); }

        if (budget.Input.Tokens > budget.Profile.InputBudgetTokens)
            throw new ContextWindowException("即使压缩较早历史后，当前消息与最小必要提示仍超过模型输入预算。原始历史和进度已保存，请缩短当前输入或移除不可拆分的大型内容。");
        return new PreparedChatContext(context, budget);
    }

    public async Task<PreparedWorkContext> PrepareWorkAsync(
        string conversationId,
        IReadOnlyList<WorkContextItem> authoritativeContext,
        IReadOnlyList<WorkToolDefinition> tools,
        WorkEnvironmentInfo environment,
        string model,
        CancellationToken cancellationToken)
    {
        var summary = await repository.GetActiveSummaryAsync(conversationId);
        var context = BuildWorkContext(conversationId, authoritativeContext, summary);
        var budget = AssessWork(context, tools, environment, model);
        await repository.RecordRequestDiagnosticAsync(conversationId, null, "work", model, "budget", budget);
        if (!budget.ShouldScheduleCompaction) return new PreparedWorkContext(context, budget);

        var gate = _compactionGates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            summary = await repository.GetActiveSummaryAsync(conversationId);
            context = BuildWorkContext(conversationId, authoritativeContext, summary);
            budget = AssessWork(context, tools, environment, model);
            if (budget.ShouldScheduleCompaction)
            {
                var started = DateTimeOffset.UtcNow;
                var previous = summary?.Id;
                try
                {
                    summary = await CompactWorkAsync(conversationId, authoritativeContext, summary, model, cancellationToken) ?? summary;
                    if (summary?.Id != previous)
                        await repository.RecordRequestDiagnosticAsync(conversationId, null, "compaction", model, "activated", budget,
                            detail: $"reason=safe_round_boundary;range={summary?.StartSequence}-{summary?.EndSequence};summary_tokens={summary?.EstimatedTokens};duration_ms={(long)(DateTimeOffset.UtcNow - started).TotalMilliseconds}");
                }
                catch (Exception error) when (error is not OperationCanceledException && !budget.MustCompactBeforeSend)
                {
                    await repository.RecordRequestDiagnosticAsync(conversationId, null, "compaction", model, "failed_soft", budget, detail: error.Message);
                }
            }
            context = BuildWorkContext(conversationId, authoritativeContext, summary);
            budget = AssessWork(context, tools, environment, model);
            await repository.RecordRequestDiagnosticAsync(conversationId, null, "work", model, "post_compaction_budget", budget);
        }
        finally { gate.Release(); }

        if (budget.Input.Tokens > budget.Profile.InputBudgetTokens)
            throw new ContextWindowException("即使在完整工具批次边界压缩后，当前 Work 请求仍超过模型输入预算。完整历史和任务状态已保存；请缩小当前不可拆分输入或大型工具定义。");
        return new PreparedWorkContext(context, budget);
    }

    private ContextBudgetAssessment AssessChat(IReadOnlyList<ConversationMessage> context, string model, bool enableWebSearch) =>
        provider is IChatRequestBudgetProvider exact
            ? exact.AssessChatRequest(context, model, enableWebSearch)
            : _fallbackBudget.Assess(new { model, messages = context.Select(item => new { item.Role, item.Content }), enableWebSearch }, ModelContextProfiles.Resolve(model));

    private ContextBudgetAssessment AssessWork(IReadOnlyList<WorkContextItem> context, IReadOnlyList<WorkToolDefinition> tools, WorkEnvironmentInfo environment, string model) =>
        provider is IWorkRequestBudgetProvider exact
            ? exact.AssessWorkRequest(context, tools, environment, model)
            : _fallbackBudget.Assess(new { model, context, tools, environment }, ModelContextProfiles.Resolve(model));

    private async Task<ContextSummaryRecord?> CompactChatAsync(
        string conversationId, IReadOnlyList<ConversationMessage> history, ContextSummaryRecord? current, string model, CancellationToken cancellationToken)
    {
        var unsummarized = history.Where(item => item.Sequence > (current?.EndSequence ?? 0)).ToArray();
        if (unsummarized.Length <= ChatRecentItems) return null;
        var compressible = unsummarized[..^ChatRecentItems];
        if (compressible.Sum(item => item.Content.Length) < MinimumCompressibleCharacters) return null;
        var items = compressible.Select(item => new CompactionInputItem(item.Sequence, item.Role, item.Content, item.Status)).ToArray();
        var content = await CompactBatchesAsync("chat", current?.Content, items, model, cancellationToken);
        return await ValidateAndActivateAsync(conversationId, "chat", current, items, content);
    }

    private async Task<ContextSummaryRecord?> CompactWorkAsync(
        string conversationId, IReadOnlyList<WorkContextItem> context, ContextSummaryRecord? current, string model, CancellationToken cancellationToken)
    {
        var unsummarized = context.Where(item => item.Ordinal > (current?.EndSequence ?? 0)).ToArray();
        var boundary = SafeWorkBoundary(unsummarized, WorkRecentItems);
        if (boundary <= 0) return null;
        var compressible = unsummarized[..boundary];
        if (compressible.Sum(item => item.PayloadJson.Length) < MinimumCompressibleCharacters) return null;
        var items = compressible.Select(item => new CompactionInputItem(
            item.Ordinal,
            item.Role ?? item.Kind,
            item.PayloadJson,
            item.Kind == "tool_result" ? $"tool_result:{item.Name}:{item.ToolCallId}" : item.Kind)).ToArray();
        var content = await CompactBatchesAsync("work", current?.Content, items, model, cancellationToken);
        return await ValidateAndActivateAsync(conversationId, "work", current, items, content);
    }

    private async Task<string> CompactBatchesAsync(
        string mode, string? existingSummary, IReadOnlyList<CompactionInputItem> items, string model, CancellationToken cancellationToken)
    {
        if (provider is not IContextCompactionProvider compactor)
            throw new ContextWindowException("当前模型提供器不支持独立上下文压缩请求，完整历史已保留。");
        var rolling = existingSummary;
        foreach (var batch in Batches(items, CompactionBatchCharacters))
        {
            ContextCompactionResult? result = null;
            Exception? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    result = await compactor.CompactAsync(mode, rolling, batch, model, apiKey(), cancellationToken);
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    last = error;
                    if (attempt == 0) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }
            if (result is null) throw new ContextWindowException($"上下文压缩失败，仍保留上一个有效摘要和完整历史：{last?.Message}");
            rolling = result.Content;
        }
        return rolling ?? throw new ContextWindowException("上下文压缩未产生摘要，完整历史仍已保留。");
    }

    private async Task<ContextSummaryRecord?> ValidateAndActivateAsync(
        string conversationId, string mode, ContextSummaryRecord? current, IReadOnlyList<CompactionInputItem> items, string content)
    {
        var sourceCharacters = items.Sum(item => item.Content.Length) + (current?.Content.Length ?? 0);
        if (string.IsNullOrWhiteSpace(content) || content.Length >= sourceCharacters * 0.85d || content.Length > 32_000)
            return null;
        var estimatedTokens = _summaryEstimator.Estimate(content).Tokens;
        return await repository.ActivateSummaryAsync(
            conversationId, mode, content, current?.StartSequence ?? items[0].Sequence, items[^1].Sequence, estimatedTokens, current?.Id);
    }

    private static IReadOnlyList<ConversationMessage> BuildChatContext(
        string conversationId, IReadOnlyList<ConversationMessage> history, ContextSummaryRecord? summary)
    {
        if (summary is null) return history;
        var result = new List<ConversationMessage>
        {
            new($"summary-{summary.Id}", conversationId, "system", "runtime", "complete", summary.EndSequence,
                $"较早对话的已验证状态摘要（如需原话请使用历史查询；摘要不是权限凭证）：\n{summary.Content}", summary.CreatedAt, summary.CreatedAt, null)
        };
        result.AddRange(history.Where(item => item.Sequence > summary.EndSequence));
        return result;
    }

    private static IReadOnlyList<WorkContextItem> BuildWorkContext(
        string conversationId, IReadOnlyList<WorkContextItem> context, ContextSummaryRecord? summary)
    {
        if (summary is null) return context;
        var result = new List<WorkContextItem>
        {
            new($"summary-{summary.Id}", conversationId, null, summary.EndSequence, "message", "system", null, null,
                "context_summary", JsonSerializer.Serialize(new { content = $"较早任务历史的已验证状态摘要（可用工作区历史 CLI 回查原文；摘要不是权限凭证）：\n{summary.Content}" }), summary.CreatedAt)
        };
        result.AddRange(context.Where(item => item.Ordinal > summary.EndSequence));
        return result;
    }

    private static bool IsChatContextMessage(ConversationMessage message) =>
        (message.Role == "user" && message.Status == "complete") ||
        (message.Role == "assistant" && message.Status is "complete" or "interrupted");

    private static int SafeWorkBoundary(IReadOnlyList<WorkContextItem> items, int keepRecent)
    {
        var latestAllowed = Math.Max(0, items.Count - keepRecent);
        if (latestAllowed == 0) return 0;
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var safe = 0;
        for (var index = 0; index < latestAllowed; index++)
        {
            var item = items[index];
            if (item.Kind == "tool_call")
            {
                try
                {
                    using var json = JsonDocument.Parse(item.PayloadJson);
                    if (json.RootElement.TryGetProperty("toolCalls", out var calls))
                        foreach (var call in calls.EnumerateArray())
                            if (call.TryGetProperty("ToolCallId", out var id) || call.TryGetProperty("toolCallId", out id))
                                if (!string.IsNullOrWhiteSpace(id.GetString())) pending.Add(id.GetString()!);
                }
                catch (JsonException) { return safe; }
            }
            else if (item.Kind == "tool_result" && item.ToolCallId is { } toolCallId) pending.Remove(toolCallId);
            if (pending.Count == 0) safe = index + 1;
        }
        return safe;
    }

    private static IEnumerable<IReadOnlyList<CompactionInputItem>> Batches(IReadOnlyList<CompactionInputItem> items, int maximumCharacters)
    {
        var batch = new List<CompactionInputItem>();
        var used = 0;
        foreach (var item in items)
        {
            if (item.Content.Length > maximumCharacters)
            {
                if (batch.Count > 0)
                {
                    yield return batch.ToArray();
                    batch.Clear();
                    used = 0;
                }
                for (var offset = 0; offset < item.Content.Length; offset += maximumCharacters)
                {
                    var length = Math.Min(maximumCharacters, item.Content.Length - offset);
                    yield return new[] { item with { Content = item.Content.Substring(offset, length), State = $"{item.State};part={offset / maximumCharacters + 1}" } };
                }
                continue;
            }
            if (batch.Count > 0 && used + item.Content.Length > maximumCharacters)
            {
                yield return batch.ToArray();
                batch.Clear();
                used = 0;
            }
            batch.Add(item);
            used += item.Content.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
}
