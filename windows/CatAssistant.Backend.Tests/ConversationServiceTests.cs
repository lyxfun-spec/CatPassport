using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Work;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class ConversationServiceTests
{
    [Fact]
    public async Task NewMessageInterruptsEmptyRunWithoutAddingAssistantContext()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var id = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(id, "第一条", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Service.SendAsync(id, "第二条", "desktop");
        await fixture.Provider.WaitForCallsAsync(2);

        var detail = await fixture.Service.GetAsync(id);
        Assert.Equal(new[] { "user", "user", "assistant" }, detail.Messages.Select(message => message.Role));
        Assert.Equal(new[] { "第一条", "第二条", "" }, detail.Messages.Select(message => message.Content));
        Assert.DoesNotContain(detail.Messages, message => message.Status == "interrupted");
        Assert.Equal(new[] { "第一条", "第二条" }, fixture.Provider.Histories[1].Select(message => message.Content));
    }

    [Fact]
    public async Task NewMessageKeepsPartialAssistantReplyInNextContext()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var id = fixture.Service.DesktopActiveConversationId!;
        var deltaSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "message.delta") deltaSeen.TrySetResult();
        };

        await fixture.Service.SendAsync(id, "第一条", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "半条回复"));
        await deltaSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.SendAsync(id, "第二条", "desktop");
        await fixture.Provider.WaitForCallsAsync(2);

        var detail = await fixture.Service.GetAsync(id);
        var interrupted = Assert.Single(detail.Messages, message => message.Status == "interrupted");
        Assert.Equal("半条回复", interrupted.Content);
        Assert.Equal(
            new[] { "第一条", "半条回复", "第二条" },
            fixture.Provider.Histories[1].Select(message => message.Content));
    }

    [Fact]
    public async Task CardNewConversationIsCreatedByItsFirstMessageNotTheLongPress()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var desktop = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.DetachDeviceForNewConversationAsync();

        Assert.Null(fixture.Service.DeviceActiveConversationId);
        Assert.False(fixture.Service.DeviceFollowDesktop);
        Assert.Single(await fixture.Service.ListAsync());

        var detail = await fixture.Service.StartForDeviceAsync("卡片第一条");
        await fixture.Provider.WaitForCallsAsync(1);

        Assert.NotEqual(desktop, detail.Conversation.Id);
        Assert.Equal(detail.Conversation.Id, fixture.Service.DeviceActiveConversationId);
        Assert.Equal(desktop, fixture.Service.DesktopActiveConversationId);
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        Assert.Contains(detail.Messages, message => message.Role == "user" && message.Content == "卡片第一条");

        await fixture.Service.FollowDesktopAsync();

        Assert.Equal(desktop, fixture.Service.DeviceActiveConversationId);
        Assert.True(fixture.Service.DeviceFollowDesktop);
    }

    [Fact]
    public async Task CardPendingNewConversationSurvivesARestart()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await fixture.Service.DetachDeviceForNewConversationAsync();

        await using var restarted = new ConversationService(
            fixture.Repository, new ControlledProvider(), () => new AppSettings(), () => "test-key", CancellationToken.None);
        await restarted.InitializeAsync();

        Assert.Null(restarted.DeviceActiveConversationId);
        Assert.False(restarted.DeviceFollowDesktop);
    }

    [Fact]
    public async Task StartCreatesConversationAndSendsFirstMessage()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var seed = fixture.Service.DesktopActiveConversationId!;

        var detail = await fixture.Service.StartAsync("chat", "你好世界", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);

        Assert.NotEqual(seed, detail.Conversation.Id);
        Assert.Equal(detail.Conversation.Id, fixture.Service.DesktopActiveConversationId);
        Assert.Equal("chat", detail.Conversation.Mode);
        Assert.Contains(detail.Messages, message => message.Role == "user" && message.Content == "你好世界");
        Assert.Equal("你好世界", detail.Conversation.Title);
        Assert.Equal("fallback", detail.Conversation.TitleSource);
    }

    [Fact]
    public async Task FirstMessageAutomaticallyGeneratesAStableConversationTitle()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "conversation.updated" && update.Conversation?.TitleSource == "generated")
                generated.TrySetResult(update.Conversation.Title);
        };
        fixture.Provider.CompleteTitle("秋日随笔");

        var detail = await fixture.Service.StartAsync("chat", "帮我写一首关于秋天的诗", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "秋天来了，落叶纷飞。"));
        fixture.Provider.CompleteStream(0);

        var title = await generated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("秋日随笔", title);
        Assert.Equal("秋日随笔", (await fixture.Service.GetAsync(detail.Conversation.Id)).Conversation.Title);
    }

    [Theory]
    [InlineData("  秋日随笔  ", "秋日随笔")]
    [InlineData("标题：秋日随笔", "秋日随笔")]
    [InlineData("Title: Autumn Notes", "Autumn Notes")]
    [InlineData("\" quoted \"", "quoted")]
    [InlineData("   ", null)]
    public void GeneratedTitlesAreNormalized(string input, string? expected)
    {
        Assert.Equal(expected, ConversationService.NormalizeGeneratedTitle(input));
    }

    [Fact]
    public async Task GeneratedTitleCannotOverwriteAUserRename()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "message.completed") completed.TrySetResult();
        };

        var detail = await fixture.Service.StartAsync("chat", "帮我写一首关于秋天的诗", "desktop");
        var id = detail.Conversation.Id;
        await fixture.Service.RenameAsync(id, "我的自定义名称");
        await fixture.Provider.WaitForCallsAsync(1);
        fixture.Provider.CompleteTitle("秋日随笔");
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "秋天来了。"));
        fixture.Provider.CompleteStream(0);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(250);

        var conversation = (await fixture.Service.GetAsync(id)).Conversation;
        Assert.Equal("我的自定义名称", conversation.Title);
        Assert.Equal("user", conversation.TitleSource);
    }

    [Fact]
    public async Task TurnThatOnlyProposesWorkStillGetsATitle()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var titled = WaitForGeneratedTitleAsync(fixture);
        var id = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(id, "把聊天内容整理成一份演示文稿", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(ToolCall: new ChatToolCall(
            "call-work-1",
            "request_work",
            """{"title":"项目演示文稿","prompt":"整理当前讨论并制作演示文稿。","contextSummary":"用户希望把当前讨论整理成演示文稿。"}""")));
        fixture.Provider.CompleteStream(0);

        Assert.Equal("自动生成的标题", await titled);
        Assert.Equal(("把聊天内容整理成一份演示文稿", ""), fixture.Provider.TitleRequests.Single());
    }

    [Fact]
    public async Task StoppedFirstAnswerStillLeadsToATitle()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var titled = WaitForGeneratedTitleAsync(fixture);
        var deltaSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "message.delta") deltaSeen.TrySetResult();
        };
        var id = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(id, "写一篇长文", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "写到一半"));
        await deltaSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.CancelAsync(id);

        Assert.Equal("自动生成的标题", await titled);
        Assert.Equal(("写一篇长文", "写到一半"), fixture.Provider.TitleRequests.Single());
    }

    [Fact]
    public async Task TitleGenerationRetriesAfterATransientFailure()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        fixture.Provider.FailTitleAttempts(1);
        var titled = WaitForGeneratedTitleAsync(fixture);

        var detail = await fixture.Service.StartAsync("chat", "帮我写一首关于秋天的诗", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "秋天来了。"));
        fixture.Provider.CompleteStream(0);

        Assert.Equal("自动生成的标题", await titled);
        Assert.Equal(2, fixture.Provider.TitleRequests.Count);
        Assert.Equal("自动生成的标题", (await fixture.Service.GetAsync(detail.Conversation.Id)).Conversation.Title);
    }

    [Fact]
    public async Task UntitledConversationKeepsTheOpeningMessageAsItsFallbackTitle()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        fixture.Provider.FailTitleAttempts(99);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "message.completed") completed.TrySetResult();
        };
        var id = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(id, "第一条", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        fixture.Provider.CompleteStream(0);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Service.SendAsync(id, "第二条", "desktop");

        var conversation = (await fixture.Service.GetAsync(id)).Conversation;
        Assert.Equal("第一条", conversation.Title);
        Assert.Equal("fallback", conversation.TitleSource);
    }

    [Fact]
    public async Task WorkConversationIsTitledFromItsRequest()
    {
        await using var fixture = await ConversationFixture.CreateAsync(enableWork: true);
        var titled = WaitForGeneratedTitleAsync(fixture);

        await fixture.Service.StartAsync("work", "整理下载文件夹里的发票", "desktop");

        Assert.Equal("自动生成的标题", await titled);
        Assert.Equal("整理下载文件夹里的发票", fixture.Provider.TitleRequests.Single().User);
    }

    private static Task<string> WaitForGeneratedTitleAsync(ConversationFixture fixture)
    {
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "conversation.updated" && update.Conversation?.TitleSource == "generated")
                source.TrySetResult(update.Conversation.Title);
        };
        return source.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task PinnedConversationsArePersistedAndSortedFirst()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var first = fixture.Service.DesktopActiveConversationId!;
        var second = await fixture.Service.CreateAsync("chat");

        await fixture.Service.SetPinnedAsync(first, true);
        var list = await fixture.Service.ListAsync();
        Assert.Equal(first, list[0].Id);
        Assert.True(list[0].IsPinned);
        Assert.False(list.Single(item => item.Id == second.Id).IsPinned);

        await fixture.Service.SetPinnedAsync(first, false);
        list = await fixture.Service.ListAsync();
        Assert.False(list.Single(item => item.Id == first).IsPinned);
    }

    [Fact]
    public async Task CitationsKeepProviderOrdinalsAndMarkersStayOutOfModelContext()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "message.completed") completed.TrySetResult();
        };
        var id = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(id, "杭州天气", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Citations:
        [
            new CitationRecord("a", "", 5, "五号来源", "https://example.com/5", null, "站点五"),
            new CitationRecord("b", "", 2, "二号来源", "https://example.com/2", null, null)
        ]));
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "明天降温[ref_5][ref_2]。"));
        fixture.Provider.CompleteStream(0);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var detail = await fixture.Service.GetAsync(id);
        Assert.Equal(new[] { 2, 5 }, detail.Citations.Select(item => item.Ordinal));
        Assert.Equal("五号来源", detail.Citations.Single(item => item.Ordinal == 5).Title);
        Assert.Contains(detail.Messages, message => message.Content == "明天降温[ref_5][ref_2]。");

        await fixture.Service.SendAsync(id, "后天呢", "desktop");
        await fixture.Provider.WaitForCallsAsync(2);
        Assert.Equal(new[] { "杭州天气", "明天降温。", "后天呢" }, fixture.Provider.Histories[1].Select(message => message.Content));
    }

    [Fact]
    public async Task ChatToolCallPersistsProposalWithoutCreatingWorkBeforeApproval()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var proposed = new TaskCompletionSource<WorkRequestRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "work.request.proposed" && update.WorkRequest is { } request) proposed.TrySetResult(request);
            if (update.Type == "run.completed") completed.TrySetResult();
        };
        var sourceId = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(sourceId, "把聊天内容整理成一份演示文稿", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(ToolCall: new ChatToolCall(
            "call-work-1",
            "request_work",
            """{"title":"项目演示文稿","prompt":"整理当前讨论并制作演示文稿。","contextSummary":"用户希望把当前讨论整理成演示文稿。"}""")));

        var request = await proposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Provider.CompleteStream(0);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("proposed", request.State);
        Assert.Equal(0, request.AnchorOffset);
        Assert.Single((await fixture.Service.GetAsync(sourceId)).WorkRequests!);
        Assert.DoesNotContain(await fixture.Service.ListAsync(), item => item.Mode == "work");
        var rejected = await fixture.Service.ResolveWorkRequestAsync(request.Id, approved: false);
        Assert.Equal("rejected", rejected.State);
        Assert.DoesNotContain(await fixture.Service.ListAsync(), item => item.Mode == "work");
    }

    [Fact]
    public async Task ApprovalCreatesOneBackgroundWorkConversationWithBidirectionalOrigin()
    {
        await using var fixture = await ConversationFixture.CreateAsync(enableWork: true);
        var proposed = new TaskCompletionSource<WorkRequestRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "work.request.proposed" && update.WorkRequest is { } request) proposed.TrySetResult(request);
            if (update.Type == "run.completed" && update.ConversationId == fixture.Service.DesktopActiveConversationId) chatCompleted.TrySetResult();
        };
        var sourceId = fixture.Service.DesktopActiveConversationId!;
        await fixture.Service.SendAsync(sourceId, "制作发布计划文档", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(ToolCall: new ChatToolCall(
            "call-work-approve",
            "request_work",
            """{"title":"发布计划","prompt":"创建一份可审阅的发布计划文档。","contextSummary":"项目需要一份发布计划，内容应可审阅。"}""")));
        var proposal = await proposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Provider.CompleteStream(0);
        await chatCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var started = await fixture.Service.ResolveWorkRequestAsync(proposal.Id, approved: true);
        var repeated = await fixture.Service.ResolveWorkRequestAsync(proposal.Id, approved: true);

        Assert.Equal("started", started.State);
        Assert.Equal(started.TargetConversationId, repeated.TargetConversationId);
        Assert.Equal(sourceId, fixture.Service.DesktopActiveConversationId);
        var work = Assert.Single(await fixture.Service.ListAsync(), item => item.Mode == "work");
        Assert.Equal(sourceId, work.OriginConversationId);
        Assert.Equal("发布计划", work.Title);
        Assert.Contains((await fixture.Service.GetAsync(work.Id)).Messages, message => message.Role == "user" && message.Content == "创建一份可审阅的发布计划文档。");
        var handoff = Assert.Single(await fixture.Repository.LoadWorkContextItemsAsync(work.Id), item => item.Name == "chat_handoff_context");
        using (var payload = JsonDocument.Parse(handoff.PayloadJson))
            Assert.Contains("项目需要一份发布计划", payload.RootElement.GetProperty("content").GetString());
        Assert.Contains((await fixture.Service.GetAsync(work.Id)).WorkRequests!, item => item.Id == proposal.Id && item.SourceConversationId == sourceId);
    }

    [Fact]
    public async Task InvalidWorkProposalFailsGracefullyWithoutInterruptingTheChatRun()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var proposed = new TaskCompletionSource<WorkRequestRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "work.request.proposed" && update.WorkRequest is { } request) proposed.TrySetResult(request);
            if (update.Type is "message.completed" or "run.completed") completed.TrySetResult();
            if (update.Type == "run.failed") failed.TrySetResult(update.Error ?? string.Empty);
        };
        var sourceId = fixture.Service.DesktopActiveConversationId!;

        await fixture.Service.SendAsync(sourceId, "帮我把资料整理成演示文稿", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(Delta: "好的，我来安排。"));
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(ToolCall: new ChatToolCall("call-broken", "request_work", "{\"title\":\"演示文稿\",\"prompt\":")));
        fixture.Provider.CompleteStream(0);

        var request = await proposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(failed.Task.IsCompleted);
        Assert.Equal("failed", request.State);
        Assert.Empty(request.Prompt);
        Assert.NotNull(request.ErrorMessage);
        Assert.Equal("好的，我来安排。".Length, request.AnchorOffset);
        var detail = await fixture.Service.GetAsync(sourceId);
        Assert.Equal("好的，我来安排。", Assert.Single(detail.Messages, message => message.Role == "assistant").Content);
        Assert.DoesNotContain(await fixture.Service.ListAsync(), item => item.Mode == "work");
        await fixture.Provider.WaitForTitleCallsAsync(1);
        await Task.Delay(200);
    }

    [Fact]
    public async Task ReproposingARejectedWorkRequestRevivesTheSameRecord()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sourceId = fixture.Service.DesktopActiveConversationId!;
        var proposals = new List<WorkRequestRecord>();
        void Subscribe() => fixture.Service.Changed += (_, update) =>
        {
            if (update.Type == "work.request.proposed" && update.WorkRequest is { } request) proposals.Add(request);
        };

        Subscribe();
        await fixture.Service.SendAsync(sourceId, "做一份演示文稿", "desktop");
        await fixture.Provider.WaitForCallsAsync(1);
        await fixture.Provider.EmitAsync(0, new ChatStreamChunk(ToolCall: new ChatToolCall("call-again", "request_work",
            """{"title":"演示文稿","prompt":"制作演示文稿。","contextSummary":"用户想要演示文稿。"}""")));
        fixture.Provider.CompleteStream(0);
        while (proposals.Count == 0) await Task.Delay(10);
        var first = proposals[0];
        Assert.Equal("rejected", (await fixture.Service.ResolveWorkRequestAsync(first.Id, approved: false)).State);

        proposals.Clear();
        Subscribe();
        await fixture.Service.SendAsync(sourceId, "换成发布会脚本再做", "desktop");
        await fixture.Provider.WaitForCallsAsync(2);
        await fixture.Provider.EmitAsync(1, new ChatStreamChunk(ToolCall: new ChatToolCall("call-again", "request_work",
            """{"title":"发布会脚本","prompt":"改写为发布会脚本。","contextSummary":"用户改要发布会脚本。"}""")));
        fixture.Provider.CompleteStream(1);
        while (proposals.Count == 0) await Task.Delay(10);

        var second = proposals[0];
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("proposed", second.State);
        Assert.Equal("发布会脚本", second.Title);
        Assert.Single((await fixture.Service.GetAsync(sourceId)).WorkRequests!);
        Assert.DoesNotContain(await fixture.Service.ListAsync(), item => item.Mode == "work");
    }

    [Fact]
    public async Task InitializeDoesNotReplayARequestThatAlreadyStarted()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-{Guid.NewGuid():N}.db");
        var workspaceRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-workspaces-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var source = await repository.CreateConversationAsync("chat");
            var request = await repository.CreateWorkRequestAsync(
                source.Id, "message-started", "run-started", "tool-started", "已启动", "生成交付物。", "用户已批准并已启动。");
            await repository.UpdateWorkRequestAsync(request.Id, "proposed", "approved");
            var (target, _) = await repository.CreateWorkConversationForRequestAsync(request.Id);
            await repository.UpdateWorkRequestAsync(request.Id, "approved", "started", target.Id);
            await repository.AddMessageAsync(target.Id, "user", "desktop", "complete", "生成交付物。");

            var settings = new AppSettings();
            var workspaces = new WorkWorkspaceService(workspaceRoot);
            var agent = new WorkAgentService(repository, workspaces, new ImmediateWorkProvider(), new WorkToolRegistry(), () => settings, () => "test-key", CancellationToken.None);
            await using (var service = new ConversationService(repository, new ControlledProvider(), () => settings, () => "test-key", CancellationToken.None, workspaces, agent))
            {
                await service.InitializeAsync();
                Assert.Single(await service.ListAsync(), item => item.Mode == "work");
                Assert.Single((await service.GetAsync(target.Id)).Messages, message => message.Content == "生成交付物。");
                Assert.Equal("started", (await repository.GetWorkRequestAsync(request.Id))!.State);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeResumesApprovedRequestWithoutCreatingDuplicateWorkConversation()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-{Guid.NewGuid():N}.db");
        var workspaceRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-workspaces-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var source = await repository.CreateConversationAsync("chat");
            var request = await repository.CreateWorkRequestAsync(
                source.Id, "message-restart", "run-restart", "tool-restart", "重启续跑", "生成重启后的交付物。", "应用在用户批准后退出。");
            await repository.UpdateWorkRequestAsync(request.Id, "proposed", "approved");

            var settings = new AppSettings();
            var workspaces = new WorkWorkspaceService(workspaceRoot);
            var agent = new WorkAgentService(repository, workspaces, new ImmediateWorkProvider(), new WorkToolRegistry(), () => settings, () => "test-key", CancellationToken.None);
            await using (var service = new ConversationService(repository, new ControlledProvider(), () => settings, () => "test-key", CancellationToken.None, workspaces, agent))
            {
                await service.InitializeAsync();
                var resumed = await repository.GetWorkRequestAsync(request.Id);
                Assert.Equal("started", resumed!.State);
                Assert.NotNull(resumed.TargetConversationId);
                Assert.Single(await service.ListAsync(), item => item.Mode == "work");
                Assert.Contains((await service.GetAsync(resumed.TargetConversationId!)).Messages,
                    message => message.Role == "user" && message.Content == "生成重启后的交付物。");
            }

            var secondWorkspaces = new WorkWorkspaceService(workspaceRoot);
            var secondAgent = new WorkAgentService(repository, secondWorkspaces, new ImmediateWorkProvider(), new WorkToolRegistry(), () => settings, () => "test-key", CancellationToken.None);
            await using (var service = new ConversationService(repository, new ControlledProvider(), () => settings, () => "test-key", CancellationToken.None, secondWorkspaces, secondAgent))
            {
                await service.InitializeAsync();
                Assert.Single(await service.ListAsync(), item => item.Mode == "work");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private sealed class ConversationFixture : IAsyncDisposable
    {
        private ConversationFixture(string path, string workspaceRoot, SqliteRepository repository, ControlledProvider provider, ConversationService service)
        {
            Path = path;
            WorkspaceRoot = workspaceRoot;
            Repository = repository;
            Provider = provider;
            Service = service;
        }

        public string Path { get; }
        public string WorkspaceRoot { get; }
        public SqliteRepository Repository { get; }
        public ControlledProvider Provider { get; }
        public ConversationService Service { get; }

        public static async Task<ConversationFixture> CreateAsync(bool enableWork = false)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-{Guid.NewGuid():N}.db");
            var workspaceRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-workspaces-{Guid.NewGuid():N}");
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var provider = new ControlledProvider();
            var settings = new AppSettings();
            var workspaces = new WorkWorkspaceService(workspaceRoot);
            var workAgent = enableWork
                ? new WorkAgentService(repository, workspaces, new ImmediateWorkProvider(), new WorkToolRegistry(), () => settings, () => "test-key", CancellationToken.None)
                : null;
            var service = new ConversationService(repository, provider, () => settings, () => "test-key", CancellationToken.None, workspaces, workAgent);
            await service.InitializeAsync();
            return new ConversationFixture(path, workspaceRoot, repository, provider, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(Path + "-wal")) File.Delete(Path + "-wal");
            if (File.Exists(Path + "-shm")) File.Delete(Path + "-shm");
            if (Directory.Exists(WorkspaceRoot)) Directory.Delete(WorkspaceRoot, recursive: true);
        }
    }

    private sealed class ImmediateWorkProvider : IWorkModelProvider
    {
        public Task<WorkModelResponse> CompleteAsync(
            IReadOnlyList<WorkContextItem> context,
            IReadOnlyList<WorkToolDefinition> tools,
            WorkEnvironmentInfo environment,
            string model,
            string apiKey,
            Func<string, Task> onContentDelta,
            CancellationToken cancellationToken) => Task.FromResult(new WorkModelResponse("任务已接收。", []));
    }

    private sealed class ControlledProvider : IChatProvider
    {
        private readonly object _lock = new();
        private readonly List<Channel<ChatStreamChunk>> _streams = [];
        private readonly List<IReadOnlyList<ConversationMessage>> _histories = [];
        private readonly List<(string User, string Assistant)> _titles = [];
        private TaskCompletionSource _changed = NewSignal();
        private TaskCompletionSource _titleChanged = NewSignal();
        private string _titleResult = "自动生成的标题";
        private int _titleFailures;

        public IReadOnlyList<(string User, string Assistant)> TitleRequests { get { lock (_lock) return _titles.ToArray(); } }

        public IReadOnlyList<IReadOnlyList<ConversationMessage>> Histories { get { lock (_lock) return _histories.ToArray(); } }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            IReadOnlyList<ConversationMessage> history,
            string model,
            string apiKey,
            bool enableWebSearch,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Channel<ChatStreamChunk> stream;
            lock (_lock)
            {
                stream = Channel.CreateUnbounded<ChatStreamChunk>();
                _streams.Add(stream);
                _histories.Add(history.ToArray());
                _changed.TrySetResult();
                _changed = NewSignal();
            }
            await foreach (var chunk in stream.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
        }

        public Task<string> GenerateTitleAsync(
            string userMessage,
            string assistantMessage,
            string model,
            string apiKey,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _titles.Add((userMessage, assistantMessage));
                _titleChanged.TrySetResult();
                _titleChanged = NewSignal();
                if (_titleFailures > 0)
                {
                    _titleFailures--;
                    return Task.FromException<string>(new InvalidOperationException("标题服务暂时不可用。"));
                }
                return Task.FromResult(_titleResult);
            }
        }

        public void FailTitleAttempts(int count) { lock (_lock) _titleFailures = count; }

        public void CompleteTitle(string value) { lock (_lock) _titleResult = value; }

        public async Task EmitAsync(int index, ChatStreamChunk chunk)
        {
            Channel<ChatStreamChunk> stream;
            lock (_lock) stream = _streams[index];
            await stream.Writer.WriteAsync(chunk);
        }

        public void CompleteStream(int index)
        {
            Channel<ChatStreamChunk> stream;
            lock (_lock) stream = _streams[index];
            stream.Writer.TryComplete();
        }

        public async Task WaitForCallsAsync(int count)
        {
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    if (_streams.Count >= count) return;
                    signal = _changed.Task;
                }
                await signal.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public async Task WaitForTitleCallsAsync(int count)
        {
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    if (_titles.Count >= count) return;
                    signal = _titleChanged.Task;
                }
                await signal.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
