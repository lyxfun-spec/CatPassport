# 小猫助手 Windows 客户端

小猫助手是一个以桌面 AI 对话为主体、可选连接 AI Passport 卡片的 Windows 客户端。卡片是 Chat 模式的伴随硬件，不是应用的主界面；即使没有卡片，桌面 Chat 仍可独立使用。

完整产品边界、数据模型、打断语义、卡片会话绑定和 Work 扩展契约见：

- [桌面 AI Agent 客户端重构设计（中文）](../docs/desktop-agent-architecture.zh_CN.md)
- [Desktop AI Agent Client Architecture (English)](../docs/desktop-agent-architecture.md)

## 当前版本范围

- 完整的桌面 Chat 模式：会话创建、切换、重命名、删除、历史持久化和流式回复。
- 同一会话收到新消息时立即打断旧生成；已产生的部分回复保存为 `interrupted`，并参与后续上下文。
- DashScope/Qwen 文本对话，可在设置中开启或关闭阿里云联网搜索。
- AI 回复支持 Markdown 展示、复制和搜索来源链接。
- Chat / Work 使用统一会话列表和界面；Work 入口已保留但尚未启用，未来接入独立 Agent 后端与沙箱。
- AI Passport 卡片只接入 Chat：默认跟随电脑当前 Chat，也可在卡片上创建独立对话，再由电脑继续。
- API Key 使用 Windows DPAPI 保存，不进入 renderer、SQLite、日志或 BLE 数据。

V1 不包含图片/文件消息、消息编辑、重新生成、Work Agent、本地工具或旧演示会话导入。

## 架构

```text
React renderer（统一会话 UI）
    ↕ Electron contextBridge / IPC
Electron main（窗口、托盘、外链与后台生命周期）
    ↕ stdin/stdout：版本化 NDJSON RPC
CatAssistant.Backend
    ├─ ConversationService（会话、运行、打断、上下文）
    ├─ DashScopeClient（Qwen、联网搜索、ASR）
    ├─ SqliteRepository（V2 数据与迁移）
    └─ AssistantCoordinator（卡片链路和会话绑定）
        ↕
CatAssistant.Core（设备协议、分片与音频）
```

- `CatAssistant.Desktop/`：Electron 主进程、受限 preload、React UI、托盘和打包配置。
- `CatAssistant.Backend/`：独立 .NET 8 后台；renderer 不直接访问云服务、数据库或 BLE。
- `CatAssistant.Core/`：纯 .NET 协议、音频和会话底层逻辑。
- `CatAssistant.Backend.Tests/`、`CatAssistant.Core.Tests/`：会话竞态、IPC 边界和协议测试。

后台协议版本仍为 1。新版主要 RPC 领域为 `app.*`、`conversations.*`、`devices.*` 和 `settings.*`；事件分为 `device.changed` 与 `conversation.changed`。旧演示 RPC 暂时保留作兼容入口，但新版 UI 不再依赖它们。

## 会话与打断规则

每个会话最多有一个活动生成，不同会话可以并行：

1. 用户消息先持久化，再创建 run 和 assistant 占位消息。
2. 若同一会话仍在生成，先取消旧 run。
3. 旧回复尚无文本时删除空占位；已有文本时保存为 `interrupted`。
4. 新用户消息随后进入历史，并基于包含该部分回复的最新上下文开始新生成。
5. 旧 run 即使迟到返回 token，也会因 run 身份校验而被丢弃。

这保证连续发送消息不需要等待队列，也不会让被打断的内容悄悄从上下文消失。

## 数据与迁移

- SQLite：`%LOCALAPPDATA%\FoloToy\CatAssistant\cat-assistant.db`
- DashScope Key：`%LOCALAPPDATA%\FoloToy\CatAssistant\dashscope.key`
- 新数据写入 `conversations_v2`、`messages_v2`、`runs`、`citations`、`app_state` 和 `work_requests`。
- 原 `conversations`、`messages` 和设置表不会被删除，也不会自动混入新版会话列表。
- 异常退出后启动恢复会把悬空 run 和有内容的生成消息标为 `interrupted`，移除空占位。
- 原 BLE UUID、加密配对、通知订阅、分片、音频序号和 PDU 要求保持不变。

## 环境与开发

- Windows 11（卡片功能需要 Bluetooth LE）
- .NET 8 SDK
- Node.js 22+
- pnpm 11+

```powershell
pnpm install
pnpm dev
```

没有卡片时也可以开发和测试桌面 Chat；后台会继续尝试发现已绑定设备，但不会阻塞 UI。

## 检查与打包

```powershell
pnpm typecheck
pnpm test
dotnet test .\windows\CatAssistant.sln -c Debug
pnpm package   # unpacked Windows x64 应用
pnpm make      # Squirrel 安装包和 ZIP
```

打包前会执行 `dotnet publish -c Release -r win-x64 --self-contained true`，并把后台放入 Electron `resources/backend/`。正式应用因此无需目标电脑另行安装 .NET。当前仍需在发布前配置正式图标、签名证书和安装升级策略。

## 验收重点

1. 连续发送两条消息，确认第一轮生成被正确截断，新回复只由最新 run 更新。
2. 切换会话时确认不同会话可并行生成，历史和流式内容不串线。
3. 开关联网搜索，确认来源链接可打开且非 HTTP(S) URL 不进入 UI。
4. 卡片跟随电脑、卡片新建独立 Chat、电脑切回该 Chat 三条路径均保持相同历史。
5. 覆盖断网、错误 Key、模型无权限、应用异常退出和重启恢复。
6. 真机覆盖配对、断链重连、录音/ASR、卡片重启、蓝牙切换和系统睡眠唤醒。
