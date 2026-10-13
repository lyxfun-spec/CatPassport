using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CatAssistant.Backend.Models;
using CatAssistant.Core.Audio;
using CatAssistant.Core.Protocol;
using CatAssistant.Core.Sessions;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace CatAssistant.Backend.Services;

public sealed class AssistantCoordinator : IAsyncDisposable
{
    private readonly BleGateway _ble;
    private readonly DashScopeClient _cloud;
    private readonly SqliteRepository _repository;
    private readonly SecureApiKeyStore _secureStore;
    private readonly SessionPolicy _sessionPolicy = new(TimeSpan.FromMinutes(30));
    private readonly PeriodicTimer _reconnectTimer = new(TimeSpan.FromSeconds(10));
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private readonly Dictionary<ulong, CatDevice> _devices = [];
    private readonly List<ChatMessage> _messages = [];
    private readonly object _stateLock = new();
    private readonly object _audioLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InteractionTurns _turns;
    private RecordingAssembler? _recording;
    private ushort? _recordingId;
    private ushort? _discardedRecordingId;
    private string? _discardedRecordingMessage;
    private string? _transcript;
    private long _conversationId;
    private string _status = "未连接";
    private string _detail = "请扫描并选择小猫助手卡片";

    public AssistantCoordinator(BleGateway ble, DashScopeClient cloud, SqliteRepository repository, SecureApiKeyStore secureStore, AppSettings settings)
    {
        _ble = ble; _cloud = cloud; _repository = repository; _secureStore = secureStore; Settings = settings;
        _turns = new InteractionTurns(_lifetime.Token);
        _ble.DeviceFound += OnDeviceFound; _ble.ControlMessageReceived += OnControlMessage; _ble.AudioFrameReceived += OnAudioFrame;
        _ble.LinkStateChanged += OnLinkStateChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public AppSettings Settings { get; }
    public event EventHandler<BackendSnapshot>? StateChanged;
    public event EventHandler<StreamUpdate>? StreamChanged;

    public async Task StartAsync()
    {
        var latest = await _repository.LoadLatestConversationAsync();
        if (latest is { } saved)
        {
            _conversationId = saved.Id;
            var savedMessages = await _repository.LoadMessagesAsync(saved.Id);
            lock (_stateLock) _messages.AddRange(savedMessages);
            if (DateTimeOffset.UtcNow - saved.LastAt < TimeSpan.FromMinutes(30)) _sessionPolicy.Complete(saved.LastAt);
        }
        PublishState();
        _ = ReconnectLoopAsync(_lifetime.Token);
        if (Settings.AutoReconnect && Settings.LastBluetoothAddress is ulong address)
            await ConnectAsync(address, Settings.LastDeviceName ?? "FoloToy Cat");
        else await ScanAsync();
    }

    public BackendSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return new BackendSnapshot(
                _status, _detail, _ble.IsConnected, _ble.PairedDeviceAddress is not null || Settings.LastBluetoothAddress is not null, _ble.MaxPduSize,
                _devices.Values.OrderByDescending(device => device.Rssi).ToArray(),
                new PublicSettings(Settings.AsrModel, Settings.ChatModel, Settings.ScreenTimeoutSeconds, Settings.AutoReconnect, !string.IsNullOrWhiteSpace(_secureStore.Load())),
                _conversationId == 0 ? null : _conversationId,
                _messages.ToArray());
        }
    }

    public async Task ScanAsync()
    {
        lock (_stateLock) _devices.Clear();
        SetStatus("扫描中", "正在查找 FoloToy Cat…");
        try { await _ble.StartScanAsync(); } catch (Exception error) { ShowError(error); }
    }

    public async Task ConnectAsync(ulong address, string? name = null)
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_ble.IsConnected) return;
            SetStatus("正在连接", "正在建立加密 BLE 连接…");
            CatDevice device;
            lock (_stateLock) device = _devices.GetValueOrDefault(address) ?? new CatDevice(address, name ?? "FoloToy Cat", 0);
            await _ble.ConnectAsync(device, _lifetime.Token);
            Settings.LastBluetoothAddress = device.Address; Settings.LastDeviceName = device.Name;
            await _repository.SaveSettingsAsync(Settings);
            SetStatus("已连接，等待卡片握手", $"已连接 {device.Name}");
        }
        catch (Exception error)
        {
            await _ble.DisconnectAsync();
            try { await _ble.StartScanAsync(); } catch { }
            ShowError(error);
            PublishState();
        }
        finally { _connectionGate.Release(); }
    }

    public async Task ForgetAsync()
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        try
        {
            CancelAllTurns("设备已忘记"); await _ble.ForgetAsync(Settings.LastBluetoothAddress);
            Settings.LastBluetoothAddress = null; Settings.LastDeviceName = null;
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
            var alias = unchecked((uint)Environment.TickCount64);
            var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, alias);
            await ResetConversationAsync(payload, _ble.IsConnected);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task CancelCurrentTurnAsync()
    {
        try
        {
            var type = CurrentStatus().Contains("AI", StringComparison.Ordinal) ? MessageType.ChatError : MessageType.AsrError;
            CancelAllTurns("本轮已取消");
            if (_ble.IsConnected) await _ble.SendAsync(type, CatProtocol.Utf8("已在 Windows 客户端取消", 256), _lifetime.Token);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task SaveSettingsAsync(string? apiKey, string asrModel, string chatModel, uint screenTimeout, bool autoReconnect)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) _secureStore.Save(apiKey);
            Settings.AsrModel = asrModel.Trim(); Settings.ChatModel = chatModel.Trim(); Settings.ScreenTimeoutSeconds = screenTimeout; Settings.AutoReconnect = autoReconnect;
            await _repository.SaveSettingsAsync(Settings);
            if (_ble.IsConnected) { await SendUInt32Async(MessageType.ScreenTimeout, screenTimeout); await SendAppStatusAsync(); }
            SetDetail("设置已保存");
        }
        catch (Exception error) { ShowError(error); throw; }
    }

    public async Task SendDesktopMessageAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("消息不能为空。", nameof(text));
        var token = _turns.StartDesktop();
        try { await RunChatAsync(new ChatMessage("user", text, DateTimeOffset.UtcNow), false, null, token); }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(error); throw; }
    }

    private void OnDeviceFound(object? sender, CatDevice device)
    {
        lock (_stateLock) _devices[device.Address] = device;
        PublishState();
    }

    private void OnLinkStateChanged(object? sender, string state) => SetDetail(state);
    private void OnControlMessage(object? sender, ControlMessage message) => _ = HandleControlAsync(message);

    private async Task HandleControlAsync(ControlMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.Hello: await HandleHelloAsync(message.Payload); break;
                case MessageType.RecordStart: BeginRecording(message.Payload); break;
                case MessageType.RecordEnd: await RunCardOperationAsync(token => EndRecordingAsync(message.Payload, token)); break;
                case MessageType.RecordCancel:
                case MessageType.TranscriptCancel: CancelCardTurn("本轮已取消"); break;
                case MessageType.TranscriptRetry: CancelCardTurn("请重新录音"); break;
                case MessageType.TranscriptConfirm: await RunCardOperationAsync(token => ConfirmTranscriptAsync(ReadId(message.Payload), token)); break;
                case MessageType.NewConversation: await ResetConversationAsync(message.Payload, true); break;
                case MessageType.CancelTurn: CancelCardTurn("生成已停止"); break;
                case MessageType.Ping: await _ble.SendAsync(MessageType.Pong, message.Payload, _lifetime.Token); break;
            }
        }
        catch (Exception error) { await ReportTurnErrorAsync(error); }
    }

    private async Task HandleHelloAsync(byte[] payload)
    {
        using var hello = JsonDocument.Parse(payload);
        var ready = JsonSerializer.SerializeToUtf8Bytes(new { app = "windows", protocol = 1, asr = "dashscope", chat = "dashscope" });
        await _ble.SendAsync(MessageType.AppReady, ready, _lifetime.Token);
        await SendAppStatusAsync();
        await SendUInt32Async(MessageType.TimeSync, (uint)DateTime.Now.TimeOfDay.TotalSeconds);
        await SendUInt32Async(MessageType.ScreenTimeout, Settings.ScreenTimeoutSeconds);
        SetStatus("卡片已就绪", $"握手完成，PDU {_ble.MaxPduSize}（要求 ≥177）");
    }

    private void BeginRecording(byte[] payload)
    {
        if (payload.Length != 9) throw new ProtocolException("RECORD_START 负载长度错误。");
        var id = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2));
        if (sampleRate != 16000 || payload[8] != 1) throw new ProtocolException("不支持的音频格式。");
        lock (_audioLock)
        {
            _recordingId = id;
            _recording = new RecordingAssembler(id, checked((int)sampleRate));
            _discardedRecordingId = null;
            _discardedRecordingMessage = null;
        }
        _turns.StartCard();
        SetStatus("正在录音", $"录音 #{id}");
    }

    private void OnAudioFrame(object? sender, byte[] packet)
    {
        try { lock (_audioLock) _recording?.Add(ImaAdpcmDecoder.Decode(packet)); }
        catch (Exception error)
        {
            _turns.CancelCard();
            lock (_audioLock)
            {
                _discardedRecordingId = _recordingId;
                _discardedRecordingMessage = $"音频错误：{error.Message}";
                _recording = null;
            }
            SetDetail(_discardedRecordingMessage);
        }
    }

    private async Task EndRecordingAsync(byte[] payload, CancellationToken token)
    {
        if (payload.Length != 8) throw new ProtocolException("RECORD_END 负载长度错误。");
        var id = ReadId(payload); RecordingAssembler? recording = null; string? discardedMessage = null;
        lock (_audioLock)
        {
            if (_recordingId == id && _recording is not null)
            {
                recording = _recording;
                _recording = null;
            }
            else if (_discardedRecordingId == id)
            {
                discardedMessage = _discardedRecordingMessage ?? "本轮已在 Windows 客户端取消";
                _discardedRecordingId = null;
                _discardedRecordingMessage = null;
            }
            else
            {
                throw new ProtocolException($"录音结束 ID 与当前录音不匹配（结束 #{id}，当前 #{_recordingId?.ToString() ?? "无"}）。");
            }
        }
        if (discardedMessage is not null)
        {
            await _ble.SendAsync(MessageType.AsrError, CatProtocol.Utf8(discardedMessage, 256), _lifetime.Token);
            return;
        }
        var advertisedFrames = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6));
        if (recording!.FrameCount != advertisedFrames) throw new ProtocolException($"录音帧数不符，收到 {recording.FrameCount}，卡片声明 {advertisedFrames}。");
        await _ble.SendAsync(MessageType.AsrStarted, IdPayload(id), token);
        SetStatus("正在识别", "正在向 DashScope 提交音频…");
        _transcript = DisplayText.Filter(await _cloud.TranscribeAsync(recording.ToWav(), Settings.AsrModel, _secureStore.Load() ?? "", token));
        if (string.IsNullOrWhiteSpace(_transcript)) throw new CloudException(CloudError.Service, "语音识别结果不包含卡片可显示的文字。");
        await _ble.SendAsync(MessageType.AsrResult, CatProtocol.Utf8(_transcript, CatProtocol.TranscriptMaxBytes), token);
        SetStatus("等待卡片确认", _transcript);
    }

    private async Task ConfirmTranscriptAsync(ushort id, CancellationToken token)
    {
        if (_recordingId != id || string.IsNullOrWhiteSpace(_transcript)) throw new ProtocolException("没有可确认的转写结果。");
        await RunChatAsync(new ChatMessage("user", _transcript, DateTimeOffset.UtcNow), true, id, token);
        _transcript = null; _recordingId = null;
    }

    private async Task RunChatAsync(ChatMessage user, bool sendToCard, ushort? recordingId, CancellationToken token)
    {
        await _chatGate.WaitAsync(token);
        try
        {
            if (_sessionPolicy.ShouldStartNew(DateTimeOffset.UtcNow) || _conversationId == 0) await CreateConversationAsync(DateTimeOffset.UtcNow);
            await _repository.AddMessageAsync(_conversationId, user); AddMessage(user);
            var history = await _repository.LoadMessagesAsync(_conversationId);
            if (sendToCard) await _ble.SendAsync(MessageType.ChatStarted, IdPayload(recordingId!.Value), token);
            SetStatus("AI 正在回答", user.Content);
            StreamChanged?.Invoke(this, new StreamUpdate(_conversationId, "started", Message: user));
            var answer = new StringBuilder();
            await foreach (var raw in _cloud.StreamChatAsync(history, Settings.ChatModel, _secureStore.Load() ?? "", token))
            {
                var delta = sendToCard ? DisplayText.Filter(raw) : raw;
                if (delta.Length == 0) continue;
                answer.Append(delta);
                if (sendToCard && Encoding.UTF8.GetByteCount(answer.ToString()) > CatProtocol.AnswerMaxBytes) throw new ProtocolException("AI 回复超过卡片 12 KiB 限制。");
                if (sendToCard) await _ble.SendAsync(MessageType.ChatDelta, Encoding.UTF8.GetBytes(delta), token);
                StreamChanged?.Invoke(this, new StreamUpdate(_conversationId, "delta", delta));
            }
            if (sendToCard)
            {
                var done = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(done, (uint)Encoding.UTF8.GetByteCount(answer.ToString()));
                await _ble.SendAsync(MessageType.ChatDone, done, token);
            }
            var assistant = new ChatMessage("assistant", answer.ToString(), DateTimeOffset.UtcNow);
            await _repository.AddMessageAsync(_conversationId, assistant); AddMessage(assistant);
            _sessionPolicy.Complete(DateTimeOffset.UtcNow);
            SetStatus("回答完成", answer.ToString());
            StreamChanged?.Invoke(this, new StreamUpdate(_conversationId, "completed", Message: assistant));
        }
        finally { _chatGate.Release(); }
    }

    private async Task ResetConversationAsync(byte[] payload, bool notifyCard)
    {
        CancelAllTurns("已开始新对话");
        await _chatGate.WaitAsync(_lifetime.Token);
        try
        {
            await CreateConversationAsync(DateTimeOffset.UtcNow);
            if (notifyCard) await _ble.SendAsync(MessageType.SessionReset, payload.Length == 4 ? payload : [], _lifetime.Token);
        }
        finally { _chatGate.Release(); }
    }

    private async Task CreateConversationAsync(DateTimeOffset now)
    {
        _conversationId = await _repository.NewConversationAsync(now); _sessionPolicy.Complete(now);
        lock (_stateLock) _messages.Clear(); PublishState();
    }

    private void AddMessage(ChatMessage message) { lock (_stateLock) _messages.Add(message); PublishState(); }
    private async Task RunCardOperationAsync(Func<CancellationToken, Task> operation)
    {
        var token = _turns.CardToken();
        try { await operation(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CancelCardTurn(string status)
    {
        _turns.CancelCard();
        lock (_audioLock) _recording = null;
        _transcript = null;
        SetStatus(status, CurrentDetail());
    }

    private void CancelAllTurns(string status)
    {
        _turns.CancelAll();
        lock (_audioLock)
        {
            if (_recording is not null)
            {
                _discardedRecordingId = _recordingId;
                _discardedRecordingMessage = "本轮已在 Windows 客户端取消";
                _recording = null;
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
        var publicMessage = error is CloudException { Kind: CloudError.ApiKeyMissing or CloudError.ApiKeyInvalid } ? "请在 Windows 客户端检查 API Key" : "服务暂时不可用，请稍后重试";
        if (_ble.IsConnected) try { await _ble.SendAsync(type, CatProtocol.Utf8(publicMessage, 256), _lifetime.Token); } catch { }
    }

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
                if (Settings.AutoReconnect && !_ble.IsConnected && Settings.LastBluetoothAddress is ulong address) await ConnectAsync(address, Settings.LastDeviceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume && Settings.AutoReconnect && !_ble.IsConnected && Settings.LastBluetoothAddress is ulong address) _ = ConnectAsync(address, Settings.LastDeviceName);
    }
    private void OnNetworkStatusChanged(object sender) { if (_ble.IsConnected) _ = SendAppStatusIgnoringFailureAsync(); }
    private async Task SendAppStatusIgnoringFailureAsync() { try { await SendAppStatusAsync(); } catch { } }
    private Task SendUInt32Async(MessageType type, uint value) { var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, value); return _ble.SendAsync(type, payload, _lifetime.Token); }
    private Task SendAppStatusAsync()
    {
        var online = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        return _ble.SendAsync(MessageType.AppStatus, JsonSerializer.SerializeToUtf8Bytes(new { network = online ? "online" : "offline", api_key = string.IsNullOrWhiteSpace(_secureStore.Load()) ? "missing" : "configured" }), _lifetime.Token);
    }

    private void SetStatus(string status, string detail) { lock (_stateLock) { _status = status; _detail = detail; } PublishState(); }
    private void SetDetail(string detail) { lock (_stateLock) _detail = detail; PublishState(); }
    private string CurrentStatus() { lock (_stateLock) return _status; }
    private string CurrentDetail() { lock (_stateLock) return _detail; }
    private void PublishState() => StateChanged?.Invoke(this, Snapshot());
    private static ushort ReadId(byte[] payload) => payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : throw new ProtocolException("消息缺少录音 ID。");
    private static byte[] IdPayload(ushort id) { var payload = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(payload, id); return payload; }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _lifetime.Cancel(); _turns.CancelAll(); _reconnectTimer.Dispose(); await _ble.DisposeAsync(); _cloud.Dispose(); _turns.Dispose(); _lifetime.Dispose(); _connectionGate.Dispose(); _chatGate.Dispose();
    }
}
