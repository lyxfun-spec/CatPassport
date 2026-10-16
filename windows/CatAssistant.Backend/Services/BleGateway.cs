using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CatAssistant.Backend.Models;
using CatAssistant.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace CatAssistant.Backend.Services;

public sealed record CatDevice(ulong Address, string Name, short Rssi)
{
    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((Address >> (i * 8)) & 0xFF).ToString("X2")));
}

public enum LinkError { BluetoothUnavailable, DeviceUnavailable, Pairing, Encryption, ServiceMissing, CharacteristicMissing, Subscription, MtuTooSmall, Handshake, BoundDeviceMismatch, Disconnected, Write }
public sealed class LinkException(LinkError kind, string message) : Exception(message) { public LinkError Kind { get; } = kind; }

public sealed class BleGateway : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = Guid.Parse("10f00000-ca71-1b91-a047-c1335b4e8f6d");
    private static readonly Guid ControlRxUuid = Guid.Parse("10f00001-ca71-1b91-a047-c1335b4e8f6d");
    private static readonly Guid ControlTxUuid = Guid.Parse("10f00002-ca71-1b91-a047-c1335b4e8f6d");
    private static readonly Guid AudioTxUuid = Guid.Parse("10f00003-ca71-1b91-a047-c1335b4e8f6d");
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ControlReassembler _reassembler = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattSession? _session;
    private GattCharacteristic? _controlRx;
    private GattCharacteristic? _controlTx;
    private GattCharacteristic? _audioTx;
    private ushort _nextMessageId = 1;
    private const int ControlWriteAttempts = 4;
    private const int PairingAttempts = 3;
    private const int ConnectionSetupAttempts = 3;
    private const int ServiceDiscoveryAttempts = 8;

    public bool IsConnected { get; private set; }
    public uint MaxPduSize => _session?.MaxPduSize ?? 0;
    public CatDevice? CurrentDevice { get; private set; }
    public ulong? PairedDeviceAddress { get; private set; }
    public event EventHandler<CatDevice>? DeviceFound;
    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<byte[]>? AudioFrameReceived;
    public event EventHandler<string>? LinkStateChanged;
    public event EventHandler? ConnectionLost;

    public async Task StartScanAsync()
    {
        var radio = await BluetoothAdapter.GetDefaultAsync();
        if (radio is null || !radio.IsLowEnergySupported) throw new LinkException(LinkError.BluetoothUnavailable, "此电脑没有可用的 Bluetooth LE 适配器。");
        StopScan();
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        _watcher.Received += OnAdvertisement;
        _watcher.Stopped += OnWatcherStopped;
        _watcher.Start();
        LinkStateChanged?.Invoke(this, "正在扫描小猫助手卡片…");
    }

    public void StopScan()
    {
        if (_watcher is null) return;
        _watcher.Received -= OnAdvertisement; _watcher.Stopped -= OnWatcherStopped; _watcher.Stop(); _watcher = null;
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        => LinkStateChanged?.Invoke(this, args.Error == BluetoothError.Success ? "扫描已停止" : $"蓝牙扫描错误：{args.Error}");

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? "FoloToy Cat" : args.Advertisement.LocalName;
        DeviceFound?.Invoke(this, new CatDevice(args.BluetoothAddress, name, args.RawSignalStrengthInDBm));
    }

    public async Task ConnectAsync(CatDevice selected, CancellationToken token)
    {
        await DisconnectAsync(); StopScan();
        for (var setupAttempt = 1; setupAttempt <= ConnectionSetupAttempts; setupAttempt++)
        {
            try
            {
                await ConnectAttemptAsync(selected, token);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await DisconnectAsync();
                throw;
            }
            catch (Exception error) when (setupAttempt < ConnectionSetupAttempts && IsRetryableSetupError(error))
            {
                Console.Error.WriteLine(
                    $"BLE connection setup attempt {setupAttempt} will restart: " +
                    $"{error.GetType().Name} (0x{error.HResult:X8}) {error.Message}");
                await DisconnectAsync();
                LinkStateChanged?.Invoke(this,
                    $"Windows 蓝牙状态正在刷新（{setupAttempt}/{ConnectionSetupAttempts - 1}），即将自动继续…");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * setupAttempt), token);
            }
            catch
            {
                await DisconnectAsync();
                throw;
            }
        }

        throw new LinkException(LinkError.DeviceUnavailable, "Windows 蓝牙连接多次初始化失败。");
    }

    private async Task ConnectAttemptAsync(CatDevice selected, CancellationToken token)
    {
        LinkStateChanged?.Invoke(this, "正在连接并检查配对…");
        await OpenDeviceAsync(selected.Address, token);
        Console.Error.WriteLine(
            $"BLE pairing target: id={_device!.DeviceInformation.Id}; kind={_device.DeviceInformation.Kind}; " +
            $"canPair={_device.DeviceInformation.Pairing.CanPair}; isPaired={_device.DeviceInformation.Pairing.IsPaired}; " +
            $"addressType={_device.BluetoothAddressType}");
        if (!_device.DeviceInformation.Pairing.IsPaired)
        {
            var pairedInThisAttempt = false;
            var pairing = _device.DeviceInformation.Pairing.Custom;
            void AcceptJustWorksPairing(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly) args.Accept();
            }

            pairing.PairingRequested += AcceptJustWorksPairing;
            DevicePairingResult? pair = null;
            try
            {
                // ConfirmOnly maps to LE Secure Connections Just Works. Do not
                // request authenticated/MITM protection from a card with no
                // keyboard or display; encrypted GATT access below remains the
                // authoritative security check.
                for (var attempt = 1; attempt <= PairingAttempts; attempt++)
                {
                    pair = await pairing.PairAsync(
                        DevicePairingKinds.ConfirmOnly,
                        DevicePairingProtectionLevel.None).AsTask(token);
                    Console.Error.WriteLine(
                        $"BLE PairAsync attempt {attempt}: {pair.Status}; protection={pair.ProtectionLevelUsed}");
                    if (pair.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                    {
                        pairedInThisAttempt = pair.Status == DevicePairingResultStatus.Paired;
                        break;
                    }
                    if (attempt < PairingAttempts)
                    {
                        LinkStateChanged?.Invoke(this, $"正在自动修复旧配对（{attempt}/{PairingAttempts - 1}）…");
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), token);
                    }
                }
            }
            finally
            {
                pairing.PairingRequested -= AcceptJustWorksPairing;
            }

            if (pair is null || pair.Status is not DevicePairingResultStatus.Paired and not DevicePairingResultStatus.AlreadyPaired)
                throw new LinkException(LinkError.Pairing,
                    $"Windows 配对失败：{pair?.Status.ToString() ?? "NoResult"}。");

            if (pairedInThisAttempt)
            {
                // PairAsync may complete while Windows is replacing its internal
                // Bluetooth objects after the stale peripheral bond was removed.
                // Reopen the device before creating GATT objects; otherwise their
                // RCWs can be disconnected later with RPC_E_DISCONNECTED (0x80010108).
                LinkStateChanged?.Invoke(this, "配对完成，正在刷新 Windows 蓝牙会话…");
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                await OpenDeviceAsync(selected.Address, token);
            }
        }
        _session = await GattSession.FromDeviceIdAsync(_device!.BluetoothDeviceId).AsTask(token)
            ?? throw new LinkException(LinkError.DeviceUnavailable, "无法创建 GATT 会话。");
        _session.MaintainConnection = true;
        _session.MaxPduSizeChanged += OnMaxPduSizeChanged;
        _session.SessionStatusChanged += OnSessionStatusChanged;
        GattDeviceServicesResult? services = null;
        for (var attempt = 1; attempt <= ServiceDiscoveryAttempts; attempt++)
        {
            try
            {
                // A fresh query establishes the physical link and starts SMP. Some
                // Windows Bluetooth stacks throw a transient COMException while the
                // bond is being restored even though encryption and MTU negotiation
                // have already succeeded. Retry in-place so we do not tear that link
                // down; use the cache only after fresh discovery had a fair chance.
                var cacheMode = attempt <= 3 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                services = await _device.GetGattServicesForUuidAsync(ServiceUuid, cacheMode).AsTask(token);
                if (services.Status != GattCommunicationStatus.Unreachable) break;
            }
            catch (COMException error)
            {
                if (attempt == ServiceDiscoveryAttempts)
                    throw;
                Console.Error.WriteLine($"BLE service discovery attempt {attempt} failed transiently: 0x{error.HResult:X8}");
            }

            if (attempt == ServiceDiscoveryAttempts)
                throw new LinkException(LinkError.DeviceUnavailable, "卡片已建立无线连接，但 Windows 未能完成安全握手；正在自动重试。");

            LinkStateChanged?.Invoke(this, $"安全握手进行中（{attempt}/{ServiceDiscoveryAttempts}）…");
            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), token);
        }
        if (services is null) throw new LinkException(LinkError.DeviceUnavailable, "无法查询小猫助手 GATT 服务。");
        if (services.Status == GattCommunicationStatus.AccessDenied) throw new LinkException(LinkError.Encryption, "Windows 拒绝访问加密 GATT 服务；请删除陈旧绑定后重新配对。");
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) throw new LinkException(LinkError.ServiceMissing, $"未发现小猫助手 GATT 服务（{services.Status}）；绑定可能陈旧。");
        _service = services.Services[0];
        var access = await _service.RequestAccessAsync().AsTask(token);
        if (access != DeviceAccessStatus.Allowed) throw new LinkException(LinkError.Encryption, $"Windows 拒绝访问小猫助手 GATT 服务：{access}。");
        _controlRx = await FindAsync(ControlRxUuid, token); _controlTx = await FindAsync(ControlTxUuid, token); _audioTx = await FindAsync(AudioTxUuid, token);
        var maxPduSize = _session.MaxPduSize;
        if (maxPduSize < CatProtocol.RequiredAttMtu) throw new LinkException(LinkError.MtuTooSmall, $"ATT MTU/PDU 仅 {maxPduSize}，协议 v1 要求至少 {CatProtocol.RequiredAttMtu}。请更新蓝牙驱动或重新配对。");
        var refreshedInformation = await DeviceInformation.CreateFromIdAsync(_device.DeviceInformation.Id).AsTask(token);
        if (!refreshedInformation.Pairing.IsPaired)
            throw new LinkException(LinkError.Pairing, "加密通道已建立，但 Windows 未保存设备绑定；请重试连接。");
        CurrentDevice = selected;
        PairedDeviceAddress = selected.Address;
        // The card emits HELLO synchronously as soon as both notification
        // subscriptions become active. Publish transport readiness first so the
        // HELLO handler can immediately write APP_READY without racing this method.
        IsConnected = true;
        _controlTx.ValueChanged += OnControlValue; _audioTx.ValueChanged += OnAudioValue;
        await SubscribeAsync(_controlTx, token); await SubscribeAsync(_audioTx, token);
        LinkStateChanged?.Invoke(this, $"GATT 已就绪（加密配对，PDU {maxPduSize}），正在等待应用握手");
    }

    private async Task OpenDeviceAsync(ulong address, CancellationToken token)
    {
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token)
            ?? throw new LinkException(LinkError.DeviceUnavailable, "找不到卡片；请确认卡片正在广播。");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    private static bool IsRetryableSetupError(Exception error)
    {
        if (error is COMException) return true;
        return error is LinkException link && link.Kind is
            LinkError.Pairing or LinkError.DeviceUnavailable or LinkError.Encryption or
            LinkError.ServiceMissing or LinkError.CharacteristicMissing or LinkError.Subscription;
    }

    private async Task<GattCharacteristic> FindAsync(Guid uuid, CancellationToken token)
    {
        var result = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(token);
        if (result.Status == GattCommunicationStatus.AccessDenied) throw new LinkException(LinkError.Encryption, "Windows 拒绝访问加密 GATT 特征；请删除陈旧绑定后重新配对。");
        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0 ? result.Characteristics[0] : throw new LinkException(LinkError.CharacteristicMissing, $"缺少 GATT 特征 {uuid}（{result.Status}）。");
    }

    private static async Task SubscribeAsync(GattCharacteristic characteristic, CancellationToken token)
    {
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
        if (status != GattCommunicationStatus.Success) throw new LinkException(status == GattCommunicationStatus.AccessDenied ? LinkError.Encryption : LinkError.Subscription, $"订阅 {characteristic.Uuid} 失败：{status}。");
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        if (!IsConnected || _controlRx is null || _session is null) throw new LinkException(LinkError.Disconnected, "卡片尚未连接。");
        await _writeLock.WaitAsync(token);
        try
        {
            var id = _nextMessageId++; if (_nextMessageId == 0) _nextMessageId = 1;
            foreach (var packet in CatProtocol.Fragment(type, id, payload, checked((int)_session.MaxPduSize - 3)))
                await WritePacketAsync(packet, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            MarkConnectionLost("控制通道写入失败，正在重新建立连接。");
            if (error is LinkException) throw;
            throw new LinkException(LinkError.Write, $"Control RX 写入失败：{error.Message}");
        }
        finally { _writeLock.Release(); }
    }

    private async Task WritePacketAsync(byte[] packet, CancellationToken token)
    {
        var lastStatus = GattCommunicationStatus.Unreachable;
        for (var attempt = 1; attempt <= ControlWriteAttempts; attempt++)
        {
            var result = await _controlRx!.WriteValueWithResultAsync(packet.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(token);
            lastStatus = result.Status;
            if (lastStatus == GattCommunicationStatus.Success) return;
            if (lastStatus != GattCommunicationStatus.ProtocolError || attempt == ControlWriteAttempts) break;
            await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), token);
        }
        throw new LinkException(LinkError.Write, $"Control RX 写入失败：{lastStatus}。");
    }

    public async Task ForgetAsync(ulong? fallbackAddress = null)
    {
        var information = _device?.DeviceInformation;
        BluetoothLEDevice? fallbackDevice = null;
        fallbackAddress ??= PairedDeviceAddress;
        if (information is null && fallbackAddress is ulong address)
        {
            fallbackDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            information = fallbackDevice?.DeviceInformation;
        }

        await DisconnectAsync();
        try
        {
            if (information?.Pairing.IsPaired == true)
            {
                var result = await information.Pairing.UnpairAsync();
                if (result.Status is not DeviceUnpairingResultStatus.Unpaired and not DeviceUnpairingResultStatus.AlreadyUnpaired) throw new LinkException(LinkError.Pairing, $"Windows 未能删除配对：{result.Status}。");
            }
            PairedDeviceAddress = null;
        }
        finally { fallbackDevice?.Dispose(); }
    }

    public Task DisconnectAsync()
    {
        IsConnected = false; _reassembler.Reset();
        if (_controlTx is not null) _controlTx.ValueChanged -= OnControlValue;
        if (_audioTx is not null) _audioTx.ValueChanged -= OnAudioValue;
        if (_session is not null) { _session.MaxPduSizeChanged -= OnMaxPduSizeChanged; _session.SessionStatusChanged -= OnSessionStatusChanged; _session.MaintainConnection = false; _session.Dispose(); }
        _service?.Dispose();
        if (_device is not null) { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; _device.Dispose(); }
        _controlRx = null; _controlTx = null; _audioTx = null; _session = null; _service = null; _device = null; CurrentDevice = null;
        return Task.CompletedTask;
    }

    private void OnControlValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try { var message = _reassembler.Accept(ReadBuffer(args.CharacteristicValue)); if (message is not null) ControlMessageReceived?.Invoke(this, message); }
        catch (ProtocolException error) { MarkConnectionLost($"控制协议错误：{error.Message}，正在重新建立连接。"); }
    }
    private void OnAudioValue(GattCharacteristic sender, GattValueChangedEventArgs args) => AudioFrameReceived?.Invoke(this, ReadBuffer(args.CharacteristicValue));
    private void OnMaxPduSizeChanged(GattSession sender, object args)
    {
        LinkStateChanged?.Invoke(this, $"ATT PDU 已变更为 {sender.MaxPduSize}");
        if (IsConnected && sender.MaxPduSize < CatProtocol.RequiredAttMtu)
            MarkConnectionLost($"ATT PDU 降至 {sender.MaxPduSize}，正在重新建立连接。");
    }
    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        if (args.Status != GattSessionStatus.Active) MarkConnectionLost($"GATT 会话已关闭：{args.Error}。");
    }
    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        MarkConnectionLost("卡片已断开；正在自动恢复连接。");
    }
    private void MarkConnectionLost(string detail)
    {
        if (!IsConnected) return;
        IsConnected = false;
        _reassembler.Reset();
        LinkStateChanged?.Invoke(this, detail);
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }
    private static byte[] ReadBuffer(IBuffer buffer) { var result = new byte[buffer.Length]; using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(result); return result; }
    public async ValueTask DisposeAsync() { StopScan(); await DisconnectAsync(); _writeLock.Dispose(); }
}
