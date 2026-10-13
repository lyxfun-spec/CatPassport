# 小猫助手 / Cat Assistant

面向 FoloToy AI Passport 的个人 AI 对话卡片：

- `firmware/`：ESP32-C3 固件，负责录音、BLE 传输、转写确认和回复显示。
- `android/`：安卓 App，负责 BLE 网关、阿里云 ASR/LLM 调用和会话保存。
- `windows/`：Electron + React 桌面界面与独立 .NET 后台；后台复用 BLE、音频、AI、SQLite 和 DPAPI 能力，并在托盘常驻。
- `docs/`：产品状态机、BLE 协议和验收说明。

设备本身不连接互联网。阿里云 API Key 仅由用户在 App 中输入并保存在手机本地，不会写入固件或通过 BLE 发送。

## 开发约束

- 固件基于官方 `main` 基线：ESP-IDF 5.5.3、ESP32-C3、8 MB Flash、无 PSRAM。
- 原官方目录保持只读；本目录是独立 Git 工作区。
- 不保存 API Key、设备私密数据或未脱敏日志。
- 未经明确授权，不刷机、不发布，也不提交功能开发改动。
