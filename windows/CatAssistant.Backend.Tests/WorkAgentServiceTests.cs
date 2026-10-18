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
    public async Task ContextLimitFailsExplicitlyWithoutDroppingHistoryOrCallingModel()
    {
        await using var fixture = await WorkAgentFixture.CreateAsync(contextCharacterLimit: 80);

        await fixture.Agent.SendAsync(fixture.ConversationId, new string('长', 120), "desktop");
        var error = await fixture.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("没有静默删除", error);
        Assert.Empty(fixture.Provider.Contexts);
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
        private readonly ConcurrentQueue<WorkModelResponse> _responses = new();
        private readonly object _lock = new();
        private readonly List<IReadOnlyList<WorkContextItem>> _contexts = [];
        private TaskCompletionSource _changed = Signal();

        public IReadOnlyList<IReadOnlyList<WorkContextItem>> Contexts { get { lock (_lock) return _contexts.ToArray(); } }

        public void Enqueue(WorkModelResponse response) => _responses.Enqueue(response);

        public Task<WorkModelResponse> CompleteAsync(
            IReadOnlyList<WorkContextItem> context,
            IReadOnlyList<WorkToolDefinition> tools,
            string model,
            string apiKey,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _contexts.Add(context.ToArray());
                _changed.TrySetResult();
                _changed = Signal();
            }
            if (!_responses.TryDequeue(out var response)) throw new InvalidOperationException("没有准备模型响应。");
            return Task.FromResult(response);
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

        private WorkAgentFixture(string databasePath, string workspaceRoot, WorkWorkspaceService workspaces, ControlledWorkProvider provider, WorkToolRegistry tools, WorkAgentService agent, string conversationId)
        {
            _databasePath = databasePath;
            _workspaceRoot = workspaceRoot;
            _workspaces = workspaces;
            Provider = provider;
            Tools = tools;
            Agent = agent;
            ConversationId = conversationId;
            Agent.Changed += OnChanged;
        }

        public ControlledWorkProvider Provider { get; }
        public WorkToolRegistry Tools { get; }
        public WorkAgentService Agent { get; }
        public string ConversationId { get; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<WorkAgentFixture> CreateAsync(int contextCharacterLimit = 96_000)
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
            var agent = new WorkAgentService(repository, workspaces, provider, tools, () => new AppSettings(), () => "key", CancellationToken.None, contextCharacterLimit);
            return new WorkAgentFixture(databasePath, workspaceRoot, workspaces, provider, tools, agent, conversation.Id);
        }

        private void OnChanged(object? sender, ConversationEvent update)
        {
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
