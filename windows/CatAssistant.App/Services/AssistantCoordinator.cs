using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CatAssistant.App.Models;
using CatAssistant.Core.Audio;
using CatAssistant.Core.Protocol;
using CatAssistant.Core.Sessions;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace CatAssistant.App.Services;

public sealed class AssistantCoordinator : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly BleGateway _ble;
    private readonly DashScopeClient _cloud;
    private readonly SqliteRepository _repository;
    private readonly SecureApiKeyStore _secureStore;
    private readonly SessionPolicy _sessionPolicy = new(TimeSpan.FromMinutes(30));
    private readonly PeriodicTimer _reconnectTimer = new(TimeSpan.FromSeconds(10));
    private readonly Dictionary<ulong, CatDevice> _devices = [];
    private readonly object _audioLock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _turn;
    private RecordingAssembler? _recording;
    private ushort? _recordingId;
    private string? _transcript;
    private long _conversationId;
    private string _status = "未连接";
    private string _detail = "请扫描并选择小猫助手卡片";

    public AssistantCoordinator(BleGateway ble, DashScopeClient cloud, SqliteRepository repository, SecureApiKeyStore secureStore, AppSettings settings)
    {
        _ble = ble; _cloud = cloud; _repository = repository; _secureStore = secureStore; Settings = settings;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("协调器必须在 UI 线程创建。");
        _ble.DeviceFound += OnDeviceFound; _ble.ControlMessageReceived += OnControlMessage; _ble.AudioFrameReceived += OnAudioFrame;
        _ble.LinkStateChanged += (_, state) => { Detail = state; OnPropertyChanged(nameof(MaxPduText)); OnPropertyChanged(nameof(IsConnected)); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CatDevice> Devices { get; } = [];
    public AppSettings Settings { get; }
    public bool IsConnected => _ble.IsConnected;
    public string MaxPduText => _ble.MaxPduSize == 0 ? "PDU：--" : $"PDU：{_ble.MaxPduSize}（要求 ≥177）";
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_secureStore.Load());
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public async Task StartAsync()
    {
        var latest = await _repository.LoadLatestConversationAsync();
        if (latest is { } saved && DateTimeOffset.UtcNow - saved.LastAt < TimeSpan.FromMinutes(30))
        {
            _conversationId = saved.Id;
            _sessionPolicy.Complete(saved.LastAt);
        }
        _ = ReconnectLoopAsync(_lifetime.Token);
        if (Settings.AutoReconnect && Settings.LastBluetoothAddress is ulong address)
            await ConnectAsync(new CatDevice(address, Settings.LastDeviceName ?? "FoloToy Cat", 0));
        else await ScanAsync();
    }

    public async Task ScanAsync()
    {
        _devices.Clear(); Devices.Clear(); Status = "扫描中";
        try { await _ble.StartScanAsync(); } catch (Exception error) { ShowError(error); }
    }

    public async Task ConnectAsync(CatDevice device)
    {
        Status = "正在连接";
        try
        {
            await _ble.ConnectAsync(device, _lifetime.Token);
            Settings.LastBluetoothAddress = device.Address; Settings.LastDeviceName = device.Name;
            await _repository.SaveSettingsAsync(Settings);
            Status = "已连接，等待卡片握手";
            OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(MaxPduText));
        }
        catch (Exception error) { ShowError(error); await _ble.DisconnectAsync(); OnPropertyChanged(nameof(IsConnected)); }
    }

    public async Task ForgetAsync()
    {
        try
        {
            _turn?.Cancel();
            await _ble.ForgetAsync();
            Settings.LastBluetoothAddress = null; Settings.LastDeviceName = null;
            await _repository.SaveSettingsAsync(Settings);
            Status = "设备已忘记"; Detail = "请重新扫描并配对"; OnPropertyChanged(nameof(IsConnected));
            await ScanAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task NewConversationAsync()
    {
        if (!_ble.IsConnected) { Status = "尚未连接"; Detail = "连接卡片后才能同步新对话。"; return; }
        try
        {
            var alias = unchecked((uint)Environment.TickCount64);
            var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, alias);
            await ResetConversationAsync(payload);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task CancelCurrentTurnAsync()
    {
        try
        {
            var type = Status.Contains("AI", StringComparison.Ordinal) ? MessageType.ChatError : MessageType.AsrError;
            CancelTurn("本轮已取消");
            if (_ble.IsConnected)
                await _ble.SendAsync(type, CatProtocol.Utf8("已在 Windows 客户端取消", 256), _lifetime.Token);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task SaveSettingsAsync(string apiKey, string asrModel, string chatModel, uint screenTimeout, bool autoReconnect)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) _secureStore.Save(apiKey);
            Settings.AsrModel = asrModel.Trim(); Settings.ChatModel = chatModel.Trim(); Settings.ScreenTimeoutSeconds = screenTimeout; Settings.AutoReconnect = autoReconnect;
            await _repository.SaveSettingsAsync(Settings);
            if (_ble.IsConnected) { await SendUInt32Async(MessageType.ScreenTimeout, screenTimeout); await SendAppStatusAsync(); }
            OnPropertyChanged(nameof(HasApiKey)); Detail = "设置已保存";
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OnDeviceFound(object? sender, CatDevice device)
    {
        _dispatcher.TryEnqueue(() => AddOrUpdateDevice(device));
    }

    private void AddOrUpdateDevice(CatDevice device)
    {
        if (_devices.TryGetValue(device.Address, out var old)) Devices.Remove(old);
        _devices[device.Address] = device; Devices.Add(device);
    }

    private void OnControlMessage(object? sender, ControlMessage message) => _ = HandleControlAsync(message);

    private async Task HandleControlAsync(ControlMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.Hello:
                    await HandleHelloAsync(message.Payload); break;
                case MessageType.RecordStart:
                    BeginRecording(message.Payload); break;
                case MessageType.RecordEnd:
                    await EndRecordingAsync(message.Payload); break;
                case MessageType.RecordCancel:
                case MessageType.TranscriptCancel:
                    CancelTurn("本轮已取消"); break;
                case MessageType.TranscriptRetry:
                    CancelTurn("请重新录音"); break;
                case MessageType.TranscriptConfirm:
                    await ConfirmTranscriptAsync(ReadId(message.Payload)); break;
                case MessageType.NewConversation:
                    await ResetConversationAsync(message.Payload); break;
                case MessageType.CancelTurn:
                    CancelTurn("生成已停止"); break;
                case MessageType.Ping:
                    await _ble.SendAsync(MessageType.Pong, message.Payload, _lifetime.Token); break;
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
        Status = "卡片已就绪"; Detail = $"握手完成，{MaxPduText}";
    }

    private void BeginRecording(byte[] payload)
    {
        if (payload.Length != 9) throw new ProtocolException("RECORD_START 负载长度错误。");
        var id = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2));
        if (sampleRate != 16000 || payload[8] != 1) throw new ProtocolException("不支持的音频格式。");
        lock (_audioLock) { _recordingId = id; _recording = new RecordingAssembler(id, checked((int)sampleRate)); }
        _turn?.Cancel(); _turn = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Status = "正在录音"; Detail = $"录音 #{id}";
    }

    private void OnAudioFrame(object? sender, byte[] packet)
    {
        try { lock (_audioLock) _recording?.Add(ImaAdpcmDecoder.Decode(packet)); }
        catch (Exception error) { _turn?.Cancel(); lock (_audioLock) _recording = null; Detail = $"音频错误：{error.Message}"; }
    }

    private async Task EndRecordingAsync(byte[] payload)
    {
        if (payload.Length != 8) throw new ProtocolException("RECORD_END 负载长度错误。");
        var id = ReadId(payload);
        RecordingAssembler recording;
        lock (_audioLock)
        {
            if (_recordingId != id || _recording is null) throw new ProtocolException("录音结束 ID 与当前录音不匹配。");
            recording = _recording; _recording = null;
        }
        var advertisedFrames = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6));
        if (recording.FrameCount != advertisedFrames) throw new ProtocolException($"录音帧数不符，收到 {recording.FrameCount}，卡片声明 {advertisedFrames}。");
        await _ble.SendAsync(MessageType.AsrStarted, IdPayload(id), _turn!.Token);
        Status = "正在识别";
        _transcript = DisplayText.Filter(await _cloud.TranscribeAsync(recording.ToWav(), Settings.AsrModel, _secureStore.Load() ?? "", _turn.Token));
        if (string.IsNullOrWhiteSpace(_transcript)) throw new CloudException(CloudError.Service, "语音识别结果不包含卡片可显示的文字。");
        await _ble.SendAsync(MessageType.AsrResult, CatProtocol.Utf8(_transcript, CatProtocol.TranscriptMaxBytes), _turn.Token);
        Status = "等待卡片确认"; Detail = _transcript;
    }

    private async Task ConfirmTranscriptAsync(ushort id)
    {
        if (_recordingId != id || string.IsNullOrWhiteSpace(_transcript)) throw new ProtocolException("没有可确认的转写结果。");
        if (_sessionPolicy.ShouldStartNew(DateTimeOffset.UtcNow) || _conversationId == 0) _conversationId = await _repository.NewConversationAsync(DateTimeOffset.UtcNow);
        var user = new ChatMessage("user", _transcript, DateTimeOffset.UtcNow);
        await _repository.AddMessageAsync(_conversationId, user);
        var history = await _repository.LoadMessagesAsync(_conversationId);
        await _ble.SendAsync(MessageType.ChatStarted, IdPayload(id), _turn!.Token);
        Status = "AI 正在回答";
        var answer = new StringBuilder();
        await foreach (var raw in _cloud.StreamChatAsync(history, Settings.ChatModel, _secureStore.Load() ?? "", _turn.Token))
        {
            var delta = DisplayText.Filter(raw);
            if (delta.Length == 0) continue;
            answer.Append(delta);
            if (Encoding.UTF8.GetByteCount(answer.ToString()) > CatProtocol.AnswerMaxBytes) throw new ProtocolException("AI 回复超过卡片 12 KiB 限制。");
            await _ble.SendAsync(MessageType.ChatDelta, Encoding.UTF8.GetBytes(delta), _turn.Token);
        }
        var byteCount = Encoding.UTF8.GetByteCount(answer.ToString());
        var done = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(done, (uint)byteCount);
        await _ble.SendAsync(MessageType.ChatDone, done, _turn.Token);
        await _repository.AddMessageAsync(_conversationId, new ChatMessage("assistant", answer.ToString(), DateTimeOffset.UtcNow));
        _sessionPolicy.Complete(DateTimeOffset.UtcNow);
        _transcript = null; _recordingId = null;
        Status = "回答完成"; Detail = answer.ToString();
    }

    private async Task ResetConversationAsync(byte[] payload)
    {
        CancelTurn("已开始新对话");
        var now = DateTimeOffset.UtcNow;
        _conversationId = await _repository.NewConversationAsync(now);
        _sessionPolicy.Complete(now);
        await _ble.SendAsync(MessageType.SessionReset, payload.Length == 4 ? payload : [], _lifetime.Token);
    }

    private void CancelTurn(string status) { _turn?.Cancel(); lock (_audioLock) _recording = null; _transcript = null; Status = status; }

    private async Task ReportTurnErrorAsync(Exception error)
    {
        if (error is OperationCanceledException && (_turn?.IsCancellationRequested == true || _lifetime.IsCancellationRequested)) return;
        var type = Status.Contains("AI", StringComparison.Ordinal) ? MessageType.ChatError : MessageType.AsrError;
        ShowError(error);
        var publicMessage = error is CloudException { Kind: CloudError.ApiKeyMissing or CloudError.ApiKeyInvalid }
            ? "请在 Windows 客户端检查 API Key" : "服务暂时不可用，请稍后重试";
        if (_ble.IsConnected) try { await _ble.SendAsync(type, CatProtocol.Utf8(publicMessage, 256), _lifetime.Token); } catch { }
    }

    private void ShowError(Exception error)
    {
        Status = error switch { LinkException link => $"蓝牙错误：{link.Kind}", CloudException cloud => $"云服务错误：{cloud.Kind}", TaskCanceledException => "云服务超时", HttpRequestException => "网络错误", SocketException => "网络错误", _ => "发生错误" };
        Detail = error.Message;
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _reconnectTimer.WaitForNextTickAsync(token))
            {
                if (!Settings.AutoReconnect || _ble.IsConnected || Settings.LastBluetoothAddress is not ulong address) continue;
                await ConnectAsync(new CatDevice(address, Settings.LastDeviceName ?? "FoloToy Cat", 0));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume && Settings.AutoReconnect && !_ble.IsConnected && Settings.LastBluetoothAddress is ulong address)
            _ = ConnectAsync(new CatDevice(address, Settings.LastDeviceName ?? "FoloToy Cat", 0));
    }

    private void OnNetworkStatusChanged(object sender)
    {
        if (_ble.IsConnected) _ = SendAppStatusIgnoringFailureAsync();
    }

    private async Task SendAppStatusIgnoringFailureAsync()
    {
        try { await SendAppStatusAsync(); } catch { }
    }

    private Task SendUInt32Async(MessageType type, uint value) { var payload = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, value); return _ble.SendAsync(type, payload, _lifetime.Token); }
    private Task SendAppStatusAsync()
    {
        var online = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { network = online ? "online" : "offline", api_key = HasApiKey ? "configured" : "missing" });
        return _ble.SendAsync(MessageType.AppStatus, payload, _lifetime.Token);
    }
    private static ushort ReadId(byte[] payload) => payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : throw new ProtocolException("消息缺少录音 ID。");
    private static byte[] IdPayload(ushort id) { var payload = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(payload, id); return payload; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (_dispatcher.HasThreadAccess) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        else _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _lifetime.Cancel(); _turn?.Cancel(); _reconnectTimer.Dispose(); await _ble.DisposeAsync(); _lifetime.Dispose(); _turn?.Dispose();
    }
}
