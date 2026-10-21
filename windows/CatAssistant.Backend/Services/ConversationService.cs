using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services;

public sealed class ConversationService : IAsyncDisposable
{
    // The streamed row is only a crash checkpoint; the completion, failure and interrupt paths all store the full text.
    private static readonly TimeSpan StreamPersistInterval = TimeSpan.FromMilliseconds(250);
    private const int TitleAttemptCount = 2;
    private static readonly TimeSpan TitleRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TitleRetryDelay = TimeSpan.FromSeconds(2);

    private sealed class ActiveRun(string id, string conversationId, string triggerMessageId, string assistantMessageId, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string ConversationId { get; } = conversationId;
        public string TriggerMessageId { get; } = triggerMessageId;
        public string AssistantMessageId { get; } = assistantMessageId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public StringBuilder Content { get; } = new();
        public object ContentLock { get; } = new();
        public DateTimeOffset? LastContentPersistedAt { get; set; }
        public HashSet<string> WorkRequestToolCalls { get; } = new(StringComparer.Ordinal);
    }

    private readonly SqliteRepository _repository;
    private readonly IChatProvider _provider;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _apiKey;
    private readonly CancellationToken _lifetime;
    private readonly WorkWorkspaceService _workspaces;
    private readonly WorkAgentService? _workAgent;
    private readonly WorkApprovalService? _workApprovals;
    private readonly ConversationContextManager _contextManager;
    private readonly HistorySearchService _historySearch;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workRequestGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _titlesInFlight = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private string? _desktopActiveConversationId;
    private string? _deviceActiveConversationId;
    private bool _deviceFollowDesktop = true;
    private bool _devicePendingNewConversation;

    public ConversationService(
        SqliteRepository repository,
        IChatProvider provider,
        Func<AppSettings> settings,
        Func<string> apiKey,
        CancellationToken lifetime,
        WorkWorkspaceService? workspaces = null,
        WorkAgentService? workAgent = null,
        WorkApprovalService? workApprovals = null)
    {
        _repository = repository;
        _provider = provider;
        _settings = settings;
        _apiKey = apiKey;
        _lifetime = lifetime;
        _workspaces = workspaces ?? new WorkWorkspaceService();
        _workAgent = workAgent;
        _workApprovals = workApprovals;
        _contextManager = new ConversationContextManager(repository, provider, apiKey);
        _historySearch = new HistorySearchService(repository);
        if (_workAgent is not null) _workAgent.Changed += OnWorkChanged;
        if (_workApprovals is not null) _workApprovals.Changed += OnWorkChanged;
    }

    public event EventHandler<ConversationEvent>? Changed;

    public string? DesktopActiveConversationId { get { lock (_stateLock) return _desktopActiveConversationId; } }
    public string? DeviceActiveConversationId { get { lock (_stateLock) return _deviceActiveConversationId; } }
    public bool DeviceFollowDesktop { get { lock (_stateLock) return _deviceFollowDesktop; } }

    public async Task InitializeAsync()
    {
        var conversations = await _repository.ListConversationsAsync();
        foreach (var workConversation in conversations.Where(item => item.Mode == "work"))
            await _workspaces.EnsureAsync(workConversation.Id, _lifetime);
        if (conversations.Count == 0)
        {
            var created = await _repository.CreateConversationAsync();
            conversations = [created];
        }
        var savedDesktop = await _repository.GetAppStateAsync("desktop_active_conversation");
        var savedDevice = await _repository.GetAppStateAsync("device_active_conversation");
        var savedFollow = await _repository.GetAppStateAsync("device_follow_desktop");
        var savedPending = await _repository.GetAppStateAsync("device_pending_new_conversation");
        var ids = conversations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        lock (_stateLock)
        {
            _desktopActiveConversationId = savedDesktop is not null && ids.Contains(savedDesktop) ? savedDesktop : conversations[0].Id;
            _devicePendingNewConversation = savedPending == "1";
            _deviceFollowDesktop = !_devicePendingNewConversation && (savedFollow is null || savedFollow == "1");
            if (_devicePendingNewConversation) _deviceActiveConversationId = null;
            else if (_deviceFollowDesktop) _deviceActiveConversationId = _desktopActiveConversationId;
            else _deviceActiveConversationId = savedDevice is not null && ids.Contains(savedDevice) ? savedDevice : _desktopActiveConversationId;
        }
        await PersistBindingsAsync();
        foreach (var request in await _repository.ListWorkRequestsByStateAsync("approved"))
            await ResumeApprovedWorkRequestAsync(request.Id);
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => _repository.ListConversationsAsync();

    public async Task<ConversationDetail> GetAsync(string id)
    {
        var detail = await _repository.GetConversationDetailAsync(id, ActiveRunId(id));
        return detail ?? throw new KeyNotFoundException("对话不存在。");
    }

    public async Task<ConversationSummary> CreateAsync(
        string mode = "chat",
        bool activateDesktop = true,
        string? originConversationId = null,
        string? title = null,
        string titleSource = "fallback")
    {
        if (mode is not ("chat" or "work")) throw new ArgumentOutOfRangeException(nameof(mode));
        var created = await _repository.CreateConversationAsync(mode, originConversationId, title, titleSource);
        if (mode == "work") await _workspaces.EnsureAsync(created.Id, _lifetime);
        if (activateDesktop) await ActivateDesktopAsync(created.Id);
        Publish(new ConversationEvent("conversation.created", created.Id, Conversation: created));
        return created;
    }

    public async Task<ConversationDetail> StartAsync(string mode, string text, string source)
    {
        if (mode == "work" && _workAgent is null) throw new InvalidOperationException("Work Runtime 尚未启用。");
        var created = await CreateAsync(mode, activateDesktop: true);
        return await SendAsync(created.Id, text, source);
    }

    public async Task<WorkWorkspacePaths> GetWorkWorkspaceAsync(string conversationId)
    {
        var conversation = await _repository.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("对话不存在。");
        if (conversation.Mode != "work") throw new InvalidOperationException("Chat 对话没有 Work 工作区。");
        return await _workspaces.EnsureAsync(conversationId, _lifetime);
    }

    public async Task<string> CopyWorkInputAsync(string conversationId, string sourcePath)
    {
        await GetWorkWorkspaceAsync(conversationId);
        return await _workspaces.CopyInputAsync(conversationId, sourcePath, _lifetime);
    }

    public Task<IReadOnlyList<WorkApprovalRecord>> ListPendingWorkApprovalsAsync(string conversationId) =>
        _workApprovals?.ListPendingAsync(conversationId) ?? Task.FromResult<IReadOnlyList<WorkApprovalRecord>>([]);

    public Task ResolveWorkApprovalAsync(string conversationId, string approvalId, bool approved) =>
        _workApprovals?.ResolveAsync(conversationId, approvalId, approved) ?? throw new InvalidOperationException("Work Runtime 尚未启用。");

    public Task<IReadOnlyList<WorkToolCallRecord>> ListWorkToolCallsAsync(string conversationId) =>
        _repository.ListWorkToolCallsAsync(conversationId);

    public Task<IReadOnlyList<WorkArtifactRecord>> ListWorkArtifactsAsync(string conversationId) =>
        _repository.ListWorkArtifactsAsync(conversationId);

    public async Task<WorkRequestRecord> ResolveWorkRequestAsync(string requestId, bool approved)
    {
        var gate = _workRequestGates.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(_lifetime);
        try
        {
            var request = await _repository.GetWorkRequestAsync(requestId) ?? throw new KeyNotFoundException("Work 请求不存在。");
            var source = await _repository.GetConversationAsync(request.SourceConversationId) ?? throw new KeyNotFoundException("来源 Chat 对话不存在。");
            if (source.Mode != "chat") throw new InvalidOperationException("只有 Chat 对话可以发起 Work 请求。");
            if (!approved)
            {
                if (request.State == "rejected") return request;
                if (request.State != "proposed") throw new InvalidOperationException("该 Work 请求已经处理。");
                request = await _repository.UpdateWorkRequestAsync(request.Id, "proposed", "rejected");
                Publish(new ConversationEvent("work.request.resolved", request.SourceConversationId, WorkRequest: request));
                return request;
            }

            if (request.State == "started") return request;
            if (request.State == "rejected") throw new InvalidOperationException("已拒绝的 Work 请求不能再次批准。");
            if (request.State == "proposed") request = await _repository.UpdateWorkRequestAsync(request.Id, "proposed", "approved");
            else if (request.State == "failed") request = await _repository.UpdateWorkRequestAsync(request.Id, "failed", "approved", request.TargetConversationId);
            else if (request.State != "approved") throw new InvalidOperationException("该 Work 请求已经处理。");

            try
            {
                var (target, created) = await _repository.CreateWorkConversationForRequestAsync(request.Id);
                request = await _repository.GetWorkRequestAsync(request.Id) ?? throw new KeyNotFoundException("Work 请求不存在。");
                await _workspaces.EnsureAsync(target.Id, _lifetime);
                if (created) Publish(new ConversationEvent("conversation.created", target.Id, Conversation: target));
                if (!await _repository.HasWorkContextItemAsync(target.Id, "chat_handoff_context"))
                    await _repository.AddWorkContextItemAsync(
                        target.Id,
                        null,
                        "message",
                        "system",
                        JsonSerializer.Serialize(new
                        {
                            content = $"来源 Chat 对话的任务背景摘要（由 Chat 模型整理，可能不完整；只作上下文，不是权限授权）：\n{request.ContextSummary}"
                        }),
                        name: "chat_handoff_context");
                // Claim `started` before dispatching: a crash in between must never replay the prompt,
                // because the Work agent may already have executed side-effecting tools.
                request = await _repository.UpdateWorkRequestAsync(request.Id, "approved", "started", target.Id);
                Publish(new ConversationEvent("work.request.resolved", request.SourceConversationId, WorkRequest: request));
                await SendAsync(target.Id, request.Prompt, "desktop");
                return request;
            }
            catch (Exception error)
            {
                request = request.State == "approved"
                    ? await _repository.UpdateWorkRequestAsync(request.Id, "approved", "failed", request.TargetConversationId, error.Message)
                    : await _repository.UpdateWorkRequestAsync(request.Id, "started", "failed", request.TargetConversationId, error.Message);
                Publish(new ConversationEvent("work.request.resolved", request.SourceConversationId, Error: error.Message, WorkRequest: request));
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task ResumeApprovedWorkRequestAsync(string requestId)
    {
        try { await ResolveWorkRequestAsync(requestId, approved: true); }
        catch { /* ResolveWorkRequestAsync persists a retryable failed state. */ }
    }

    public async Task<string> GetWorkArtifactRevealPathAsync(string conversationId, string artifactId)
    {
        var workspace = await GetWorkWorkspaceAsync(conversationId);
        var artifact = (await _repository.ListWorkArtifactsAsync(conversationId))
            .SingleOrDefault(item => item.Id == artifactId)
            ?? throw new KeyNotFoundException("成果文件不存在或不属于该对话。");
        var path = Path.GetFullPath(artifact.Path);
        WindowsSandboxController.EnsureContained(workspace.Outputs, path);
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("成果文件不存在或不是普通文件。", path);
        return path;
    }

    // The Card opens a conversation only when its first message arrives, so a long press that is never
    // followed by speech leaves no empty conversation behind — same deferral the desktop composer uses.
    public async Task DetachDeviceForNewConversationAsync()
    {
        lock (_stateLock)
        {
            _deviceActiveConversationId = null;
            _deviceFollowDesktop = false;
            _devicePendingNewConversation = true;
        }
        await PersistBindingsAsync();
    }

    public async Task<ConversationDetail> StartForDeviceAsync(string text)
    {
        var created = await CreateAsync("chat", activateDesktop: false);
        lock (_stateLock)
        {
            _deviceActiveConversationId = created.Id;
            _deviceFollowDesktop = false;
            _devicePendingNewConversation = false;
        }
        await PersistBindingsAsync();
        return await SendAsync(created.Id, text, "device");
    }

    public async Task ActivateDesktopAsync(string id)
    {
        var conversation = await _repository.GetConversationAsync(id) ?? throw new KeyNotFoundException("对话不存在。");
        lock (_stateLock)
        {
            _desktopActiveConversationId = id;
            if (_deviceFollowDesktop && conversation.Mode == "chat") _deviceActiveConversationId = id;
        }
        await PersistBindingsAsync();
        Publish(new ConversationEvent("conversation.activated", id));
    }

    public async Task FollowDesktopAsync()
    {
        string id;
        lock (_stateLock) id = _desktopActiveConversationId ?? throw new InvalidOperationException("没有可跟随的桌面对话。");
        var conversation = await _repository.GetConversationAsync(id) ?? throw new KeyNotFoundException("对话不存在。");
        if (conversation.Mode != "chat") throw new InvalidOperationException("AI 卡片只能跟随 Chat 对话。");
        lock (_stateLock)
        {
            _deviceActiveConversationId = id;
            _deviceFollowDesktop = true;
            _devicePendingNewConversation = false;
        }
        await PersistBindingsAsync();
        Publish(new ConversationEvent("device.binding", id));
    }

    public async Task RenameAsync(string id, string title)
    {
        await _repository.RenameConversationAsync(id, title, "user");
        var conversation = await _repository.GetConversationAsync(id);
        Publish(new ConversationEvent("conversation.updated", id, Conversation: conversation));
    }

    public async Task SetPinnedAsync(string id, bool pinned)
    {
        await _repository.SetConversationPinnedAsync(id, pinned);
        var conversation = await _repository.GetConversationAsync(id);
        Publish(new ConversationEvent("conversation.updated", id, Conversation: conversation));
    }

    public async Task DeleteAsync(string id)
    {
        await CancelAsync(id);
        await _repository.DeleteConversationAsync(id);
        var remaining = await _repository.ListConversationsAsync();
        if (remaining.Count == 0) remaining = [await _repository.CreateConversationAsync()];
        lock (_stateLock)
        {
            if (_desktopActiveConversationId == id) _desktopActiveConversationId = remaining[0].Id;
            if (_deviceActiveConversationId == id) _deviceActiveConversationId = _desktopActiveConversationId;
            if (_deviceFollowDesktop) _deviceActiveConversationId = _desktopActiveConversationId;
        }
        await PersistBindingsAsync();
        Publish(new ConversationEvent("conversation.deleted", id));
    }

    public async Task<ConversationDetail> SendAsync(string conversationId, string text, string source)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("消息不能为空。", nameof(text));
        if (source is not ("desktop" or "device")) throw new ArgumentOutOfRangeException(nameof(source));
        var conversation = await _repository.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("对话不存在。");
        if (conversation.Mode == "work")
        {
            if (_workAgent is null) throw new InvalidOperationException("Work Runtime 尚未启用。");
            if (source != "desktop") throw new InvalidOperationException("Work 第一版只接受桌面消息。");
            if (IsUntitled(conversation))
            {
                await _repository.RenameConversationAsync(conversationId, SqliteRepository.FallbackTitle(text), "fallback");
                Publish(new ConversationEvent("conversation.updated", conversationId, Conversation: await _repository.GetConversationAsync(conversationId)));
            }
            var detail = await _workAgent.SendAsync(conversationId, text, source);
            // A Work conversation is titled from its request alone; the answer lands minutes later and the
            // sidebar cannot stay on a truncated prompt that long.
            ScheduleTitleGeneration(conversationId);
            return detail;
        }
        if (conversation.Mode != "chat") throw new InvalidOperationException("该对话不使用 Chat Runtime。");
        var gate = Gate(conversationId);
        ActiveRun run;
        await gate.WaitAsync(_lifetime);
        try
        {
            await InterruptLockedAsync(conversationId);
            var user = await _repository.AddMessageAsync(conversationId, "user", source, "complete", text);
            if (IsUntitled(conversation))
            {
                await _repository.RenameConversationAsync(conversationId, SqliteRepository.FallbackTitle(text), "fallback");
                conversation = (await _repository.GetConversationAsync(conversationId))!;
            }
            var runId = Guid.NewGuid().ToString("N");
            await _repository.CreateRunAsync(runId, conversationId, user.Id);
            var assistant = await _repository.AddMessageAsync(conversationId, "assistant", "runtime", "streaming", "", runId);
            await _repository.AttachAssistantToRunAsync(runId, assistant.Id);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            run = new ActiveRun(runId, conversationId, user.Id, assistant.Id, cancellation);
            _runs[conversationId] = run;
            Publish(new ConversationEvent("message.created", conversationId, user.Id, runId, Message: user, Conversation: conversation));
            Publish(new ConversationEvent("run.started", conversationId, assistant.Id, runId, Message: assistant, Status: "generating"));
        }
        finally { gate.Release(); }
        _ = ExecuteRunObservedAsync(run);
        return await GetAsync(conversationId);
    }

    public async Task CancelAsync(string conversationId)
    {
        if (_workAgent?.ActiveRunId(conversationId) is not null)
        {
            await _workAgent.StopAsync(conversationId);
            return;
        }
        var gate = Gate(conversationId);
        await gate.WaitAsync(_lifetime);
        try { await InterruptLockedAsync(conversationId); }
        finally { gate.Release(); }
    }

    public string? ActiveRunId(string conversationId) =>
        _workAgent?.ActiveRunId(conversationId) ?? (_runs.TryGetValue(conversationId, out var run) ? run.Id : null);

    private void OnWorkChanged(object? sender, ConversationEvent update) => Publish(update);

    private async Task ExecuteRunObservedAsync(ActiveRun run)
    {
        try { await ExecuteRunAsync(run); }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception error) { await FailRunAsync(run, error); }
        finally { run.Cancellation.Dispose(); }
    }

    private async Task ExecuteRunAsync(ActiveRun run)
    {
        var history = await _repository.LoadMessagesAsync(run.ConversationId);
        var citations = new Dictionary<int, CitationRecord>();
        var settings = _settings();
        var prepared = await _contextManager.PrepareChatAsync(
            run.ConversationId,
            history.Where(message => message.Id != run.AssistantMessageId).ToArray(),
            settings.ChatModel,
            settings.WebSearchEnabled,
            run.Cancellation.Token);
        var context = prepared.Messages;
        ModelUsage? usage = null;
        if (_provider is IChatToolLoopProvider toolLoop)
        {
            usage = await ExecuteChatToolLoopAsync(run, toolLoop, context, settings, citations);
        }
        else await foreach (var chunk in _provider.StreamAsync(context, settings.ChatModel, _apiKey(), settings.WebSearchEnabled, run.Cancellation.Token))
        {
            if (!IsCurrent(run)) return;
            if (!string.IsNullOrEmpty(chunk.Delta)) await AppendChatDeltaAsync(run, chunk.Delta);
            if (chunk.Citations is not null)
                foreach (var citation in chunk.Citations) citations.TryAdd(citation.Ordinal, citation);
            if (chunk.ToolCall is { } toolCall) await ProposeWorkRequestAsync(run, toolCall);
            if (chunk.Usage is not null) usage = chunk.Usage;
        }
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(_lifetime);
        try
        {
            if (!IsCurrent(run)) return;
            _runs.TryRemove(run.ConversationId, out _);
            string content;
            lock (run.ContentLock) content = run.Content.ToString();
            if (content.Length == 0 && run.WorkRequestToolCalls.Count > 0) await _repository.RemoveMessageIfEmptyAsync(run.AssistantMessageId);
            else await _repository.UpdateMessageAsync(run.AssistantMessageId, content, "complete");
            if (citations.Count > 0)
            {
                var normalized = citations.Values.OrderBy(item => item.Ordinal).Select(item => item with { MessageId = run.AssistantMessageId }).ToArray();
                await _repository.ReplaceCitationsAsync(run.AssistantMessageId, normalized);
            }
            await _repository.FinishRunAsync(run.Id, "completed", "stop");
            if (usage is not null) await _repository.SetRunUsageAsync(run.Id, usage);
            await _repository.RecordRequestDiagnosticAsync(run.ConversationId, run.Id, "chat", settings.ChatModel, "completed", prepared.Budget, usage);
            var message = (await _repository.LoadMessagesAsync(run.ConversationId)).SingleOrDefault(item => item.Id == run.AssistantMessageId);
            if (message is not null) Publish(new ConversationEvent("message.completed", run.ConversationId, run.AssistantMessageId, run.Id, Message: message, Status: "completed"));
            else Publish(new ConversationEvent("run.completed", run.ConversationId, RunId: run.Id, Status: "completed"));
            ScheduleTitleGeneration(run.ConversationId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelUsage?> ExecuteChatToolLoopAsync(
        ActiveRun run,
        IChatToolLoopProvider provider,
        IReadOnlyList<ConversationMessage> context,
        AppSettings settings,
        Dictionary<int, CitationRecord> citations)
    {
        var messages = context.Select(item => new ChatModelMessage(item.Role, item.Content)).ToList();
        ModelUsage? usage = null;
        for (var turn = 0; turn < 4; turn++)
        {
            var calls = new List<ChatToolCall>();
            var turnContent = new StringBuilder();
            await foreach (var chunk in provider.StreamTurnAsync(messages, settings.ChatModel, _apiKey(), settings.WebSearchEnabled, run.Cancellation.Token))
            {
                if (!IsCurrent(run)) return usage;
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    turnContent.Append(chunk.Delta);
                    await AppendChatDeltaAsync(run, chunk.Delta);
                }
                if (chunk.Citations is not null)
                    foreach (var citation in chunk.Citations) citations.TryAdd(citation.Ordinal, citation);
                if (chunk.ToolCall is not null) calls.Add(chunk.ToolCall);
                if (chunk.Usage is not null) usage = chunk.Usage;
            }
            if (calls.Count == 0) return usage;

            messages.Add(new ChatModelMessage("assistant", turnContent.Length == 0 ? null : turnContent.ToString(), calls));
            foreach (var call in calls)
            {
                string output;
                if (call.Name == "search_conversation_history") output = await ExecuteHistorySearchToolAsync(run.ConversationId, call.ArgumentsJson, run.Cancellation.Token);
                else if (call.Name == "request_work")
                {
                    var proposed = await ProposeWorkRequestAsync(run, call);
                    output = JsonSerializer.Serialize(proposed
                        ? new { proposed = true, requiresUserApproval = true, error = (string?)null }
                        : new { proposed = false, requiresUserApproval = true, error = (string?)"invalid_or_duplicate_proposal" });
                }
                else output = JsonSerializer.Serialize(new { error = "unknown_tool" });
                messages.Add(new ChatModelMessage("tool", output, ToolCallId: call.ToolCallId));
            }
        }
        throw new InvalidOperationException("Chat 工具调用轮次过多，已停止以避免循环。");
    }

    private async Task<string> ExecuteHistorySearchToolAsync(string conversationId, string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            var root = arguments.RootElement;
            var messageId = root.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var full = root.TryGetProperty("full", out var fullValue) && fullValue.ValueKind == JsonValueKind.True;
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                var message = await _historySearch.GetMessageAsync(conversationId, messageId!);
                if (message is null) return JsonSerializer.Serialize(new { found = false, messageId });
                var offset = root.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var parsedOffset)
                    ? Math.Clamp(parsedOffset, 0, message.Content.Length)
                    : 0;
                var requested = root.TryGetProperty("maxCharacters", out var maxValue) && maxValue.TryGetInt32(out var parsedMaximum)
                    ? Math.Clamp(parsedMaximum, 200, 20_000)
                    : full ? 12_000 : 2_000;
                var length = Math.Min(requested, message.Content.Length - offset);
                var content = message.Content.Substring(offset, length);
                var nextOffset = offset + length < message.Content.Length ? offset + length : (int?)null;
                return JsonSerializer.Serialize(new { found = true, message.Id, message.Sequence, message.CreatedAt, message.Role, message.Status, content, offset, nextOffset, totalCharacters = message.Content.Length, truncated = nextOffset is not null });
            }

            var query = root.TryGetProperty("query", out var queryValue) && queryValue.ValueKind == JsonValueKind.String ? queryValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(query)) return JsonSerializer.Serialize(new { error = "query_or_message_id_required" });
            var role = root.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString() : null;
            var topK = root.TryGetProperty("topK", out var topValue) && topValue.TryGetInt32(out var parsed) ? Math.Clamp(parsed, 1, 20) : 8;
            var from = ParseOptionalTimestamp(root, "from");
            var to = ParseOptionalTimestamp(root, "to");
            var results = await _historySearch.SearchAsync(conversationId, query!, topK, role, from, to, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                query,
                count = results.Count,
                indexStatus = results.Any(item => item.MatchSource.Contains("vector", StringComparison.Ordinal)) ? "hybrid_ready" : "keyword_fallback_or_no_vector_hit",
                results = results.Select(item => new { item.MessageId, item.Sequence, item.CreatedAt, item.Role, item.Status, item.Snippet, item.Score, item.MatchSource })
            });
        }
        catch (JsonException error) { return JsonSerializer.Serialize(new { error = "invalid_arguments", detail = error.Message }); }
        catch (ArgumentException error) { return JsonSerializer.Serialize(new { error = "invalid_arguments", detail = error.Message }); }
    }

    private static DateTimeOffset? ParseOptionalTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), out var parsed))
            throw new ArgumentException($"{name} 必须是有效的 ISO-8601 时间。");
        return parsed;
    }

    private async Task AppendChatDeltaAsync(ActiveRun run, string delta)
    {
        string? checkpoint = null;
        lock (run.ContentLock)
        {
            run.Content.Append(delta);
            if (run.LastContentPersistedAt is null || DateTimeOffset.UtcNow - run.LastContentPersistedAt >= StreamPersistInterval)
            {
                run.LastContentPersistedAt = DateTimeOffset.UtcNow;
                checkpoint = run.Content.ToString();
            }
        }
        if (checkpoint is not null) await _repository.UpdateMessageAsync(run.AssistantMessageId, checkpoint, "streaming");
        Publish(new ConversationEvent("message.delta", run.ConversationId, run.AssistantMessageId, run.Id, delta));
    }

    private async Task FailRunAsync(ActiveRun run, Exception error)
    {
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!IsCurrent(run)) return;
            _runs.TryRemove(run.ConversationId, out _);
            string content;
            lock (run.ContentLock) content = run.Content.ToString();
            await _repository.UpdateMessageAsync(run.AssistantMessageId, content, "failed");
            await _repository.FinishRunAsync(run.Id, "failed", "error", ErrorCode(error));
            Publish(new ConversationEvent("run.failed", run.ConversationId, run.AssistantMessageId, run.Id, Status: "failed", Error: error.Message));
            ScheduleTitleGeneration(run.ConversationId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ProposeWorkRequestAsync(ActiveRun run, ChatToolCall toolCall)
    {
        if (toolCall.Name != "request_work" || !IsCurrent(run)) return false;
        if (!run.WorkRequestToolCalls.Add(toolCall.ToolCallId)) return true;
        try
        {
            var arguments = ParseWorkRequestArguments(toolCall.ArgumentsJson);
            var request = await _repository.CreateWorkRequestAsync(
                run.ConversationId,
                run.TriggerMessageId,
                run.Id,
                toolCall.ToolCallId,
                arguments.Title,
                arguments.Prompt,
                arguments.ContextSummary);
            if (request.State == "proposed")
                Publish(new ConversationEvent("work.request.proposed", run.ConversationId, run.TriggerMessageId, run.Id, WorkRequest: request));
            return request.State == "proposed";
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            // A malformed proposal must not fail the whole chat answer: the model still owes the user text.
            await ProposeFailedWorkRequestAsync(run, toolCall.ToolCallId, error.Message);
            return false;
        }
    }

    private async Task ProposeFailedWorkRequestAsync(ActiveRun run, string toolCallId, string errorMessage)
    {
        if (!IsCurrent(run)) return;
        var request = await _repository.CreateFailedWorkRequestAsync(run.ConversationId, run.TriggerMessageId, run.Id, toolCallId, errorMessage);
        Publish(new ConversationEvent("work.request.proposed", run.ConversationId, run.TriggerMessageId, run.Id, WorkRequest: request));
    }

    private static WorkRequestArguments ParseWorkRequestArguments(string argumentsJson)
    {
        var arguments = JsonSerializer.Deserialize<WorkRequestArguments>(argumentsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (string.IsNullOrWhiteSpace(arguments?.Title)) throw new ArgumentException("模型返回的 Work 请求缺少标题。");
        if (string.IsNullOrWhiteSpace(arguments.Prompt)) throw new ArgumentException("模型返回的 Work 请求缺少执行提示词。");
        if (string.IsNullOrWhiteSpace(arguments.ContextSummary)) throw new ArgumentException("模型返回的 Work 请求缺少上下文摘要。");
        return arguments!;
    }

    private sealed record WorkRequestArguments(string Title, string Prompt, string ContextSummary);

    private async Task InterruptLockedAsync(string conversationId)
    {
        if (!_runs.TryRemove(conversationId, out var previous)) return;
        previous.Cancellation.Cancel();
        string content;
        lock (previous.ContentLock) content = previous.Content.ToString();
        if (content.Length == 0) await _repository.RemoveMessageIfEmptyAsync(previous.AssistantMessageId);
        else await _repository.UpdateMessageAsync(previous.AssistantMessageId, content, "interrupted");
        await _repository.FinishRunAsync(previous.Id, "interrupted", "interrupted");
        Publish(new ConversationEvent("message.interrupted", conversationId, previous.AssistantMessageId, previous.Id, Status: "interrupted"));
        // A stopped first answer is still answer enough to name the conversation; without this the title
        // would wait for a later turn that may never come.
        ScheduleTitleGeneration(conversationId);
    }

    private static bool IsUntitled(ConversationSummary conversation) =>
        conversation.TitleSource == "fallback" && conversation.Title == SqliteRepository.UntitledTitle;

    // Fire-and-forget while the app runs, but tracked so shutdown drains the request instead of abandoning a
    // conversation on its truncated title.
    private void ScheduleTitleGeneration(string conversationId)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_titlesInFlight.TryAdd(conversationId, settled.Task)) return;
        _ = Task.Run(async () =>
        {
            try { await GenerateTitleAsync(conversationId); }
            finally { _titlesInFlight.TryRemove(conversationId, out _); settled.SetResult(); }
        });
    }

    private async Task GenerateTitleAsync(string conversationId)
    {
        try
        {
            var conversation = await _repository.GetConversationAsync(conversationId);
            if (conversation is null || conversation.TitleSource != "fallback") return;
            var history = await _repository.LoadMessagesAsync(conversationId);
            var userMessage = history.FirstOrDefault(item => item.Role == "user");
            if (userMessage is null) return;
            var assistantMessage = history
                .Where(item => item.Role == "assistant")
                .Select(item => item with { Content = CitationMarkers.Strip(item.Content) })
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Content));
            var settings = _settings();
            var apiKey = _apiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return;
            string? title = null;
            string? failure = null;
            for (var attempt = 1; attempt <= TitleAttemptCount && title is null; attempt++)
            {
                if (attempt > 1) await Task.Delay(TitleRetryDelay * (attempt - 1), _lifetime);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                    timeout.CancelAfter(TitleRequestTimeout);
                    title = NormalizeGeneratedTitle(await _provider.GenerateTitleAsync(
                        userMessage.Content, assistantMessage?.Content ?? "", settings.ChatModel, apiKey, timeout.Token));
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error) { failure = error.Message; }
            }
            if (title is null)
            {
                // Stay on 'fallback': the next completed turn retries this conversation.
                Console.Error.WriteLine($"[title] conversation {conversationId} left untitled after {TitleAttemptCount} attempts: {failure}");
                return;
            }
            if (!await _repository.TrySetGeneratedTitleAsync(conversationId, title)) return;
            conversation = await _repository.GetConversationAsync(conversationId);
            Publish(new ConversationEvent("conversation.updated", conversationId, Conversation: conversation));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine($"[title] conversation {conversationId}: {error.Message}"); }
    }

    internal static string? NormalizeGeneratedTitle(string value)
    {
        var title = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        title = title.Trim('"', '\'', '“', '”', '‘', '’', '#', '*', '`', ' ');
        foreach (var prefix in new[] { "标题：", "标题:", "Title:", "Title：" })
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) title = title[prefix.Length..].Trim();
        title = title.Trim('"', '\'', '“', '”', '‘', '’', '#', '*', '`', ' ', '。');
        if (title.Length == 0) return null;
        return title.Length <= 36 ? title : title[..36];
    }

    private bool IsCurrent(ActiveRun run) => _runs.TryGetValue(run.ConversationId, out var current) && ReferenceEquals(current, run);
    private SemaphoreSlim Gate(string conversationId) => _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
    private void Publish(ConversationEvent value) => Changed?.Invoke(this, value);

    private async Task PersistBindingsAsync()
    {
        string? desktop;
        string? device;
        bool follow;
        bool pending;
        lock (_stateLock)
        {
            desktop = _desktopActiveConversationId;
            device = _deviceActiveConversationId;
            follow = _deviceFollowDesktop;
            pending = _devicePendingNewConversation;
        }
        await _repository.SetAppStateAsync("desktop_active_conversation", desktop);
        await _repository.SetAppStateAsync("device_active_conversation", device);
        await _repository.SetAppStateAsync("device_follow_desktop", follow ? "1" : "0");
        await _repository.SetAppStateAsync("device_pending_new_conversation", pending ? "1" : null);
    }

    private static string ErrorCode(Exception error) => error switch
    {
        CloudException cloud => cloud.Kind.ToString(),
        HttpRequestException => "network",
        TaskCanceledException => "timeout",
        _ => "unknown"
    };

    public async ValueTask DisposeAsync()
    {
        if (_workAgent is not null)
        {
            _workAgent.Changed -= OnWorkChanged;
            await _workAgent.DisposeAsync();
        }
        if (_workApprovals is not null) _workApprovals.Changed -= OnWorkChanged;
        foreach (var run in _runs.Values) run.Cancellation.Cancel();
        await Task.WhenAll(_runs.Values.Select(async run =>
        {
            string content;
            lock (run.ContentLock) content = run.Content.ToString();
            if (content.Length == 0) await _repository.RemoveMessageIfEmptyAsync(run.AssistantMessageId);
            else await _repository.UpdateMessageAsync(run.AssistantMessageId, content, "interrupted");
            await _repository.FinishRunAsync(run.Id, "interrupted", "interrupted");
        }));
        _runs.Clear();
        await Task.WhenAll(_titlesInFlight.Values);
        _titlesInFlight.Clear();
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
        foreach (var gate in _workRequestGates.Values) gate.Dispose();
        _workRequestGates.Clear();
        _workspaces.Dispose();
    }
}
