using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using CatAssistant.Core.Audio;
using CatAssistant.Core.Protocol;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace CatAssistant.Backend.Services;

public sealed class AssistantCoordinator : IAsyncDisposable
{
    private readonly BleGateway _ble;
    private readonly DashScopeClient _cloud;
    private readonly SqliteRepository _repository;
    private readonly SecureApiKeyStore _secureStore;
    private readonly ConversationService _conversations;
    private readonly WindowsSandboxController _workSandbox;
    private readonly PeriodicTimer _reconnectTimer = new(TimeSpan.FromSeconds(1));
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Dictionary<ulong, CatDevice> _devices = [];
    private readonly Dictionary<string, int> _cardRunBytes = [];
    private readonly Dictionary<string, CitationMarkerStreamFilter> _cardRunMarkerFilters = [];
    private readonly object _stateLock = new();
    private readonly object _audioLock = new();
    private readonly SemaphoreSlim _audioArrived = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ControlMessage> _controlMessages = Channel.CreateUnbounded<ControlMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly InteractionTurns _turns;
    private readonly RecordingInbox _recordings = new();
    private ushort? _recordingId;
    private ushort? _discardedRecordingId;
    private string? _discardedRecordingMessage;
    private string? _transcript;
    private string _status = "未连接";
    private string _detail = "请扫描并选择小猫助手卡片";
    private TaskCompletionSource<bool>? _handshakeReady;
    private readonly Task _controlPump;
    private readonly Task _linkHealthPump;
    private bool _linkReady;
    private uint? _pendingPing;
    private DateTimeOffset _pendingPingAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPingAt = DateTimeOffset.MinValue;
    private int _reconnectFailures;
    private DateTimeOffset _nextReconnectAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan AudioDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(6);

    public AssistantCoordinator(BleGateway ble, DashScopeClient cloud, SqliteRepository repository, SecureApiKeyStore secureStore, AppSettings settings)
    {
        _ble = ble; _cloud = cloud; _repository = repository; _secureStore = secureStore; Settings = settings;
        var workPython = WorkRuntimeLocator.FindPython();
        if (workPython is not null) Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", workPython);
        _workSandbox = new WindowsSandboxController(workPython is null ? null : [Path.GetDirectoryName(workPython)!]);
        var workspaces = new WorkWorkspaceService(conversationRoot: id =>
            Path.Combine(_workSandbox.GetPrivateStorageRoot(id), "Workspace"));
        var workApprovals = new WorkApprovalService(repository);
        var workTools = new WorkToolRegistry();
        workTools.Register(new ExecuteCommandTool(_workSandbox, workApprovals));
        workTools.Register(new ReadFileTool(_workSandbox, workApprovals));
        workTools.Register(new WriteFileTool(_workSandbox, workApprovals));
        workTools.Register(new ListDirectoryTool(_workSandbox, workApprovals));
        workTools.Register(new DeliverFilesTool(repository));
        workTools.Register(new WebSearchTool(cloud, () => Settings, () => _secureStore.Load() ?? ""));
        var skills = new BuiltInSkillCatalog();
        workTools.Register(new ListSkillsTool(skills));
        workTools.Register(new LoadSkillTool(skills));
        var workAgent = new WorkAgentService(
            repository, workspaces, cloud, workTools, () => Settings, () => _secureStore.Load() ?? "", _lifetime.Token, approvals: workApprovals);
        _conversations = new ConversationService(
            repository, cloud, () => Settings, () => _secureStore.Load() ?? "", _lifetime.Token, workspaces, workAgent, workApprovals);
        _turns = new InteractionTurns(_lifetime.Token);
        _ble.DeviceFound += OnDeviceFound; _ble.ControlMessageReceived += OnControlMessage; _ble.AudioFrameReceived += OnAudioFrame;
        _ble.LinkStateChanged += OnLinkStateChanged;
        _ble.ConnectionLost += OnConnectionLost;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _conversations.Changed += OnConversationChanged;
        _controlPump = ProcessControlMessagesAsync(_lifetime.Token);
        _linkHealthPump = MonitorLinkHealthAsync(_lifetime.Token);
    }

    public AppSettings Settings { get; }
    public ConversationService Conversations => _conversations;
    public event EventHandler<DeviceState>? StateChanged;

    public async Task StartAsync()
    {
        await _conversations.InitializeAsync();
        PublishState();
        _ = ReconnectLoopAsync(_lifetime.Token);
        if (Settings.AutoReconnect && Settings.LastBluetoothAddress is ulong address)
            _ = ConnectAsync(address, Settings.LastDeviceName ?? "FoloToy Cat");
        else
            _ = ScanAsync();
    }

    public DeviceState Snapshot()
    {
        lock (_stateLock)
        {
            return new DeviceState(
                _status, _detail, _linkReady, Settings.LastBluetoothAddress is not null,
                Settings.LastBluetoothAddress, Settings.LastDeviceName, _ble.MaxPduSize,
                _devices.Values.OrderByDescending(device => device.Rssi).ToArray(),
                _conversations.DeviceActiveConversationId, null, _conversations.DeviceFollowDesktop);
        }
    }

    public PublicSettings PublicSettings() => new(
        Settings.AsrModel,
        Settings.ChatModel,
        Settings.ScreenTimeoutSeconds,
        Settings.AutoReconnect,
        Settings.WebSearchEnabled,
        !string.IsNullOrWhiteSpace(_secureStore.Load()));

    public async Task ScanAsync()
    {
        lock (_stateLock) _devices.Clear();
        SetStatus("扫描中", "正在查找 FoloToy Cat…");
        try { await _ble.StartScanAsync(); } catch (Exception error) { ShowError(error); }
    }

    public Task ConnectAsync(ulong address, string? name = null) => ConnectAsync(address, name, automatic: false);

    private async Task ConnectAsync(ulong address, string? name, bool automatic)
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        TaskCompletionSource<bool>? handshake = null;
        try
        {
            EnsureDeviceMatchesOwner(Settings.LastBluetoothAddress, address);
            if (IsLinkReady()) return;
            handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handshakeReady = handshake;
            SetLinkReady(false);
            SetStatus(automatic ? "正在自动恢复" : "正在连接", "正在建立加密 BLE 连接…");
            CatDevice device;
            lock (_stateLock) device = _devices.GetValueOrDefault(address) ?? new CatDevice(address, name ?? "FoloToy Cat", 0);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await _ble.ConnectAsync(device, connectCts.Token);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested && connectCts.IsCancellationRequested)
            {
                throw new LinkException(LinkError.DeviceUnavailable, "蓝牙连接超时，客户端将自动重试。");
            }
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            handshakeCts.CancelAfter(HandshakeTimeout);
            try
            {
                await handshake.Task.WaitAsync(handshakeCts.Token);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                throw new LinkException(LinkError.Handshake, "卡片应用握手超时，连接将自动重试。");
            }
            Settings.LastBluetoothAddress = device.Address; Settings.LastDeviceName = device.Name;
            await _repository.SaveSettingsAsync(Settings);
            _reconnectFailures = 0;
            _nextReconnectAt = DateTimeOffset.MinValue;
            SetStatus("卡片已就绪", $"已绑定 {device.Name}，PDU {_ble.MaxPduSize}");
        }
        catch (Exception error)
        {
            SetLinkReady(false);
            await _ble.DisconnectAsync();
            try { await _ble.StartScanAsync(); } catch { }
            ScheduleReconnectAfterFailure();
            ShowError(error);
            PublishState();
        }
        finally
        {
            if (ReferenceEquals(_handshakeReady, handshake)) _handshakeReady = null;
            _connectionGate.Release();
        }
    }

    internal static void EnsureDeviceMatchesOwner(ulong? ownerAddress, ulong requestedAddress)
    {
        if (ownerAddress is ulong owner && owner != requestedAddress)
            throw new LinkException(LinkError.BoundDeviceMismatch, "客户端已绑定另一张 AI 卡片；请先点击“取消配对”再更换设备。");
    }

    internal static TimeSpan ReconnectDelayForFailure(int failureCount)
    {
        var seconds = Math.Min(15, 1 << Math.Clamp(failureCount - 1, 0, 4));
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task ForgetAsync()
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        try
        {
            CancelAllTurns("设备已忘记"); await _ble.ForgetAsync(Settings.LastBluetoothAddress);
            Settings.LastBluetoothAddress = null; Settings.LastDeviceName = null;
            _reconnectFailures = 0; _nextReconnectAt = DateTimeOffset.MinValue;
            SetLinkReady(false);
            await _repository.SaveSettingsAsync(Settings);
            SetStatus("设备已忘记", "请重新扫描并配对");
            await ScanAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _connectionGate.Release(); }
    }

    public async Task NewConversationAsync()
    {
        try
        {
            await _conversations.CreateAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task CancelCurrentTurnAsync()
    {
        try
        {
            var type = CurrentStatus().Contains("AI", StringComparison.Ordinal) ? MessageType.ChatError : MessageType.AsrError;
            var conversationId = _conversations.DesktopActiveConversationId;
            if (conversationId is not null) await _conversations.CancelAsync(conversationId);
            if (IsLinkReady()) await _ble.SendAsync(type, CatProtocol.Utf8("已在 Windows 客户端取消", 256), _lifetime.Token);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task SaveSettingsAsync(string? apiKey, string asrModel, string chatModel, uint screenTimeout, bool autoReconnect, bool webSearchEnabled)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) _secureStore.Save(apiKey);
            Settings.AsrModel = asrModel.Trim(); Settings.ChatModel = chatModel.Trim(); Settings.ScreenTimeoutSeconds = screenTimeout; Settings.AutoReconnect = autoReconnect; Settings.WebSearchEnabled = webSearchEnabled;
            await _repository.SaveSettingsAsync(Settings);
            if (IsLinkReady()) { await SendUInt32Async(MessageType.ScreenTimeout, screenTimeout); await SendAppStatusAsync(); }
            SetDetail("设置已保存");
        }
        catch (Exception error) { ShowError(error); throw; }
    }

    public async Task SendDesktopMessageAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("消息不能为空。", nameof(text));
        var conversationId = _conversations.DesktopActiveConversationId ?? throw new InvalidOperationException("没有活动对话。");
        try { await _conversations.SendAsync(conversationId, text, "desktop"); }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(error); throw; }
    }

    private void OnDeviceFound(object? sender, CatDevice device)
    {
        lock (_stateLock) _devices[device.Address] = device;
        PublishState();
    }

    private void OnConversationChanged(object? sender, ConversationEvent update)
    {
        if (update.Type is "conversation.created" or "conversation.activated" or "conversation.deleted" or "device.binding")
            PublishState();
        if (!IsLinkReady() || update.ConversationId != _conversations.DeviceActiveConversationId) return;
        _ = RelayConversationToCardAsync(update);
    }

    private async Task RelayConversationToCardAsync(ConversationEvent update)
    {
        try
        {
            switch (update.Type)
            {
                case "run.started" when update.RunId is not null:
                    lock (_stateLock) { _cardRunBytes[update.RunId] = 0; _cardRunMarkerFilters[update.RunId] = new CitationMarkerStreamFilter(); }
                    await _ble.SendAsync(MessageType.ChatStarted, ReadOnlyMemory<byte>.Empty, _lifetime.Token);
                    SetStatus("AI 正在回答", "正在生成回复");
                    break;
                case "message.delta" when update.RunId is not null && update.Delta is not null:
                    string visible;
                    lock (_stateLock)
                    {
                        if (!_cardRunMarkerFilters.TryGetValue(update.RunId, out var markerFilter))
                            _cardRunMarkerFilters[update.RunId] = markerFilter = new CitationMarkerStreamFilter();
                        visible = markerFilter.Push(update.Delta);
                    }
                    await SendCardDeltaAsync(update.RunId, visible);
                    break;
                case "message.completed" when update.RunId is not null:
                case "message.interrupted" when update.RunId is not null:
                    string tail = "";
                    lock (_stateLock)
                    {
                        if (_cardRunMarkerFilters.Remove(update.RunId, out var markerFilter)) tail = markerFilter.Flush();
                    }
                    await SendCardDeltaAsync(update.RunId, tail);
                    int count;
                    lock (_stateLock)
                    {
                        count = _cardRunBytes.GetValueOrDefault(update.RunId);
                        _cardRunBytes.Remove(update.RunId);
                    }
                    var done = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(done, (uint)count);
                    await _ble.SendAsync(MessageType.ChatDone, done, _lifetime.Token);
                    SetStatus(update.Type == "message.interrupted" ? "回答已打断" : "回答完成", "可继续提问");
                    break;
                case "run.failed":
                    if (update.RunId is not null) lock (_stateLock) _cardRunMarkerFilters.Remove(update.RunId);
                    await _ble.SendAsync(MessageType.ChatError, CatProtocol.Utf8("服务暂时不可用，请稍后重试", 256), _lifetime.Token);
                    SetStatus("AI 服务错误", update.Error ?? "服务暂时不可用");
                    break;
                case "conversation.activated" when _conversations.DeviceFollowDesktop:
                case "device.binding":
                    await _ble.SendAsync(MessageType.SessionReset, ReadOnlyMemory<byte>.Empty, _lifetime.Token);
                    SetStatus("卡片已就绪", "已跟随电脑当前对话");
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine($"{DateTimeOffset.Now:O} card relay failed: {error.Message}"); }
    }

    private Task SendCardDeltaAsync(string runId, string text)
    {
        var filtered = DisplayText.Filter(text);
        if (filtered.Length == 0) return Task.CompletedTask;
        byte[] payload;
        lock (_stateLock)
        {
            var used = _cardRunBytes.GetValueOrDefault(runId);
            var remaining = CatProtocol.AnswerMaxBytes - used;
            if (remaining <= 0) return Task.CompletedTask;
            payload = Utf8Prefix(filtered, remaining);
            if (payload.Length == 0) return Task.CompletedTask;
            _cardRunBytes[runId] = used + payload.Length;
        }
        return _ble.SendAsync(MessageType.ChatDelta, payload, _lifetime.Token);
    }

    private static byte[] Utf8Prefix(string value, int maximumBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maximumBytes) return bytes;
        var length = maximumBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return bytes[..length];
    }

    private void OnLinkStateChanged(object? sender, string state) => SetDetail(state);
    private void OnControlMessage(object? sender, ControlMessage message)
    {
        if (message.Type == MessageType.Pong)
        {
            AcceptPong(message.Payload);
            return;
        }
        if (message.Type == MessageType.Ping)
        {
            _ = ReplyToPingAsync(message.Payload);
            return;
        }
        if (!_controlMessages.Writer.TryWrite(message))
            Console.Error.WriteLine($"{DateTimeOffset.Now:O} control message queue closed; dropped {message.Type}");
    }

    private async Task ReplyToPingAsync(byte[] payload)
    {
        try { await _ble.SendAsync(MessageType.Pong, payload, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine($"{DateTimeOffset.Now:O} PONG send failed: {error.Message}"); }
    }

    private async Task ProcessControlMessagesAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in _controlMessages.Reader.ReadAllAsync(token))
                await HandleControlAsync(message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void OnConnectionLost(object? sender, EventArgs args)
    {
        HandleConnectionLoss("连接已中断，正在自动恢复");
    }

    private async Task HandleControlAsync(ControlMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.Hello: await HandleHelloAsync(message.Payload); break;
                case MessageType.RecordStart: BeginRecording(message.Payload); break;
                case MessageType.RecordEnd: StartCardOperation(token => EndRecordingAsync(message.Payload, token)); break;
                case MessageType.RecordCancel:
                case MessageType.TranscriptCancel: CancelCardTurn("本轮已取消"); break;
                case MessageType.TranscriptRetry: CancelCardTurn("请重新录音"); break;
                case MessageType.TranscriptConfirm: StartCardOperation(token => ConfirmTranscriptAsync(ReadId(message.Payload), token)); break;
                case MessageType.NewConversation: await ResetConversationAsync(message.Payload, true); break;
                case MessageType.CancelTurn: CancelCardTurn("生成已停止"); break;
            }
        }
        catch (Exception error)
        {
            if (message.Type == MessageType.Hello)
            {
                _handshakeReady?.TrySetException(error);
                SetLinkReady(false);
                await _ble.DisconnectAsync();
            }
            else await ReportTurnErrorAsync(error);
        }
    }

    private async Task HandleHelloAsync(byte[] payload)
    {
        using var hello = JsonDocument.Parse(payload);
        if (!hello.RootElement.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 1)
            throw new ProtocolException("卡片协议版本不兼容。");
        var ready = JsonSerializer.SerializeToUtf8Bytes(new { app = "windows", protocol = 1, asr = "dashscope", chat = "dashscope" });
        await _ble.SendAsync(MessageType.AppReady, ready, _lifetime.Token);
        await SendAppStatusAsync();
        await SendUInt32Async(MessageType.TimeSync, (uint)DateTime.Now.TimeOfDay.TotalSeconds);
        await SendUInt32Async(MessageType.ScreenTimeout, Settings.ScreenTimeoutSeconds);
        SetLinkReady(true);
        SetStatus("卡片已就绪", $"握手完成，PDU {_ble.MaxPduSize}（要求 ≥177）");
        _handshakeReady?.TrySetResult(true);
    }

    private void BeginRecording(byte[] payload)
    {
        if (payload.Length != 9) throw new ProtocolException("RECORD_START 负载长度错误。");
        var id = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2));
        if (sampleRate != 16000 || payload[8] != 1) throw new ProtocolException("不支持的音频格式。");
        while (_audioArrived.Wait(0)) { }
        lock (_audioLock)
        {
            _recordingId = id;
            _recordings.Begin(id, checked((int)sampleRate));
            _discardedRecordingId = null;
            _discardedRecordingMessage = null;
        }
        _turns.StartCard();
        SetStatus("正在录音", $"录音 #{id}");
    }

    private void OnAudioFrame(object? sender, byte[] packet)
    {
        AudioFrame? frame = null;
        try
        {
            frame = ImaAdpcmDecoder.Decode(packet);
            lock (_audioLock) _recordings.Add(frame);
            _audioArrived.Release();
        }
        catch (Exception error)
        {
            var message = $"音频错误：{error.Message}";
            lock (_audioLock)
            {
                _discardedRecordingId = frame?.RecordingId ?? _recordingId;
                _discardedRecordingMessage = message;
                _recordings.Cancel(_discardedRecordingId);
            }
            _audioArrived.Release();
            SetStatus("录音失败", message);
        }
    }

    private async Task EndRecordingAsync(byte[] payload, CancellationToken token)
    {
        if (payload.Length != 8) throw new ProtocolException("RECORD_END 负载长度错误。");
        var id = ReadId(payload);
        var advertisedFrames = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6));
        RecordingAssembler? recording = null;
        string? discardedMessage = null;
        var deadline = DateTimeOffset.UtcNow + AudioDrainTimeout;
        while (recording is null && discardedMessage is null)
        {
            lock (_audioLock)
            {
                if (_discardedRecordingId == id)
                {
                    discardedMessage = _discardedRecordingMessage ?? "本轮已在 Windows 客户端取消";
                    _discardedRecordingId = null;
                    _discardedRecordingMessage = null;
                    _recordingId = null;
                }
                else if (_recordingId == id)
                {
                    _recordings.TryComplete(id, advertisedFrames, out recording);
                }
                else
                {
                    throw new ProtocolException(
                        $"录音结束 ID 与当前录音不匹配（结束 #{id}，当前 #{_recordingId?.ToString() ?? "无"}）。");
                }
            }

            if (recording is not null || discardedMessage is not null) break;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _audioArrived.WaitAsync(remaining, token))
            {
                int received;
                lock (_audioLock) received = _recordings.ActiveFrameCount;
                throw new ProtocolException($"录音帧接收超时，收到 {received}，卡片声明 {advertisedFrames}。");
            }
        }
        if (discardedMessage is not null)
        {
            SetStatus("录音失败", discardedMessage);
            await _ble.SendAsync(MessageType.AsrError, CatProtocol.Utf8(discardedMessage, 256), _lifetime.Token);
            return;
        }
        await _ble.SendAsync(MessageType.AsrStarted, IdPayload(id), token);
        SetStatus("正在识别", "正在向 DashScope 提交音频…");
        _transcript = DisplayText.Filter(await _cloud.TranscribeAsync(recording!.ToWav(), Settings.AsrModel, _secureStore.Load() ?? "", token));
        if (string.IsNullOrWhiteSpace(_transcript)) throw new CloudException(CloudError.NoSpeech, "语音识别结果不包含卡片可显示的文字。");
        await _ble.SendAsync(MessageType.AsrResult, CatProtocol.Utf8(_transcript, CatProtocol.TranscriptMaxBytes), token);
        SetStatus("等待卡片确认", _transcript);
    }

    private async Task ConfirmTranscriptAsync(ushort id, CancellationToken token)
    {
        if (_recordingId != id || string.IsNullOrWhiteSpace(_transcript)) throw new ProtocolException("没有可确认的转写结果。");
        var conversationId = _conversations.DeviceActiveConversationId ?? throw new InvalidOperationException("卡片没有活动对话。");
        await _conversations.SendAsync(conversationId, _transcript, "device");
        _transcript = null; _recordingId = null;
    }

    private async Task ResetConversationAsync(byte[] payload, bool notifyCard)
    {
        CancelAllTurns("已开始新对话");
        await _conversations.CreateForDeviceAsync();
        if (notifyCard) await _ble.SendAsync(MessageType.SessionReset, payload.Length == 4 ? payload : [], _lifetime.Token);
    }
    private async Task RunCardOperationAsync(Func<CancellationToken, Task> operation)
    {
        var token = _turns.CardToken();
        try { await operation(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void StartCardOperation(Func<CancellationToken, Task> operation) =>
        _ = ObserveCardOperationAsync(operation);

    private async Task ObserveCardOperationAsync(Func<CancellationToken, Task> operation)
    {
        try { await RunCardOperationAsync(operation); }
        catch (Exception error) { await ReportTurnErrorAsync(error); }
    }

    private void CancelCardTurn(string status)
    {
        _turns.CancelCard();
        if (_conversations.DeviceActiveConversationId is { } conversationId) _ = _conversations.CancelAsync(conversationId);
        lock (_audioLock) _recordings.Cancel(_recordingId);
        _transcript = null;
        SetStatus(status, CurrentDetail());
    }

    private void CancelAllTurns(string status)
    {
        _turns.CancelAll();
        lock (_audioLock)
        {
            if (_recordings.ActiveRecordingId is not null)
            {
                _discardedRecordingId = _recordingId;
                _discardedRecordingMessage = "本轮已在 Windows 客户端取消";
                _recordings.Cancel(_recordingId);
            }
        }
        _transcript = null;
        SetStatus(status, CurrentDetail());
    }

    private async Task ReportTurnErrorAsync(Exception error)
    {
        if (error is OperationCanceledException && _lifetime.IsCancellationRequested) return;
        var type = CurrentStatus().Contains("AI", StringComparison.Ordinal) ? MessageType.ChatError : MessageType.AsrError;
        ShowError(error);
        var publicMessage = ErrorMessageForCard(error);
        if (IsLinkReady()) try { await _ble.SendAsync(type, CatProtocol.Utf8(publicMessage, 256), _lifetime.Token); } catch { }
    }

    internal static string ErrorMessageForCard(Exception error) => error switch
    {
        CloudException { Kind: CloudError.NoSpeech } => "没有听到清晰语音，请按住 OK 说完后再松开",
        CloudException { Kind: CloudError.ApiKeyMissing or CloudError.ApiKeyInvalid } => "请在 Windows 客户端检查 API Key",
        _ => "服务暂时不可用，请稍后重试",
    };

    private void ShowError(Exception error)
    {
        Console.Error.WriteLine($"{DateTimeOffset.Now:O} {error}");
        var status = error switch { LinkException link => $"蓝牙错误：{link.Kind}", CloudException cloud => $"云服务错误：{cloud.Kind}", TaskCanceledException => "云服务超时", HttpRequestException => "网络错误", SocketException => "网络错误", _ => "发生错误" };
        var detail = string.IsNullOrWhiteSpace(error.Message) ? $"{error.GetType().Name}（0x{error.HResult:X8}）" : error.Message;
        SetStatus(status, detail);
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _reconnectTimer.WaitForNextTickAsync(token))
                if (Settings.AutoReconnect && !IsLinkReady() && Settings.LastBluetoothAddress is ulong address &&
                    DateTimeOffset.UtcNow >= _nextReconnectAt)
                    await ConnectAsync(address, Settings.LastDeviceName, automatic: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task MonitorLinkHealthAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                uint? ping = null;
                var timedOut = false;
                var now = DateTimeOffset.UtcNow;
                lock (_stateLock)
                {
                    if (!_linkReady) continue;
                    if (_pendingPing is not null && now - _pendingPingAt >= PongTimeout)
                    {
                        timedOut = true;
                    }
                    else if (_pendingPing is null && now - _lastPingAt >= PingInterval)
                    {
                        ping = unchecked((uint)Environment.TickCount64);
                        _pendingPing = ping;
                        _pendingPingAt = now;
                        _lastPingAt = now;
                    }
                }

                if (timedOut)
                {
                    HandleConnectionLoss("卡片心跳超时，正在自动恢复");
                    await _ble.DisconnectAsync();
                    continue;
                }
                if (ping is not uint tokenValue) continue;
                var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, tokenValue);
                try { await _ble.SendAsync(MessageType.Ping, payload, token); }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    HandleConnectionLoss("蓝牙健康检查失败，正在自动恢复");
                    await _ble.DisconnectAsync();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void AcceptPong(byte[] payload)
    {
        if (payload.Length != 4) return;
        var token = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        lock (_stateLock)
        {
            if (_pendingPing == token) _pendingPing = null;
        }
    }

    private void HandleConnectionLoss(string status)
    {
        SetLinkReady(false);
        _handshakeReady?.TrySetException(new LinkException(LinkError.Disconnected, "卡片在握手期间断开。"));
        CancelAllTurns(status);
        _nextReconnectAt = DateTimeOffset.UtcNow;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume && Settings.AutoReconnect && !IsLinkReady())
            _nextReconnectAt = DateTimeOffset.UtcNow;
    }
    private void OnNetworkStatusChanged(object sender) { if (IsLinkReady()) _ = SendAppStatusIgnoringFailureAsync(); }
    private async Task SendAppStatusIgnoringFailureAsync() { try { await SendAppStatusAsync(); } catch { } }
    private Task SendUInt32Async(MessageType type, uint value) { var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, value); return _ble.SendAsync(type, payload, _lifetime.Token); }
    private Task SendAppStatusAsync()
    {
        var online = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        return _ble.SendAsync(MessageType.AppStatus, JsonSerializer.SerializeToUtf8Bytes(new { network = online ? "online" : "offline", api_key = string.IsNullOrWhiteSpace(_secureStore.Load()) ? "missing" : "configured" }), _lifetime.Token);
    }

    private void SetStatus(string status, string detail) { lock (_stateLock) { _status = status; _detail = detail; } PublishState(); }
    private void SetDetail(string detail) { lock (_stateLock) _detail = detail; PublishState(); }
    private void SetLinkReady(bool ready)
    {
        lock (_stateLock)
        {
            _linkReady = ready;
            _pendingPing = null;
            _pendingPingAt = DateTimeOffset.MinValue;
            _lastPingAt = ready ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
        }
    }
    private bool IsLinkReady() { lock (_stateLock) return _linkReady; }
    private void ScheduleReconnectAfterFailure()
    {
        if (!Settings.AutoReconnect || Settings.LastBluetoothAddress is null) return;
        _reconnectFailures++;
        _nextReconnectAt = DateTimeOffset.UtcNow + ReconnectDelayForFailure(_reconnectFailures);
    }
    private string CurrentStatus() { lock (_stateLock) return _status; }
    private string CurrentDetail() { lock (_stateLock) return _detail; }
    private void PublishState() => StateChanged?.Invoke(this, Snapshot());
    private static ushort ReadId(byte[] payload) => payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : throw new ProtocolException("消息缺少录音 ID。");
    private static byte[] IdPayload(ushort id) { var payload = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(payload, id); return payload; }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _ble.ConnectionLost -= OnConnectionLost;
        _conversations.Changed -= OnConversationChanged;
        _lifetime.Cancel(); _controlMessages.Writer.TryComplete(); _turns.CancelAll(); _reconnectTimer.Dispose();
        try { await Task.WhenAll(_controlPump, _linkHealthPump); } catch (OperationCanceledException) { }
        await _conversations.DisposeAsync();
        await _ble.DisposeAsync(); _cloud.Dispose(); _workSandbox.Dispose(); _turns.Dispose(); _lifetime.Dispose(); _audioArrived.Dispose(); _connectionGate.Dispose();
    }
}
