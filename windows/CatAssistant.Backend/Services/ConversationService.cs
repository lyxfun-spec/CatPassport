using System.Collections.Concurrent;
using System.Text;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed class ConversationService : IAsyncDisposable
{
    private sealed class ActiveRun(string id, string conversationId, string assistantMessageId, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string ConversationId { get; } = conversationId;
        public string AssistantMessageId { get; } = assistantMessageId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public StringBuilder Content { get; } = new();
        public object ContentLock { get; } = new();
    }

    private readonly SqliteRepository _repository;
    private readonly IChatProvider _provider;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _apiKey;
    private readonly CancellationToken _lifetime;
    private readonly WorkWorkspaceService _workspaces;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    private readonly object _stateLock = new();
    private string? _desktopActiveConversationId;
    private string? _deviceActiveConversationId;
    private bool _deviceFollowDesktop = true;

    public ConversationService(
        SqliteRepository repository,
        IChatProvider provider,
        Func<AppSettings> settings,
        Func<string> apiKey,
        CancellationToken lifetime,
        WorkWorkspaceService? workspaces = null)
    {
        _repository = repository;
        _provider = provider;
        _settings = settings;
        _apiKey = apiKey;
        _lifetime = lifetime;
        _workspaces = workspaces ?? new WorkWorkspaceService();
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
        var ids = conversations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        lock (_stateLock)
        {
            _desktopActiveConversationId = savedDesktop is not null && ids.Contains(savedDesktop) ? savedDesktop : conversations[0].Id;
            _deviceFollowDesktop = savedFollow is null || savedFollow == "1";
            _deviceActiveConversationId = _deviceFollowDesktop
                ? _desktopActiveConversationId
                : savedDevice is not null && ids.Contains(savedDevice) ? savedDevice : _desktopActiveConversationId;
        }
        await PersistBindingsAsync();
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => _repository.ListConversationsAsync();

    public async Task<ConversationDetail> GetAsync(string id)
    {
        var detail = await _repository.GetConversationDetailAsync(id, ActiveRunId(id));
        return detail ?? throw new KeyNotFoundException("对话不存在。");
    }

    public async Task<ConversationSummary> CreateAsync(string mode = "chat", bool activateDesktop = true)
    {
        if (mode is not ("chat" or "work")) throw new ArgumentOutOfRangeException(nameof(mode));
        var created = await _repository.CreateConversationAsync(mode);
        if (mode == "work") await _workspaces.EnsureAsync(created.Id, _lifetime);
        if (activateDesktop) await ActivateDesktopAsync(created.Id);
        Publish(new ConversationEvent("conversation.created", created.Id, Conversation: created));
        return created;
    }

    public async Task<ConversationDetail> StartAsync(string mode, string text, string source)
    {
        if (mode == "work") throw new InvalidOperationException("Work Runtime 尚未启用。");
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

    public async Task<ConversationSummary> CreateForDeviceAsync()
    {
        var created = await _repository.CreateConversationAsync("chat");
        lock (_stateLock)
        {
            _deviceActiveConversationId = created.Id;
            _deviceFollowDesktop = false;
        }
        await PersistBindingsAsync();
        Publish(new ConversationEvent("conversation.created", created.Id, Conversation: created));
        return created;
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
        if (conversation.Mode != "chat") throw new InvalidOperationException("该对话不使用 Chat Runtime。");
        var gate = Gate(conversationId);
        ActiveRun run;
        await gate.WaitAsync(_lifetime);
        try
        {
            await InterruptLockedAsync(conversationId);
            var user = await _repository.AddMessageAsync(conversationId, "user", source, "complete", text);
            if (conversation.TitleSource == "fallback")
            {
                await _repository.RenameConversationAsync(conversationId, SqliteRepository.FallbackTitle(text), "generated");
                conversation = (await _repository.GetConversationAsync(conversationId))!;
            }
            var runId = Guid.NewGuid().ToString("N");
            await _repository.CreateRunAsync(runId, conversationId, user.Id);
            var assistant = await _repository.AddMessageAsync(conversationId, "assistant", "runtime", "streaming", "", runId);
            await _repository.AttachAssistantToRunAsync(runId, assistant.Id);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            run = new ActiveRun(runId, conversationId, assistant.Id, cancellation);
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
        var gate = Gate(conversationId);
        await gate.WaitAsync(_lifetime);
        try { await InterruptLockedAsync(conversationId); }
        finally { gate.Release(); }
    }

    public string? ActiveRunId(string conversationId) => _runs.TryGetValue(conversationId, out var run) ? run.Id : null;

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
        var context = BuildContext(history.Where(message => message.Id != run.AssistantMessageId).ToArray());
        var citations = new Dictionary<int, CitationRecord>();
        var settings = _settings();
        await foreach (var chunk in _provider.StreamAsync(context, settings.ChatModel, _apiKey(), settings.WebSearchEnabled, run.Cancellation.Token))
        {
            if (!IsCurrent(run)) return;
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                string content;
                lock (run.ContentLock)
                {
                    run.Content.Append(chunk.Delta);
                    content = run.Content.ToString();
                }
                await _repository.UpdateMessageAsync(run.AssistantMessageId, content, "streaming");
                Publish(new ConversationEvent("message.delta", run.ConversationId, run.AssistantMessageId, run.Id, chunk.Delta));
            }
            if (chunk.Citations is not null)
                foreach (var citation in chunk.Citations) citations.TryAdd(citation.Ordinal, citation);
        }
        var gate = Gate(run.ConversationId);
        await gate.WaitAsync(_lifetime);
        try
        {
            if (!IsCurrent(run)) return;
            _runs.TryRemove(run.ConversationId, out _);
            string content;
            lock (run.ContentLock) content = run.Content.ToString();
            await _repository.UpdateMessageAsync(run.AssistantMessageId, content, "complete");
            if (citations.Count > 0)
            {
                var normalized = citations.Values.OrderBy(item => item.Ordinal).Select(item => item with { MessageId = run.AssistantMessageId }).ToArray();
                await _repository.ReplaceCitationsAsync(run.AssistantMessageId, normalized);
            }
            await _repository.FinishRunAsync(run.Id, "completed", "stop");
            var message = (await _repository.LoadMessagesAsync(run.ConversationId)).Single(item => item.Id == run.AssistantMessageId);
            Publish(new ConversationEvent("message.completed", run.ConversationId, run.AssistantMessageId, run.Id, Message: message, Status: "completed"));
            var userMessages = history.Where(item => item.Role == "user").Take(2).ToArray();
            if (userMessages.Length == 1 && !string.IsNullOrWhiteSpace(content))
                _ = GenerateTitleObservedAsync(run.ConversationId, userMessages[0].Content, CitationMarkers.Strip(content));
        }
        finally
        {
            gate.Release();
        }
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
        }
        finally
        {
            gate.Release();
        }
    }

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
    }

    private static IReadOnlyList<ConversationMessage> BuildContext(IReadOnlyList<ConversationMessage> messages)
    {
        const int characterBudget = 48_000;
        var selected = new List<ConversationMessage>();
        var used = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role is not ("user" or "assistant")) continue;
            if (message.Role == "assistant" && message.Status is not ("complete" or "interrupted")) continue;
            if (message.Role == "user" && message.Status != "complete") continue;
            if (selected.Count > 0 && used + message.Content.Length > characterBudget) break;
            selected.Add(message.Role == "assistant" ? message with { Content = CitationMarkers.Strip(message.Content) } : message);
            used += message.Content.Length;
        }
        selected.Reverse();
        return selected;
    }

    private async Task GenerateTitleObservedAsync(string conversationId, string userMessage, string assistantMessage)
    {
        try
        {
            var conversation = await _repository.GetConversationAsync(conversationId);
            if (conversation is null || conversation.TitleSource == "user") return;
            var settings = _settings();
            var candidate = await _provider.GenerateTitleAsync(userMessage, assistantMessage, settings.ChatModel, _apiKey(), _lifetime);
            var title = NormalizeGeneratedTitle(candidate);
            if (title is null || !await _repository.TrySetGeneratedTitleAsync(conversationId, title)) return;
            conversation = await _repository.GetConversationAsync(conversationId);
            Publish(new ConversationEvent("conversation.updated", conversationId, Conversation: conversation));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { }
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
        lock (_stateLock)
        {
            desktop = _desktopActiveConversationId;
            device = _deviceActiveConversationId;
            follow = _deviceFollowDesktop;
        }
        await _repository.SetAppStateAsync("desktop_active_conversation", desktop);
        await _repository.SetAppStateAsync("device_active_conversation", device);
        await _repository.SetAppStateAsync("device_follow_desktop", follow ? "1" : "0");
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
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
        _workspaces.Dispose();
    }
}
