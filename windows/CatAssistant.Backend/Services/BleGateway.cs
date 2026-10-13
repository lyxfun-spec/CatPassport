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

public enum LinkError { BluetoothUnavailable, DeviceUnavailable, Pairing, Encryption, ServiceMissing, CharacteristicMissing, Subscription, MtuTooSmall, Disconnected, Write }
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

    public bool IsConnected { get; private set; }
    public uint MaxPduSize => _session?.MaxPduSize ?? 0;
    public CatDevice? CurrentDevice { get; private set; }
    public ulong? PairedDeviceAddress { get; private set; }
    public event EventHandler<CatDevice>? DeviceFound;
    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<byte[]>? AudioFrameReceived;
    public event EventHandler<string>? LinkStateChanged;

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
        LinkStateChanged?.Invoke(this, "正在连接并检查配对…");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address).AsTask(token)
            ?? throw new LinkException(LinkError.DeviceUnavailable, "找不到卡片；请确认卡片正在广播。");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        if (!_device.DeviceInformation.Pairing.IsPaired)
        {
            var pairing = _device.DeviceInformation.Pairing.Custom;
            void AcceptJustWorksPairing(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly) args.Accept();
            }

            pairing.PairingRequested += AcceptJustWorksPairing;
            DevicePairingResult pair;
            try
            {
                pair = await pairing.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption).AsTask(token);
            }
            finally
            {
                pairing.PairingRequested -= AcceptJustWorksPairing;
            }
            if (pair.Status is not DevicePairingResultStatus.Paired and not DevicePairingResultStatus.AlreadyPaired)
                throw new LinkException(LinkError.Pairing, $"配对失败：{pair.Status}。可尝试“忘记设备并重新配对”。");
        }
        PairedDeviceAddress = selected.Address;
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(token)
            ?? throw new LinkException(LinkError.DeviceUnavailable, "无法创建 GATT 会话。");
        _session.MaintainConnection = true; _session.MaxPduSizeChanged += OnMaxPduSizeChanged;
        var services = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Cached).AsTask(token);
        if (services.Status == GattCommunicationStatus.AccessDenied) throw new LinkException(LinkError.Encryption, "Windows 拒绝访问加密 GATT 服务；请删除陈旧绑定后重新配对。");
        if (services.Status == GattCommunicationStatus.Unreachable) throw new LinkException(LinkError.DeviceUnavailable, "卡片当前不可达；请确认卡片开机且蓝牙已开启。");
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) throw new LinkException(LinkError.ServiceMissing, $"未发现小猫助手 GATT 服务（{services.Status}）；绑定可能陈旧。");
        _service = services.Services[0];
        var access = await _service.RequestAccessAsync().AsTask(token);
        if (access != DeviceAccessStatus.Allowed) throw new LinkException(LinkError.Encryption, $"Windows 拒绝访问小猫助手 GATT 服务：{access}。");
        _controlRx = await FindAsync(ControlRxUuid, token); _controlTx = await FindAsync(ControlTxUuid, token); _audioTx = await FindAsync(AudioTxUuid, token);
        CurrentDevice = selected; IsConnected = true;
        _controlTx.ValueChanged += OnControlValue; _audioTx.ValueChanged += OnAudioValue;
        await SubscribeAsync(_controlTx, token); await SubscribeAsync(_audioTx, token);
        if (_session.MaxPduSize < CatProtocol.RequiredAttMtu) throw new LinkException(LinkError.MtuTooSmall, $"ATT MTU/PDU 仅 {_session.MaxPduSize}，协议 v1 要求至少 {CatProtocol.RequiredAttMtu}。请更新蓝牙驱动或重新配对。");
        LinkStateChanged?.Invoke(this, $"已连接（加密配对，PDU {_session.MaxPduSize}）");
    }

    private async Task<GattCharacteristic> FindAsync(Guid uuid, CancellationToken token)
    {
        var result = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Cached).AsTask(token);
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
            {
                var result = await _controlRx.WriteValueWithResultAsync(packet.AsBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(token);
                if (result.Status != GattCommunicationStatus.Success) throw new LinkException(LinkError.Write, $"Control RX 写入失败：{result.Status}。");
            }
        }
        finally { _writeLock.Release(); }
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
        if (_session is not null) { _session.MaxPduSizeChanged -= OnMaxPduSizeChanged; _session.MaintainConnection = false; _session.Dispose(); }
        _service?.Dispose();
        if (_device is not null) { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; _device.Dispose(); }
        _controlRx = null; _controlTx = null; _audioTx = null; _session = null; _service = null; _device = null; CurrentDevice = null;
        return Task.CompletedTask;
    }

    private void OnControlValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try { var message = _reassembler.Accept(ReadBuffer(args.CharacteristicValue)); if (message is not null) ControlMessageReceived?.Invoke(this, message); }
        catch (ProtocolException error) { LinkStateChanged?.Invoke(this, $"协议错误：{error.Message}"); }
    }
    private void OnAudioValue(GattCharacteristic sender, GattValueChangedEventArgs args) => AudioFrameReceived?.Invoke(this, ReadBuffer(args.CharacteristicValue));
    private void OnMaxPduSizeChanged(GattSession sender, object args) => LinkStateChanged?.Invoke(this, $"ATT PDU 已变更为 {sender.MaxPduSize}");
    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        IsConnected = false; _reassembler.Reset(); LinkStateChanged?.Invoke(this, "卡片已断开；将按设置尝试重连。");
    }
    private static byte[] ReadBuffer(IBuffer buffer) { var result = new byte[buffer.Length]; using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(result); return result; }
    public async ValueTask DisposeAsync() { StopScan(); await DisconnectAsync(); _writeLock.Dispose(); }
}
