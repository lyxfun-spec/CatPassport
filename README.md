# 小猫助手 · CatPassport

把一张 FoloToy AI Passport 卡片变成 Windows 桌面 AI Agent 的随身入口：桌面端负责对话、联网搜索和自主任务（Work），卡片通过 BLE 负责录音、转写确认和回复显示。没有卡片时，桌面端也是一个完整的 AI 对话与 Work 客户端。

> **非官方声明**：CatPassport 是独立社区项目，与 FoloToy 官方无关联、未获其背书。固件基于官方开源 [FoloToy/ai-passport](https://github.com/FoloToy/ai-passport)（MIT）二次开发，硬件为 ESP32-C3（8 MB Flash、无 PSRAM）。

## 功能

**桌面 Chat**

- 流式回复、Markdown 渲染、会话历史持久化；新消息立即打断旧生成，部分回复保留为 `interrupted` 并参与后续上下文。
- 应用自有的 `web_search` 工具编排联网搜索，回复附可跳转的来源引用；不使用模型厂商的隐式内置搜索。
- 简体中文 / English 双语界面，默认跟随系统区域语言。
- 模型走可配置的 OpenAI Chat Completions 兼容地址，默认 DeepSeek。

**Work 模式（自研 Agent 框架）**

- 文件中心的自主 Agent：原生 function tool calling、同批独立工具并发、中途消息、停止与崩溃恢复。
- 每个 Work 对话运行在独立 Windows AppContainer 沙箱 + Job Object 中；外部文件操作和命令需用户审批，由可信后端代理执行。
- 内置 documents / presentations / spreadsheets / pdf 技能，按需渐进加载；打包只读 Python 办公运行时。
- 长输出截断自动恢复；成果以胶囊形式交付到对话工作区 `outputs/`。
- Chat 中可发起 Work 请求，经批准卡片确认后后台执行，双向跳转。

**AI Passport 卡片**

- 卡片不连接互联网：录音 → BLE 传输 → 桌面转写确认 → 回复分页显示。
- 默认跟随电脑当前 Chat，也可在卡片上新建独立对话再回到电脑继续。
- API Key 只在桌面端输入，用 Windows DPAPI 保存在本机，不写入固件、不经 BLE 发送、不进日志。

## 架构总览

```text
React renderer（统一会话 UI）
    ↕ Electron contextBridge / IPC
Electron main（窗口、托盘、后台生命周期）
    ↕ stdin/stdout NDJSON RPC
CatAssistant.Backend（.NET 8 独立后台）
    ├─ ConversationService / WorkAgentService（Chat 与 Work 运行时）
    ├─ IChatProvider / IWorkModelProvider（OpenAI 兼容模型边界）
    ├─ IWebSearchProvider（搜索边界）
    ├─ SqliteRepository（唯一状态存储）
    └─ AssistantCoordinator（BLE 卡片链路）
        ↕ BLE
AI Passport 卡片固件（ESP-IDF / ESP32-C3）
```

renderer 不直接访问云服务、数据库或 BLE。完整设计见[桌面 AI Agent 客户端架构](docs/desktop-agent-architecture.zh_CN.md)。

## 仓库结构

| 目录 | 内容 |
| --- | --- |
| `firmware/` | ESP32-C3 卡片固件（ESP-IDF 5.5.3），含开发文档与 CI |
| `windows/` | Electron + React 桌面端、.NET 8 后台与测试 |
| `flash-tool/` | 离线一键刷机包（无需 ESP-IDF / Python / 网络） |
| `installer/` | WPF 安装向导 + WiX MSI 构建脚本 |
| `docs/` | 架构、BLE 协议、上下文管理、Work 实施记录 |
| `assets/` | 品牌图标与生成脚本 |

## 快速开始

### 桌面端开发

环境：Windows 11（卡片功能需要 Bluetooth LE）、.NET 8 SDK、Node.js 22+、pnpm 11+。

```powershell
pnpm install
pnpm dev
```

首次启动后在“设置 → AI 服务”中配置 OpenAI 兼容地址与 API Key（默认 DeepSeek），并为卡片 ASR 配置 DashScope Key。没有卡片也可以正常开发和测试桌面 Chat。

```powershell
pnpm typecheck
pnpm test
dotnet test .\windows\CatAssistant.sln -c Debug
pnpm package   # unpacked Windows x64 应用
pnpm make      # 安装包和 ZIP
```

更多细节见 [windows/README.md](windows/README.md)。

### 固件构建

环境：ESP-IDF **v5.5.3**、ESP32-C3、8 MB Flash、无 PSRAM。

```bash
source <ESP-IDF-v5.5.3-路径>/export.sh
idf.py set-target esp32c3
idf.py build
idf.py flash monitor
```

完整的构建、验证与分区说明见[固件构建与测试](firmware/docs/development/engineering/build-and-test.zh_CN.md)和[环境准备](firmware/docs/development/engineering/environment-setup.zh_CN.md)。

### 一键刷机（无需开发环境）

`flash-tool/` 是离线刷机包：连接 USB，双击 `一键刷入小猫助手.cmd`，按提示选择 COM 口并确认 SHA-256 即可。详见 [flash-tool/README.md](flash-tool/README.md)。

> 合并镜像从 0x0 写入会重置 NVS；需要保留设备已有状态时用分段 `idf.py flash`。

### 安装程序

`installer/` 提供 WPF 向导包装的 WiX MSI，构建与测试脚本见 [installer/README.md](installer/README.md)。

## 安全与隐私

- 设备不连接互联网；所有云端调用只发生在桌面后台。
- API Key 用 DPAPI 保存在本机，不进入 renderer、SQLite、日志或 BLE 数据。
- Work 命令运行在 AppContainer 沙箱中，文件边界由 ACL 控制；外部操作逐项审批，run 结束即撤销授权。沙箱提供文件隔离，不宣称具备数据防泄漏能力。
- 刷机日志可能包含设备 MAC 等识别信息，对外分享前请先脱敏。

## 文档

- [桌面 AI Agent 客户端架构](docs/desktop-agent-architecture.zh_CN.md) · [English](docs/desktop-agent-architecture.md)
- [BLE 协议](docs/ble-protocol.zh_CN.md)
- [上下文管理](docs/context-management.zh_CN.md) · [English](docs/context-management.md)
- [Work 模式实施记录](docs/work-mode-implementation.zh_CN.md)
- [第三方声明](docs/third-party-notices.md)

## 许可证

本项目采用 [MIT License](LICENSE)。固件基线来自 FoloToy（MIT），字体、esptool 等第三方组件的许可见 [NOTICE](NOTICE) 与 [docs/third-party-notices.md](docs/third-party-notices.md)。

---

## English summary

CatPassport turns a FoloToy AI Passport card (ESP32-C3) into a companion device for a Windows desktop AI agent. The desktop app (Electron + React + a standalone .NET 8 backend) provides streaming chat with app-owned web search and citations, plus **Work mode**: a self-built agent runtime with native tool calling, per-conversation AppContainer sandboxing, user approvals, parallel tools, built-in office skills and artifact delivery. The card itself never touches the internet — it records audio, streams it over BLE, confirms transcription and pages through replies. API keys stay on the PC, protected by Windows DPAPI.

This is an independent community project, not affiliated with or endorsed by FoloToy. The firmware is derived from the MIT-licensed [FoloToy/ai-passport](https://github.com/FoloToy/ai-passport). See per-directory READMEs for build instructions; the project is MIT licensed.