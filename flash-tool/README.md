# 小猫助手 AI Passport 一键刷机工具

这是一个供 Windows 10/11 使用的离线刷机包。它不依赖 ESP-IDF、Python、Git 或网络，不会在现场重新编译固件。

## 正式使用

1. 启动 AI Passport，使用支持数据传输的 USB Type-C 线连接电脑。
2. 双击 `一键刷入小猫助手.cmd`。
3. 根据设备名称选择 AI Passport 对应的 COM 口。
4. 确认显示的端口、固件版本和 SHA-256，输入大写的 `FLASH`。
5. 等待出现“固件写入和写后校验成功”。不要在写入期间拔线。
6. 检查卡片屏幕、按键和 Windows 客户端连接。

工具会把日志保存到 `logs/`。日志可能包含设备 MAC 等识别信息，对外发送前请先检查和脱敏。

## 事前彩排

在 PowerShell 中运行下面两条命令。它们都不会写入设备：

```powershell
.\Flash-CatPassport.ps1 -VerifyOnly
.\Flash-CatPassport.ps1 -ListOnly
```

- `-VerifyOnly` 检查烧录工具、固件包和所有 SHA-256。
- `-ListOnly` 在完成离线校验后仅列出串口设备。

可选的开发/维护参数：

```powershell
.\Flash-CatPassport.ps1 -Port COM7
.\Flash-CatPassport.ps1 -Port COM7 -Baud 115200
.\Flash-CatPassport.ps1 -Port COM7 -Yes -MonitorSeconds 0
```

`-Yes` 仅在同时显式指定 `-Port` 时有效，用于受控的自动化验证。现场人工刷机建议保留交互确认。

## 安全边界

- 仅接受通过完整清单和 SHA-256 校验的 ESP32-C3 / 8 MB 归档。
- 仅将已校验的合并 `full.bin` 从 `0x0` 写入。
- 写入前识别 ESP32-C3 和 8 MB Flash。
- 不会默认选择第一个 COM 口。
- 不会执行整片擦除，也不会无限重试。
- 合并镜像会替换原厂固件，并可能重置 NVS/PHY 数据；这是首次完整部署的预期行为。
- 烧录成功不代表屏幕、按键、音频和 BLE 都已通过真机验收。

## 常见问题

- **没有 COM 口**：检查卡片是否开机、USB 线是否支持数据、接口是否接触良好。
- **端口被占用**：关闭其他串口终端、ESP-IDF monitor 或可能占用该端口的程序。
- **无法连接芯片**：先重新插拔数据线；若仍失败，按硬件说明进入下载模式后再试。
- **高速写入不稳定**：改用 `.\Flash-CatPassport.ps1 -Port COMx -Baud 115200`。
- **写入中断**：不要整片擦除。修正数据线、供电或端口问题后，重新运行同一刷机流程。ESP32-C3 的 ROM 下载程序不会被这次 Flash 写入覆盖。

## 刷机后验收清单

1. 屏幕正常点亮并进入小猫助手首页。
2. 三个实体按键均可用。
3. Windows 客户端可以发现并连接卡片。
4. 完成一次录音、语音识别确认、AI 回复和卡片显示。
5. 重启卡片后仍能正常工作。
6. 日志中没有重启循环、assert 或 watchdog。

