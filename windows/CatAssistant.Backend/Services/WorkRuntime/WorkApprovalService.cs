using System.Collections.Concurrent;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed record WorkApprovalOutcome(bool Approved, string State, WorkApprovalRecord Approval);

public sealed class WorkApprovalService
{
    private sealed record Pending(WorkApprovalRecord Approval, TaskCompletionSource<bool> Completion);
    private sealed record TaskGrant(string RunId, string Directory, string Capability);

    private readonly SqliteRepository _repository;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<TaskGrant>> _grants = new(StringComparer.Ordinal);

    public WorkApprovalService(SqliteRepository repository) => _repository = repository;

    public event EventHandler<ConversationEvent>? Changed;

    public async Task<WorkApprovalOutcome> RequestAsync(
        WorkToolExecutionContext context,
        string operationJson,
        string scope,
        string targetPath,
        string capability,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(targetPath);
        if (HasTaskGrant(context.RunId, fullPath, capability))
        {
            var synthetic = new WorkApprovalRecord("task-grant", context.ConversationId, context.RunId, context.ToolCallId,
                operationJson, "task_directory", fullPath, capability, "approved", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return new WorkApprovalOutcome(true, "approved", synthetic);
        }

        var approvalTarget = scope == "task_directory" && !Directory.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
        var approval = await _repository.CreateWorkApprovalAsync(
            context.ConversationId, context.RunId, context.ToolCallId, operationJson, scope, approvalTarget, capability);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(approval.Id, new Pending(approval, completion))) throw new InvalidOperationException("审批 ID 冲突。");
        Changed?.Invoke(this, new ConversationEvent("approval.requested", context.ConversationId, RunId: context.RunId, Status: "waiting_approval", Approval: approval));
        try
        {
            var approved = await completion.Task.WaitAsync(cancellationToken);
            if (approved && scope == "task_directory")
            {
                _grants.GetOrAdd(context.RunId, _ => []).Add(new TaskGrant(context.RunId, approvalTarget, capability));
            }
            return new WorkApprovalOutcome(approved, approved ? "approved" : "rejected", approval with
            {
                State = approved ? "approved" : "rejected",
                ResolvedAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            try { await _repository.ResolveWorkApprovalAsync(approval.Id, "cancelled"); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _pending.TryRemove(approval.Id, out _); }
    }

    public async Task ResolveAsync(string conversationId, string approvalId, bool approved)
    {
        if (!_pending.TryGetValue(approvalId, out var pending) || pending.Approval.ConversationId != conversationId)
            throw new InvalidOperationException("审批不存在、已处理或不属于该对话。");
        var state = approved ? "approved" : "rejected";
        await _repository.ResolveWorkApprovalAsync(approvalId, state);
        pending.Completion.TrySetResult(approved);
        Changed?.Invoke(this, new ConversationEvent("approval.resolved", conversationId, RunId: pending.Approval.RunId, Status: state,
            Approval: pending.Approval with { State = state, ResolvedAt = DateTimeOffset.UtcNow }));
    }

    public Task<IReadOnlyList<WorkApprovalRecord>> ListPendingAsync(string conversationId) =>
        _repository.ListPendingWorkApprovalsAsync(conversationId);

    public void EndRun(string runId) => _grants.TryRemove(runId, out _);

    private bool HasTaskGrant(string runId, string path, string capability)
    {
        if (!_grants.TryGetValue(runId, out var grants)) return false;
        return grants.Any(grant => CapabilityAllows(grant.Capability, capability) && IsAtOrBelow(grant.Directory, path));
    }

    private static bool CapabilityAllows(string granted, string requested) =>
        granted == requested || granted == "write" && requested == "read";

    private static bool IsAtOrBelow(string directory, string path)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(path);
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
