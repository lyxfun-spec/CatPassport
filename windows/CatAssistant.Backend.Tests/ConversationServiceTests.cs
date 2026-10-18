using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services;
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
    public async Task CardConversationDetachesAndCanFollowDesktopAgain()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var desktop = fixture.Service.DesktopActiveConversationId!;

        var card = await fixture.Service.CreateForDeviceAsync();

        Assert.NotEqual(desktop, card.Id);
        Assert.Equal(card.Id, fixture.Service.DeviceActiveConversationId);
        Assert.False(fixture.Service.DeviceFollowDesktop);

        await fixture.Service.FollowDesktopAsync();

        Assert.Equal(desktop, fixture.Service.DeviceActiveConversationId);
        Assert.True(fixture.Service.DeviceFollowDesktop);
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
        Assert.NotEqual("fallback", detail.Conversation.TitleSource);
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

    private sealed class ConversationFixture : IAsyncDisposable
    {
        private ConversationFixture(string path, ControlledProvider provider, ConversationService service)
        {
            Path = path;
            Provider = provider;
            Service = service;
        }

        public string Path { get; }
        public ControlledProvider Provider { get; }
        public ConversationService Service { get; }

        public static async Task<ConversationFixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cat-assistant-{Guid.NewGuid():N}.db");
            var repository = new SqliteRepository(path);
            await repository.InitializeAsync();
            var provider = new ControlledProvider();
            var settings = new AppSettings();
            var service = new ConversationService(repository, provider, () => settings, () => "test-key", CancellationToken.None);
            await service.InitializeAsync();
            return new ConversationFixture(path, provider, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(Path + "-wal")) File.Delete(Path + "-wal");
            if (File.Exists(Path + "-shm")) File.Delete(Path + "-shm");
        }
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
                return Task.FromResult(_titleResult);
            }
        }

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
