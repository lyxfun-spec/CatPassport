using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed class WorkAgentService : IAsyncDisposable
{
    private sealed class ActiveWorkRun(string id, string conversationId, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string ConversationId { get; } = conversationId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();
        public Task? Execution { get; set; }
        public bool Stopping { get; set; }
    }

    private readonly SqliteRepository _repository;
    private readonly WorkWorkspaceService _workspaces;
    private readonly IWorkModelProvider _provider;
    private readonly WorkToolRegistry _tools;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _apiKey;
    private readonly CancellationToken _lifetime;
    private readonly int _contextCharacterLimit;
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
        int contextCharacterLimit = 96_000)
    {
        _repository = repository;
        _workspaces = workspaces;
        _provider = provider;
        _tools = tools;
        _settings = settings;
        _apiKey = apiKey;
        _lifetime = lifetime;
        if (contextCharacterLimit <= 0) throw new ArgumentOutOfRangeException(nameof(contextCharacterLimit));
        _contextCharacterLimit = contextCharacterLimit;
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
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
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
        var round = 0;
        while (true)
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            var context = await _repository.LoadWorkContextItemsAsync(run.ConversationId);
            var settings = _settings();
            var definitions = _tools.Definitions;
            var estimatedCharacters = WorkContextBudget.EstimateCharacters(context, definitions);
            if (estimatedCharacters >= _contextCharacterLimit)
                throw new WorkContextLimitException(estimatedCharacters, _contextCharacterLimit);
            var response = await _provider.CompleteAsync(context, definitions, settings.ChatModel, _apiKey(), run.Cancellation.Token);
            run.Cancellation.Token.ThrowIfCancellationRequested();

            var roundId = $"{run.Id}:{++round}";
            if (response.ToolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(response.Content)) await AddAssistantMessageAsync(run, response.Content, addToContext: true);
                if (await CompleteOrContinueAsync(run)) continue;
                return;
            }

            ConversationMessage? explanation = null;
            if (!string.IsNullOrWhiteSpace(response.Content)) explanation = await AddAssistantMessageAsync(run, response.Content, addToContext: false);
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
            }

            var workspace = await _workspaces.EnsureAsync(run.ConversationId, run.Cancellation.Token);
            var tasks = response.ToolCalls.Select((call, index) => ExecuteToolAsync(run, workspace, call, persistedCalls[index])).ToArray();
            var results = await Task.WhenAll(tasks);
            for (var index = 0; index < results.Length; index++)
            {
                var call = response.ToolCalls[index];
                var result = results[index];
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
        try
        {
            var result = await _tools.ExecuteAsync(call.Name, new WorkToolExecutionContext(run.ConversationId, run.Id, workspace.Root), call.ArgumentsJson, run.Cancellation.Token);
            stopwatch.Stop();
            await _repository.UpdateWorkToolCallAsync(
                persisted.Id,
                result.Success ? "completed" : "failed",
                result.OutputJson,
                result.ErrorCode,
                result.ErrorMessage,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            stopwatch.Stop();
            var result = WorkToolResult.Error("cancelled", "操作已取消。");
            await _repository.UpdateWorkToolCallAsync(persisted.Id, "cancelled", result.OutputJson, result.ErrorCode, result.ErrorMessage, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception error)
        {
            stopwatch.Stop();
            var result = WorkToolResult.Error("tool_error", error.Message);
            await _repository.UpdateWorkToolCallAsync(persisted.Id, "failed", result.OutputJson, result.ErrorCode, result.ErrorMessage, stopwatch.ElapsedMilliseconds);
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
            _runs.TryRemove(run.ConversationId, out _);
            await _repository.FinishRunAsync(run.Id, "completed", "stop");
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

    private async Task<ConversationMessage> AddAssistantMessageAsync(ActiveWorkRun run, string content, bool addToContext)
    {
        var message = await _repository.AddMessageAsync(run.ConversationId, "assistant", "runtime", "complete", content, run.Id);
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

    private async Task CompleteIfCurrentAsync(ActiveWorkRun run, string state, string reason, Exception? error = null)
    {
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_runs.TryGetValue(run.ConversationId, out var current) || !ReferenceEquals(current, run) || run.Stopping) return;
            _runs.TryRemove(run.ConversationId, out _);
            await _repository.FinishRunAsync(run.Id, state, reason, error?.GetType().Name);
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
        foreach (var run in runs) await _repository.FinishRunAsync(run.Id, "interrupted", "interrupted");
        _runs.Clear();
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }
}
