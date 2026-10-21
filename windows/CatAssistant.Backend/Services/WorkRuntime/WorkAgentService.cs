using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed class WorkAgentService : IAsyncDisposable
{
    // The streamed row is only a crash checkpoint; the round commit, stop and failure paths all store the full text.
    private static readonly TimeSpan StreamPersistInterval = TimeSpan.FromMilliseconds(250);

    private sealed class ActiveWorkRun(string id, string conversationId, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string ConversationId { get; } = conversationId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();
        public Task? Execution { get; set; }
        public bool Stopping { get; set; }
        public string? StreamMessageId { get; set; }
        public StringBuilder StreamContent { get; } = new();
        public DateTimeOffset? LastContentPersistedAt { get; set; }
    }

    private readonly SqliteRepository _repository;
    private readonly WorkWorkspaceService _workspaces;
    private readonly IWorkModelProvider _provider;
    private readonly WorkToolRegistry _tools;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _apiKey;
    private readonly CancellationToken _lifetime;
    private readonly WorkApprovalService? _approvals;
    private readonly ConversationContextManager _contextManager;
    private readonly WorkHistoryExporter _historyExporter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveWorkRun> _runs = new(StringComparer.Ordinal);

    public WorkAgentService(
        SqliteRepository repository,
        WorkWorkspaceService workspaces,
        IWorkModelProvider provider,
        WorkToolRegistry tools,
        Func<AppSettings> settings,
        Func<string> apiKey,
        CancellationToken lifetime,
        WorkApprovalService? approvals = null)
    {
        _repository = repository;
        _workspaces = workspaces;
        _provider = provider;
        _tools = tools;
        _settings = settings;
        _apiKey = apiKey;
        _lifetime = lifetime;
        _approvals = approvals;
        _contextManager = new ConversationContextManager(repository, provider, apiKey);
        _historyExporter = new WorkHistoryExporter(repository);
    }

    public event EventHandler<ConversationEvent>? Changed;

    public string? ActiveRunId(string conversationId) => _runs.TryGetValue(conversationId, out var run) ? run.Id : null;

    public async Task<ConversationDetail> SendAsync(string conversationId, string text, string source)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("消息不能为空。", nameof(text));
        if (source != "desktop") throw new ArgumentOutOfRangeException(nameof(source), "Work 第一版只接受桌面消息。");
        var conversation = await _repository.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("对话不存在。");
        if (conversation.Mode != "work") throw new InvalidOperationException("该对话不使用 Work Runtime。");

        var gate = Gate(conversationId);
        await gate.WaitAsync(_lifetime);
        try
        {
            if (_runs.TryGetValue(conversationId, out var stoppingRun) && stoppingRun.Stopping)
                throw new InvalidOperationException("Work 任务正在停止。");
            var user = await _repository.AddMessageAsync(conversationId, "user", source, "complete", text);
            if (_runs.TryGetValue(conversationId, out var current))
            {
                current.PendingMessages.Enqueue(user);
                Publish(new ConversationEvent("message.created", conversationId, user.Id, current.Id, Message: user));
                return (await _repository.GetConversationDetailAsync(conversationId, current.Id))!;
            }

            var runId = Guid.NewGuid().ToString("N");
            await _repository.CreateRunAsync(runId, conversationId, user.Id, "work");
            await SynchronizeUntrackedMessagesAsync(conversationId, runId);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            var run = new ActiveWorkRun(runId, conversationId, cancellation);
            if (!_runs.TryAdd(conversationId, run)) throw new InvalidOperationException("Work Run 状态冲突。");
            Publish(new ConversationEvent("message.created", conversationId, user.Id, runId, Message: user));
            Publish(new ConversationEvent("run.started", conversationId, RunId: runId, Status: "starting"));
            run.Execution = ExecuteObservedAsync(run);
            return (await _repository.GetConversationDetailAsync(conversationId, runId))!;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(string conversationId)
    {
        var gate = Gate(conversationId);
        ActiveWorkRun? run;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_runs.TryGetValue(conversationId, out run)) return;
            run.Stopping = true;
            run.Cancellation.Cancel();
        }
        finally
        {
            gate.Release();
        }
        if (run.Execution is not null) await run.Execution;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_runs.TryGetValue(conversationId, out var current) && ReferenceEquals(current, run))
                _runs.TryRemove(conversationId, out _);
            await _repository.FinishRunAsync(run.Id, "interrupted", "interrupted");
            _approvals?.EndRun(run.Id);
            Publish(new ConversationEvent("run.stopped", conversationId, RunId: run.Id, Status: "stopped"));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteObservedAsync(ActiveWorkRun run)
    {
        try { await ExecuteAsync(run); }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            await InterruptStreamedMessageAsync(run);
        }
        catch (Exception error)
        {
            await CompleteIfCurrentAsync(run, "failed", "error", error);
        }
        finally
        {
            run.Cancellation.Dispose();
        }
    }

    private async Task ExecuteAsync(ActiveWorkRun run)
    {
        var workspace = await _workspaces.EnsureAsync(run.ConversationId, run.Cancellation.Token);
        var environment = WorkEnvironmentInfo.From(workspace);
        var round = 0;
        while (true)
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            await DrainPendingMessagesAsync(run);
            var context = await _repository.LoadWorkContextItemsAsync(run.ConversationId);
            await _historyExporter.ExportAsync(run.ConversationId, workspace, run.Cancellation.Token);
            var settings = _settings();
            var definitions = _tools.Definitions;
            var prepared = await _contextManager.PrepareWorkAsync(
                run.ConversationId, context, definitions, environment, settings.WorkModel, run.Cancellation.Token);
            // A message that arrived during export/compaction must be persisted and budgeted
            // before the next model request rather than waiting behind that request.
            if (await DrainPendingMessagesAsync(run)) continue;
            context = prepared.Items;
            run.StreamMessageId = null;
            run.StreamContent.Clear();
            run.LastContentPersistedAt = null;
            var response = await _provider.CompleteAsync(
                context,
                definitions,
                environment,
                settings.WorkModel,
                _apiKey(),
                delta => AppendStreamedContentAsync(run, delta),
                run.Cancellation.Token);
            run.Cancellation.Token.ThrowIfCancellationRequested();
            if (response.Usage is not null) await _repository.SetRunUsageAsync(run.Id, response.Usage);
            await _repository.RecordRequestDiagnosticAsync(run.ConversationId, run.Id, "work", settings.WorkModel, "round_completed", prepared.Budget, response.Usage);

            var roundId = $"{run.Id}:{++round}";
            if (response.ToolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(response.Content)) await CommitRoundMessageAsync(run, response.Content!, addToContext: true);
                if (await CompleteOrContinueAsync(run)) continue;
                return;
            }

            ConversationMessage? explanation = null;
            if (!string.IsNullOrWhiteSpace(response.Content)) explanation = await CommitRoundMessageAsync(run, response.Content!, addToContext: false);
            await _repository.AddWorkContextItemAsync(
                run.ConversationId,
                run.Id,
                "tool_call",
                "assistant",
                JsonSerializer.Serialize(new { content = response.Content, toolCalls = response.ToolCalls }),
                messageId: explanation?.Id,
                name: "assistant_tool_calls");

            var batchId = Guid.NewGuid().ToString("N");
            var persistedCalls = new WorkToolCallRecord[response.ToolCalls.Count];
            for (var index = 0; index < response.ToolCalls.Count; index++)
            {
                var call = response.ToolCalls[index];
                persistedCalls[index] = await _repository.CreateWorkToolCallAsync(
                    run.ConversationId, run.Id, roundId, batchId, call.ToolCallId, index, call.Name, StorableArguments(call.ArgumentsJson));
                Publish(new ConversationEvent("tool.created", run.ConversationId, RunId: run.Id, Status: "pending"));
            }

            var tasks = response.ToolCalls.Select((call, index) => ExecuteToolAsync(run, workspace, call, persistedCalls[index])).ToArray();
            var results = await Task.WhenAll(tasks);
            for (var index = 0; index < results.Length; index++)
            {
                var call = response.ToolCalls[index];
                var result = await _historyExporter.ExternalizeToolResultAsync(workspace, run.Id, call, results[index], run.Cancellation.Token);
                await _repository.AddWorkContextItemAsync(
                    run.ConversationId,
                    run.Id,
                    "tool_result",
                    "tool",
                    result.OutputJson,
                    toolCallId: call.ToolCallId,
                    name: call.Name);
            }
            await DrainPendingMessagesAsync(run);
        }
    }

    private async Task<WorkToolResult> ExecuteToolAsync(
        ActiveWorkRun run,
        WorkWorkspacePaths workspace,
        WorkModelToolCall call,
        WorkToolCallRecord persisted)
    {
        var stopwatch = Stopwatch.StartNew();
        await _repository.UpdateWorkToolCallAsync(persisted.Id, "running");
        Publish(new ConversationEvent("tool.started", run.ConversationId, RunId: run.Id, Status: "running"));
        try
        {
            var result = await _tools.ExecuteAsync(call.Name, new WorkToolExecutionContext(run.ConversationId, run.Id, workspace.Root, call.ToolCallId), call.ArgumentsJson, run.Cancellation.Token);
            stopwatch.Stop();
            await _repository.UpdateWorkToolCallAsync(
                persisted.Id,
                result.Success ? "completed" : "failed",
                result.OutputJson,
                result.ErrorCode,
                result.ErrorMessage,
                stopwatch.ElapsedMilliseconds);
            Publish(new ConversationEvent(result.Success ? "tool.completed" : "tool.failed", run.ConversationId, RunId: run.Id,
                Status: result.Success ? "completed" : "failed", Error: result.ErrorMessage));
            return result;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            stopwatch.Stop();
            var result = WorkToolResult.Error("cancelled", "操作已取消。");
            await _repository.UpdateWorkToolCallAsync(persisted.Id, "cancelled", result.OutputJson, result.ErrorCode, result.ErrorMessage, stopwatch.ElapsedMilliseconds);
            Publish(new ConversationEvent("tool.cancelled", run.ConversationId, RunId: run.Id, Status: "cancelled"));
            return result;
        }
        catch (Exception error)
        {
            stopwatch.Stop();
            var result = WorkToolResult.Error("tool_error", error.Message);
            await _repository.UpdateWorkToolCallAsync(persisted.Id, "failed", result.OutputJson, result.ErrorCode, result.ErrorMessage, stopwatch.ElapsedMilliseconds);
            Publish(new ConversationEvent("tool.failed", run.ConversationId, RunId: run.Id, Status: "failed", Error: error.Message));
            return result;
        }
    }

    private async Task<bool> DrainPendingMessagesAsync(ActiveWorkRun run)
    {
        var drained = false;
        while (run.PendingMessages.TryDequeue(out var message))
        {
            await AddUserContextAsync(message, run.Id);
            drained = true;
        }
        return drained;
    }

    private async Task<bool> CompleteOrContinueAsync(ActiveWorkRun run)
    {
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_runs.TryGetValue(run.ConversationId, out var current) || !ReferenceEquals(current, run) || run.Stopping) return false;
            if (await DrainPendingMessagesAsync(run)) return true;
            var workspace = await _workspaces.EnsureAsync(run.ConversationId, CancellationToken.None);
            await _historyExporter.ExportAsync(run.ConversationId, workspace, CancellationToken.None);
            await _repository.FinishRunAsync(run.Id, "completed", "stop");
            _approvals?.EndRun(run.Id);
            _runs.TryRemove(run.ConversationId, out _);
            Publish(new ConversationEvent("run.completed", run.ConversationId, RunId: run.Id, Status: "completed"));
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SynchronizeUntrackedMessagesAsync(string conversationId, string runId)
    {
        foreach (var message in await _repository.LoadMessagesWithoutWorkContextAsync(conversationId))
        {
            await _repository.AddWorkContextItemAsync(
                conversationId,
                runId,
                "message",
                message.Role,
                JsonSerializer.Serialize(new { content = message.Content }),
                messageId: message.Id);
        }
    }

    private Task AddUserContextAsync(ConversationMessage message, string runId) =>
        _repository.AddWorkContextItemAsync(
            message.ConversationId,
            runId,
            "message",
            "user",
            JsonSerializer.Serialize(new { content = message.Content }),
            messageId: message.Id);

    private async Task AppendStreamedContentAsync(ActiveWorkRun run, string delta)
    {
        if (run.Stopping || string.IsNullOrEmpty(delta)) return;
        var messageId = run.StreamMessageId;
        if (messageId is null)
        {
            var created = await _repository.AddMessageAsync(run.ConversationId, "assistant", "runtime", "streaming", "", run.Id);
            messageId = created.Id;
            run.StreamMessageId = messageId;
            run.StreamContent.Clear();
            Publish(new ConversationEvent("message.created", run.ConversationId, created.Id, run.Id, Message: created));
        }
        run.StreamContent.Append(delta);
        if (run.LastContentPersistedAt is null || DateTimeOffset.UtcNow - run.LastContentPersistedAt >= StreamPersistInterval)
        {
            run.LastContentPersistedAt = DateTimeOffset.UtcNow;
            await _repository.UpdateMessageAsync(messageId, run.StreamContent.ToString(), "streaming");
        }
        Publish(new ConversationEvent("message.delta", run.ConversationId, messageId, run.Id, delta));
    }

    private async Task<ConversationMessage> CommitRoundMessageAsync(ActiveWorkRun run, string content, bool addToContext)
    {
        ConversationMessage message;
        if (run.StreamMessageId is { } streamedId)
        {
            run.StreamMessageId = null;
            await _repository.UpdateMessageAsync(streamedId, content, "complete");
            message = (await _repository.LoadMessagesAsync(run.ConversationId)).Single(item => item.Id == streamedId);
        }
        else message = await _repository.AddMessageAsync(run.ConversationId, "assistant", "runtime", "complete", content, run.Id);
        if (addToContext)
            await _repository.AddWorkContextItemAsync(
                run.ConversationId,
                run.Id,
                "message",
                "assistant",
                JsonSerializer.Serialize(new { content }),
                messageId: message.Id);
        Publish(new ConversationEvent("message.completed", run.ConversationId, message.Id, run.Id, Message: message, Status: "completed"));
        return message;
    }

    private async Task InterruptStreamedMessageAsync(ActiveWorkRun run)
    {
        if (run.StreamMessageId is not { } messageId) return;
        run.StreamMessageId = null;
        await _repository.UpdateMessageAsync(messageId, run.StreamContent.ToString(), "interrupted");
        Publish(new ConversationEvent("message.interrupted", run.ConversationId, messageId, run.Id, Status: "interrupted"));
    }

    private async Task RecordRunFailureAsync(ActiveWorkRun run, string reason)
    {
        if (run.StreamMessageId is { } messageId)
        {
            run.StreamMessageId = null;
            var partial = run.StreamContent.ToString();
            await _repository.UpdateMessageAsync(messageId, partial.Length > 0 ? $"{partial}\n\n{reason}" : reason, "failed");
            return;
        }
        var message = await _repository.AddMessageAsync(run.ConversationId, "assistant", "runtime", "failed", reason, run.Id);
        Publish(new ConversationEvent("message.created", run.ConversationId, message.Id, run.Id, Message: message));
    }

    private async Task CompleteIfCurrentAsync(ActiveWorkRun run, string state, string reason, Exception? error = null)
    {
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_runs.TryGetValue(run.ConversationId, out var current) || !ReferenceEquals(current, run) || run.Stopping) return;
            await _repository.FinishRunAsync(run.Id, state, reason, error?.GetType().Name);
            if (error is not null) await RecordRunFailureAsync(run, error.Message);
            _approvals?.EndRun(run.Id);
            _runs.TryRemove(run.ConversationId, out _);
            Publish(new ConversationEvent(error is null ? "run.completed" : "run.failed", run.ConversationId, RunId: run.Id, Status: state, Error: error?.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(string conversationId) => _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
    private void Publish(ConversationEvent value) => Changed?.Invoke(this, value);

    private static string StorableArguments(string value)
    {
        try { using var _ = JsonDocument.Parse(value); return value; }
        catch (JsonException) { return JsonSerializer.Serialize(new { invalidArguments = value }); }
    }

    public async ValueTask DisposeAsync()
    {
        var runs = _runs.Values.ToArray();
        foreach (var run in runs) run.Cancellation.Cancel();
        await Task.WhenAll(runs.Select(run => run.Execution ?? Task.CompletedTask));
        foreach (var run in runs)
        {
            await _repository.FinishRunAsync(run.Id, "interrupted", "interrupted");
            _approvals?.EndRun(run.Id);
        }
        _runs.Clear();
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }
}
