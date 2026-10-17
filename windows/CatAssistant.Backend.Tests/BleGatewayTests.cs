using CatAssistant.Backend.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class BleGatewayTests
{
    [Fact]
    public void OffRadioProducesActionableBluetoothDisabledError()
    {
        var error = BleGateway.RadioStateError(RadioState.Off);

        Assert.NotNull(error);
        Assert.Equal(LinkError.BluetoothDisabled, error.Kind);
        Assert.Equal("Windows 蓝牙已关闭；请先在系统设置中打开蓝牙，然后重新扫描。", error.Message);
    }

    [Fact]
    public void OnRadioAllowsBluetoothOperations()
    {
        Assert.Null(BleGateway.RadioStateError(RadioState.On));
    }

    [Fact]
    public void WatcherRadioFailureUsesBluetoothDisabledPrompt()
    {
        Assert.Equal(
            "Windows 蓝牙已关闭；请先在系统设置中打开蓝牙，然后重新扫描。",
            BleGateway.ScanStoppedMessage(BluetoothError.RadioNotAvailable));
    }
}
