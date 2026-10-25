using System.Collections.Concurrent;
using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WorkAgentServiceTests
{
    [Fact]
    public async Task TruncationRetriesWithoutExecutingOrReplayingDiscardedTools()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        var executions = 0;
        fixture.Tools.Register(new BlockingTool("write-probe", () => executions++, Task.CompletedTask));
        fixture.Provider.EnqueueStreamed(["截断的说明"], new WorkModelResponse("截断的说明",
            [new WorkModelToolCall("discarded", "write-probe", "{}")], OutputTruncated: true));
        fixture.Provider.Enqueue(new WorkModelResponse(null, [new WorkModelToolCall("valid", "write-probe", "{}") ]));
        fixture.Provider.Enqueue(new WorkModelResponse("完成", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, "写入文件", "desktop");
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executions);
        var recoveryContext = fixture.Provider.Contexts[1];
        Assert.Contains(recoveryContext, item => item.Name == "output_truncation_recovery" && item.Role == "system");
        Assert.DoesNotContain(recoveryContext, item => item.Kind is "tool_call" or "tool_result" || JsonContent(item) == "截断的说明");
        var messages = await fixture.Repository.LoadMessagesAsync(fixture.ConversationId);
        Assert.Contains(messages, message => message.Content == "截断的说明" && message.Status == "interrupted");
        Assert.DoesNotContain(fixture.Events, item => item.Type == "tool.failed");
    }

    [Fact]
    public async Task RepeatedTruncationStopsWithAnExplicitError()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        for (var index = 0; index < 3; index++)
            fixture.Provider.Enqueue(new WorkModelResponse(null, [], OutputTruncated: true));

        await fixture.Agent.SendAsync(fixture.ConversationId, "写入文件", "desktop");
        var error = await fixture.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("模型输出连续达到长度上限", error);
        Assert.Equal(3, fixture.Provider.Contexts.Count);
        Assert.Null(fixture.Agent.ActiveRunId(fixture.ConversationId));
        Assert.DoesNotContain(fixture.Events, item => item.Type == "run.completed" || item.Type == "tool.started");
    }

    [Fact]
    public async Task SuccessfulToolRoundResetsTheTruncationRetryBudget()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        var executions = 0;
        fixture.Tools.Register(new BlockingTool("probe", () => executions++, Task.CompletedTask));
        for (var batch = 0; batch < 2; batch++)
        {
            fixture.Provider.Enqueue(new WorkModelResponse(null, [], OutputTruncated: true));
            fixture.Provider.Enqueue(new WorkModelResponse(null, [], OutputTruncated: true));
            fixture.Provider.Enqueue(new WorkModelResponse(null, [new WorkModelToolCall($"valid-{batch}", "probe", "{}") ]));
        }
        fixture.Provider.Enqueue(new WorkModelResponse("完成", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, "写入多个文件", "desktop");
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, executions);
        Assert.Equal(7, fixture.Provider.Contexts.Count);
        Assert.DoesNotContain(fixture.Events, item => item.Type == "run.failed");
    }

    [Fact]
    public async Task ToolBatchRunsInParallelAndQueuedMessageEntersNextRoundInOrder()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Tools.Register(new BlockingTool("alpha", OnStarted, release.Task));
        fixture.Tools.Register(new BlockingTool("beta", OnStarted, release.Task));
        fixture.Provider.Enqueue(new WorkModelResponse(null,
        [
            new WorkModelToolCall("call-a", "alpha", "{\"value\":1}"),
            new WorkModelToolCall("call-b", "beta", "{\"value\":2}")
        ]));
        fixture.Provider.Enqueue(new WorkModelResponse("全部完成", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, "开始处理", "desktop");
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Agent.SendAsync(fixture.ConversationId, "改用浅色", "desktop");
        release.TrySetResult();
        await fixture.Provider.WaitForCallsAsync(2);
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondContext = fixture.Provider.Contexts[1];
        var results = secondContext.Where(item => item.Kind == "tool_result").ToArray();
        Assert.Equal(new[] { "call-a", "call-b" }, results.Select(item => item.ToolCallId));
        var queued = Assert.Single(secondContext, item => item.Kind == "message" && item.Role == "user" && JsonContent(item) == "改用浅色");
        Assert.True(queued.Ordinal > results.Max(item => item.Ordinal));
        Assert.Null(fixture.Agent.ActiveRunId(fixture.ConversationId));
        Assert.All(fixture.Provider.Models, model => Assert.Equal("qwen3.8-max", model));
        Assert.All(fixture.Provider.Environments, environment =>
        {
            Assert.Equal(fixture.ConversationId, Path.GetFileName(Path.GetDirectoryName(environment.InputsDirectory)));
            Assert.Equal(environment.WorkspaceRoot, environment.OutputsDirectory[..^"outputs".Length].TrimEnd(Path.DirectorySeparatorChar));
        });

        void OnStarted()
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
        }
    }

    [Fact]
    public async Task StopCancelsRunningToolsAndPreventsAnotherModelRound()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.Register(new CancellableTool(started, cancelled));
        fixture.Provider.Enqueue(new WorkModelResponse(null, [new WorkModelToolCall("call-stop", "wait", "{}") ]));

        await fixture.Agent.SendAsync(fixture.ConversationId, "开始", "desktop");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Agent.StopAsync(fixture.ConversationId);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Provider.Enqueue(new WorkModelResponse("已继续", []));
        await fixture.Agent.SendAsync(fixture.ConversationId, "继续", "desktop");
        await fixture.Provider.WaitForCallsAsync(2);
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(fixture.Agent.ActiveRunId(fixture.ConversationId));
        var resumed = fixture.Provider.Contexts[1];
        Assert.Contains(resumed, item => item.Kind == "tool_result" && item.ToolCallId == "call-stop");
        Assert.Contains(resumed, item => item.Kind == "message" && JsonContent(item) == "继续");
    }

    [Fact]
    public async Task LongMessageWithinTokenBudgetDoesNotForceANewConversation()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        fixture.Provider.Enqueue(new WorkModelResponse("已继续", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, new string('长', 120), "desktop");
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(fixture.Provider.Contexts);
        Assert.Contains(await fixture.Repository.LoadMessagesAsync(fixture.ConversationId), message => message.Content == "已继续");
    }

    [Fact]
    public async Task ModelTimeoutPersistsAVisibleFailureInsteadOfEndingSilently()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        fixture.Provider.EnqueueFailure(new TaskCanceledException("模型在 10 分钟内没有完成生成，本轮任务已中断。", new TimeoutException()));

        await fixture.Agent.SendAsync(fixture.ConversationId, "制作 Transformer 的 PPT", "desktop");
        var error = await fixture.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("模型在 10 分钟内没有完成生成，本轮任务已中断。", error);
        Assert.Null(fixture.Agent.ActiveRunId(fixture.ConversationId));
        var failure = Assert.Single(await fixture.Repository.LoadMessagesAsync(fixture.ConversationId), message => message.Role == "assistant");
        Assert.Equal("failed", failure.Status);
        Assert.Equal(error, failure.Content);
        Assert.All(await fixture.Repository.LoadWorkContextItemsAsync(fixture.ConversationId), item => Assert.NotEqual(failure.Content, JsonContent(item)));
    }

    [Fact]
    public async Task StreamedAnswerAccumulatesIntoOneMessageAndOneContextItem()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        fixture.Provider.EnqueueStreamed(["环境已确认：", "python-pptx 可用。"], new WorkModelResponse("环境已确认：python-pptx 可用。", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, "制作 PPT", "desktop");
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var answer = Assert.Single(await fixture.Repository.LoadMessagesAsync(fixture.ConversationId), message => message.Role == "assistant");
        Assert.Equal("complete", answer.Status);
        Assert.Equal("环境已确认：python-pptx 可用。", answer.Content);
        Assert.Single(fixture.Events, update => update.Type == "message.created" && update.MessageId == answer.Id);
        Assert.Equal(2, fixture.Events.Count(update => update.Type == "message.delta" && update.MessageId == answer.Id));
        var context = await fixture.Repository.LoadWorkContextItemsAsync(fixture.ConversationId);
        Assert.Single(context, item => item.Kind == "message" && item.Role == "assistant" && item.MessageId == answer.Id);
    }

    [Fact]
    public async Task StoppingDuringAStreamKeepsThePartialAnswer()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        fixture.Provider.EnqueueStreamThenHang(["大纲已经写好，"]);

        await fixture.Agent.SendAsync(fixture.ConversationId, "制作 PPT", "desktop");
        await WaitUntilAsync(() => fixture.Events.Any(update => update.Type == "message.created" && update.Message?.Role == "assistant"));
        await fixture.Agent.StopAsync(fixture.ConversationId);

        var partial = Assert.Single(await fixture.Repository.LoadMessagesAsync(fixture.ConversationId), message => message.Role == "assistant");
        Assert.Equal("interrupted", partial.Status);
        Assert.Equal("大纲已经写好，", partial.Content);
    }

    [Fact]
    public async Task DifferentConversationsRunConcurrentlyWithoutSharingWorkspaces()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync();
        var secondConversation = await fixture.Repository.CreateConversationAsync("work");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        fixture.Tools.Register(new BlockingTool("shared-wait", () =>
        {
            if (Interlocked.Increment(ref starts) == 2) bothStarted.TrySetResult();
        }, release.Task));
        fixture.Provider.Enqueue(new WorkModelResponse(null, [new WorkModelToolCall("call-a", "shared-wait", "{}") ]));
        fixture.Provider.Enqueue(new WorkModelResponse(null, [new WorkModelToolCall("call-b", "shared-wait", "{}") ]));
        fixture.Provider.Enqueue(new WorkModelResponse("完成", []));
        fixture.Provider.Enqueue(new WorkModelResponse("完成", []));

        await fixture.Agent.SendAsync(fixture.ConversationId, "任务 A", "desktop");
        await fixture.Agent.SendAsync(secondConversation.Id, "任务 B", "desktop");
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(fixture.Agent.ActiveRunId(fixture.ConversationId));
        Assert.NotNull(fixture.Agent.ActiveRunId(secondConversation.Id));

        release.TrySetResult();
        await fixture.Provider.WaitForCallsAsync(4);
        await WaitUntilAsync(() => fixture.Agent.ActiveRunId(fixture.ConversationId) is null && fixture.Agent.ActiveRunId(secondConversation.Id) is null);
        var firstWorkspace = await fixture.Workspaces.EnsureAsync(fixture.ConversationId);
        var secondWorkspace = await fixture.Workspaces.EnsureAsync(secondConversation.Id);
        Assert.NotEqual(firstWorkspace.Root, secondWorkspace.Root);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(20);
        Assert.True(condition());
    }

    private static string? JsonContent(WorkContextItem item)
    {
        using var json = JsonDocument.Parse(item.PayloadJson);
        return json.RootElement.TryGetProperty("content", out var content) ? content.GetString() : null;
    }

    private sealed class BlockingTool(string name, Action started, Task release) : IWorkTool
    {
        public WorkToolDefinition Definition { get; } = new(name, name, "{\"type\":\"object\"}");

        public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
        {
            started();
            await release.WaitAsync(cancellationToken);
            return WorkToolResult.Ok(new { tool = name });
        }
    }

    private sealed class CancellableTool(TaskCompletionSource started, TaskCompletionSource cancelled) : IWorkTool
    {
        public WorkToolDefinition Definition { get; } = new("wait", "wait", "{\"type\":\"object\"}");

        public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return WorkToolResult.Ok(new { });
        }
    }

    private sealed class ControlledWorkProvider : IWorkModelProvider
    {
        private readonly ConcurrentQueue<Func<Func<string, Task>, CancellationToken, Task<WorkModelResponse>>> _replies = new();
        private readonly object _lock = new();
        private readonly List<IReadOnlyList<WorkContextItem>> _contexts = [];
        private readonly List<string> _models = [];
        private readonly List<WorkEnvironmentInfo> _environments = [];
        private TaskCompletionSource _changed = Signal();

        public IReadOnlyList<IReadOnlyList<WorkContextItem>> Contexts { get { lock (_lock) return _contexts.ToArray(); } }
        public IReadOnlyList<string> Models { get { lock (_lock) return _models.ToArray(); } }
        public IReadOnlyList<WorkEnvironmentInfo> Environments { get { lock (_lock) return _environments.ToArray(); } }

        public void Enqueue(WorkModelResponse response) => _replies.Enqueue((_, _) => Task.FromResult(response));

        public void EnqueueFailure(Exception error) => _replies.Enqueue((_, _) => Task.FromException<WorkModelResponse>(error));

        public void EnqueueStreamed(IReadOnlyList<string> deltas, WorkModelResponse response) => _replies.Enqueue(async (onContentDelta, _) =>
        {
            foreach (var delta in deltas) await onContentDelta(delta);
            return response;
        });

        public void EnqueueStreamThenHang(IReadOnlyList<string> deltas) => _replies.Enqueue(async (onContentDelta, cancellationToken) =>
        {
            foreach (var delta in deltas) await onContentDelta(delta);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("取消后不应继续执行。");
        });

        public async Task<WorkModelResponse> CompleteAsync(
            IReadOnlyList<WorkContextItem> context,
            IReadOnlyList<WorkToolDefinition> tools,
            WorkEnvironmentInfo environment,
            string model,
            string apiKey,
            Func<string, Task> onContentDelta,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _contexts.Add(context.ToArray());
                _models.Add(model);
                _environments.Add(environment);
                _changed.TrySetResult();
                _changed = Signal();
            }
            if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException("没有准备模型响应。");
            return await reply(onContentDelta, cancellationToken);
        }

        public async Task WaitForCallsAsync(int count)
        {
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    if (_contexts.Count >= count) return;
                    signal = _changed.Task;
                }
                await signal.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WorkAgentFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _workspaceRoot;
        private readonly WorkWorkspaceService _workspaces;

        private WorkAgentFixture(string databasePath, string workspaceRoot, SqliteRepository repository, WorkWorkspaceService workspaces, ControlledWorkProvider provider, WorkToolRegistry tools, WorkAgentService agent, string conversationId)
        {
            _databasePath = databasePath;
            _workspaceRoot = workspaceRoot;
            _workspaces = workspaces;
            Repository = repository;
            Provider = provider;
            Tools = tools;
            Agent = agent;
            ConversationId = conversationId;
            Agent.Changed += OnChanged;
        }

        public ControlledWorkProvider Provider { get; }
        public SqliteRepository Repository { get; }
        public WorkWorkspaceService Workspaces => _workspaces;
        public WorkToolRegistry Tools { get; }
        public WorkAgentService Agent { get; }
        public string ConversationId { get; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<ConversationEvent> Events { get; } = [];

        public static async Task<WorkAgentFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"cat-assistant-agent-{Guid.NewGuid():N}.db");
            var workspaceRoot = Path.Combine(Path.GetTempPath(), $"cat-assistant-agent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspaceRoot);
            var repository = new SqliteRepository(databasePath);
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            var workspaces = new WorkWorkspaceService(workspaceRoot);
            var provider = new ControlledWorkProvider();
            var tools = new WorkToolRegistry();
            var agent = new WorkAgentService(repository, workspaces, provider, tools, () => new AppSettings(), () => "key", CancellationToken.None);
            return new WorkAgentFixture(databasePath, workspaceRoot, repository, workspaces, provider, tools, agent, conversation.Id);
        }

        private void OnChanged(object? sender, ConversationEvent update)
        {
            Events.Add(update);
            if (update.Type == "run.completed") Completed.TrySetResult();
            if (update.Type == "run.failed") Failed.TrySetResult(update.Error ?? "");
        }

        public async ValueTask DisposeAsync()
        {
            Agent.Changed -= OnChanged;
            await Agent.DisposeAsync();
            _workspaces.Dispose();
            foreach (var candidate in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }
}
