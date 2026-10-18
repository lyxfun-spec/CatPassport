using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WorkApprovalServiceTests
{
    [Fact]
    public async Task ApprovalWaitsUntilExplicitDecisionAndTaskGrantIsScopedToRunAndDirectory()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Approvals.Changed += (_, update) =>
        {
            if (update.Type == "approval.requested") requested.TrySetResult();
        };
        var directory = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "one.txt");
        await fixture.CreateRunAsync("run-a");
        var context = new WorkToolExecutionContext(fixture.ConversationId, "run-a", fixture.Root, "call-a");

        var waiting = fixture.Approvals.RequestAsync(context, "{\"path\":\"one.txt\"}", "task_directory", target, "read", CancellationToken.None);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        var pending = Assert.Single(await fixture.Approvals.ListPendingAsync(fixture.ConversationId));
        await fixture.Approvals.ResolveAsync(fixture.ConversationId, pending.Id, approved: true);
        Assert.True((await waiting).Approved);

        var reused = await fixture.Approvals.RequestAsync(context with { ToolCallId = "call-b" }, "{}", "single",
            Path.Combine(directory, "nested", "two.txt"), "read", CancellationToken.None);
        Assert.True(reused.Approved);
        Assert.Empty(await fixture.Approvals.ListPendingAsync(fixture.ConversationId));

        fixture.Approvals.EndRun("run-a");
        await fixture.CreateRunAsync("run-b");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Approvals.RequestAsync(
            context with { RunId = "run-b", ToolCallId = "call-c" }, "{}", "single", target, "read", cancelled.Token));
        Assert.Empty(await fixture.Approvals.ListPendingAsync(fixture.ConversationId));
    }

    [Fact]
    public async Task RejectionIsReturnedWithoutImplicitRetryOrGrant()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        await fixture.CreateRunAsync("run-reject");
        var context = new WorkToolExecutionContext(fixture.ConversationId, "run-reject", fixture.Root, "call-reject");
        var waiting = fixture.Approvals.RequestAsync(context, "{}", "single", Path.Combine(fixture.Root, "denied.txt"), "write", CancellationToken.None);
        var pending = Assert.Single(await WaitForPendingAsync(fixture));
        await fixture.Approvals.ResolveAsync(fixture.ConversationId, pending.Id, approved: false);
        var result = await waiting;
        Assert.False(result.Approved);
        Assert.Equal("rejected", result.State);
    }

    [Fact]
    public async Task ExternalFileToolUsesTrustedApprovalBrokerAndReusesOnlyApprovedDirectory()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        await fixture.CreateRunAsync("run-tool");
        var external = Path.Combine(Path.GetTempPath(), $"cat-approved-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(external, "nested"));
        await File.WriteAllTextAsync(Path.Combine(external, "one.txt"), "approved-content");
        await File.WriteAllTextAsync(Path.Combine(external, "nested", "two.txt"), "reused-content");
        using var sandbox = new WindowsSandboxController();
        var tool = new ReadFileTool(sandbox, fixture.Approvals);
        var context = new WorkToolExecutionContext(fixture.ConversationId, "run-tool", fixture.Root, "call-tool-a");
        try
        {
            var waiting = tool.ExecuteAsync(context,
                System.Text.Json.JsonSerializer.Serialize(new { path = Path.Combine(external, "one.txt"), approval_scope = "task_directory" }),
                CancellationToken.None);
            var pending = Assert.Single(await WaitForPendingAsync(fixture));
            Assert.Equal(external, pending.TargetPath);
            await fixture.Approvals.ResolveAsync(fixture.ConversationId, pending.Id, approved: true);
            var first = await waiting;
            Assert.True(first.Success, first.ErrorMessage);
            Assert.Contains("approved-content", first.OutputJson);

            var reused = await tool.ExecuteAsync(context with { ToolCallId = "call-tool-b" },
                System.Text.Json.JsonSerializer.Serialize(new { path = Path.Combine(external, "nested", "two.txt") }),
                CancellationToken.None);
            Assert.True(reused.Success, reused.ErrorMessage);
            Assert.Contains("reused-content", reused.OutputJson);
            Assert.Empty(await fixture.Approvals.ListPendingAsync(fixture.ConversationId));
        }
        finally { Directory.Delete(external, recursive: true); }
    }

    [Fact]
    public async Task ExternalBrokerRejectsDirectoryJunctionsBeforeRequestingApproval()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await ApprovalFixture.CreateAsync();
        await fixture.CreateRunAsync("run-junction");
        var external = Path.Combine(Path.GetTempPath(), $"cat-junction-target-{Guid.NewGuid():N}");
        var junction = Path.Combine(Path.GetTempPath(), $"cat-junction-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "secret.txt"), "secret");
        var shell = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   shell, $"/d /q /c mklink /J \"{junction}\" \"{external}\"") { UseShellExecute = false, CreateNoWindow = true }))
        {
            Assert.NotNull(process);
            await process!.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        using var sandbox = new WindowsSandboxController();
        var tool = new ReadFileTool(sandbox, fixture.Approvals);
        try
        {
            var result = await tool.ExecuteAsync(
                new WorkToolExecutionContext(fixture.ConversationId, "run-junction", fixture.Root, "call-junction"),
                System.Text.Json.JsonSerializer.Serialize(new { path = Path.Combine(junction, "secret.txt") }),
                CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal("unsafe_external_path", result.ErrorCode);
            Assert.Empty(await fixture.Approvals.ListPendingAsync(fixture.ConversationId));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(external, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<CatAssistant.Backend.Models.WorkApprovalRecord>> WaitForPendingAsync(ApprovalFixture fixture)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var pending = await fixture.Approvals.ListPendingAsync(fixture.ConversationId);
            if (pending.Count > 0) return pending;
            await Task.Delay(20);
        }
        return [];
    }

    private sealed class ApprovalFixture : IAsyncDisposable
    {
        private ApprovalFixture(string root, SqliteRepository repository, string conversationId)
        {
            Root = root;
            Repository = repository;
            ConversationId = conversationId;
            Approvals = new WorkApprovalService(repository);
        }

        public string Root { get; }
        public SqliteRepository Repository { get; }
        public string ConversationId { get; }
        public WorkApprovalService Approvals { get; }

        public async Task CreateRunAsync(string id)
        {
            var message = await Repository.AddMessageAsync(ConversationId, "user", "desktop", "complete", id);
            await Repository.CreateRunAsync(id, ConversationId, message.Id, "work");
        }

        public static async Task<ApprovalFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cat-approval-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var repository = new SqliteRepository(Path.Combine(root, "assistant.db"));
            await repository.InitializeAsync();
            var conversation = await repository.CreateConversationAsync("work");
            return new ApprovalFixture(root, repository, conversation.Id);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
