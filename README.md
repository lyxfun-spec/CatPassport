# 小猫助手 / Cat Assistant

面向 FoloToy AI Passport 的个人 AI 对话卡片：

- `firmware/`：ESP32-C3 固件，负责录音、BLE 传输、转写确认和回复显示。
- `android/`：早期安卓客户端，当前停止开发，仅保留历史代码。
- `windows/`：当前使用的 Electron + React 桌面客户端与独立 .NET 后台；后台负责 BLE、音频、AI、SQLite 和 DPAPI，并在托盘常驻。
- `docs/`：产品状态机、BLE 协议和验收说明。

设备本身不连接互联网。阿里云 API Key 仅由用户在 Windows 客户端中输入并使用 DPAPI 保存在本机，不会写入固件或通过 BLE 发送。

## 开发约束

- 固件基于官方 `main` 基线：ESP-IDF 5.5.3、ESP32-C3、8 MB Flash、无 PSRAM。
- 原官方目录保持只读；本目录是独立 Git 工作区。
- 不保存 API Key、设备私密数据或未脱敏日志。
- 未经明确授权，不刷机、不发布，也不提交功能开发改动。
