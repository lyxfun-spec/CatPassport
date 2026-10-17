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
        private TaskCompletionSource _changed = NewSignal();

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

        public async Task EmitAsync(int index, ChatStreamChunk chunk)
        {
            Channel<ChatStreamChunk> stream;
            lock (_lock) stream = _streams[index];
            await stream.Writer.WriteAsync(chunk);
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

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
