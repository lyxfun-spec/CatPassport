# 小猫助手 Windows 客户端

Windows 11 桌面常驻网关。卡片仍是 BLE Peripheral；本程序负责加密连接、音频解码、DashScope ASR/流式聊天和本地会话。

## 环境与运行

- Windows 11（支持 Bluetooth LE）
- Visual Studio 2022 17.10+，安装“.NET 桌面开发”和“Windows 应用 SDK C#”工作负载
- .NET 8 SDK、Windows 10/11 SDK 10.0.19041+

```powershell
dotnet restore windows/CatAssistant.sln
dotnet build windows/CatAssistant.sln -c Debug
dotnet test windows/CatAssistant.Core.Tests/CatAssistant.Core.Tests.csproj
dotnet run --project windows/CatAssistant.App/CatAssistant.App.csproj
```

首次运行时在“设置”中填写 DashScope API Key，然后扫描并选择 `FoloToy Cat`。应用显示 Windows 协商出的 `GattSession.MaxPduSize`；低于 177 时会中止握手并给出明确诊断，不会尝试修改固件协议。

关闭窗口会隐藏到系统托盘。托盘菜单可重新打开或彻底退出。应用会记住最后设备并自动重连；陈旧绑定可使用“忘记设备并重新配对”。API Key 通过 DPAPI CurrentUser 保存。SQLite 数据库仅保存设置和产品要求的会话历史，不写调试日志、原始录音或密钥。

## 真机检查

1. 首次配对后确认链路已加密，Control TX 与 Audio TX 均成功订阅。
2. 确认界面显示的 PDU 至少为 177，并完成 HELLO / `app=windows` 的 APP_READY。
3. 连续录音 30 秒，验证无丢帧、WAV/ASR 正常且临时音频请求完成后释放。
4. 覆盖取消、新会话、卡片重启、蓝牙关闭再打开、系统睡眠唤醒和忘记后重新配对。
5. 断网、错误 API Key、无模型权限时确认错误类别清晰且卡片只显示通用提示。
