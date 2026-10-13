# 小猫助手 Windows 客户端

Windows 11 桌面常驻网关。界面使用 Electron + React + TypeScript；BLE、设备协议、音频、DashScope、SQLite 与 DPAPI 密钥存储仍全部运行在 C#/.NET 后台中。

## 架构

```text
React renderer（纯 UI）
    ↕ Electron contextBridge / IPC
Electron main（窗口、托盘、后台进程生命周期）
    ↕ stdin/stdout：版本化 NDJSON RPC
CatAssistant.Backend（BLE、音频、AI、SQLite、DPAPI）
    ↕
CatAssistant.Core（设备协议、分片、ADPCM、会话策略）
```

- `CatAssistant.Desktop/`：Electron 主进程、受限 preload、React UI、动画、托盘和打包配置。
- `CatAssistant.Backend/`：独立 .NET 8 后台；不引用 WinUI 或 Windows App SDK。
- `CatAssistant.Core/`：纯 .NET 协议、音频和会话逻辑。
- `CatAssistant.Backend.Tests/`、`CatAssistant.Core.Tests/`：IPC 安全边界和底层协议测试。

后台协议版本为 1。请求格式为 `{"version":1,"id":1,"method":"state.get","params":{...}}`；响应使用 `kind=response`，状态和流式对话使用 `kind=event`。当前方法按领域分为 `state.*`、`devices.*`、`settings.*`、`conversation.*` 和 `app.*`。`conversation.stream` 事件包含 `started`、`delta`、`completed` 阶段，电脑端直接对话与卡片对话共用同一个 C# 会话/流式实现，Electron 不直接调用 AI 服务。

## 环境与开发

- Windows 11（支持 Bluetooth LE）
- .NET 8 SDK
- Node.js 22+
- pnpm 11+

不需要 Visual Studio、WinUI 3 工作负载或 Windows App SDK。首次安装依赖：

```powershell
pnpm install
```

从仓库根目录用一条命令启动 Electron、React 热更新和 .NET 后台：

```powershell
pnpm dev
```

可选的环境检查：

```powershell
. .\windows\Enter-CatAssistantEnvironment.ps1
```

## 数据与安全兼容

迁移没有改变用户数据位置或格式：

- SQLite：`%LOCALAPPDATA%\FoloToy\CatAssistant\cat-assistant.db`
- DashScope Key：`%LOCALAPPDATA%\FoloToy\CatAssistant\dashscope.key`
- Key 继续使用 DPAPI `CurrentUser` 加密；不会发送给 Electron renderer、写入 SQLite、日志或 BLE。
- 原有 `settings`、`conversations`、`messages` 表和键名保持不变，无需数据迁移。
- 原 BLE Service/Characteristic UUID、加密配对、通知订阅、分片、音频序号校验和 PDU ≥177 行为保持不变。

首次运行时在“设置”中填写 DashScope API Key。连接页会自动扫描附近的 `FoloToy Cat`，点击设备卡片即立即连接；客户端同一时间只连接一张卡片，更换设备前需使用右上角“取消配对”。关闭窗口只隐藏到托盘，Electron 与 .NET 后台继续运行并服务卡片；双击托盘或选择“打开小猫助手”恢复窗口。只有托盘“退出”才会请求后台优雅关闭并完全退出。

## 检查与打包

```powershell
pnpm typecheck
pnpm test
pnpm package   # 生成 unpacked Windows x64 应用
pnpm make      # 生成 Squirrel 安装包和 ZIP
```

打包前会执行 `dotnet publish -c Release -r win-x64 --self-contained true`，并把输出放入 Electron 的 `resources/backend/`。因此正式应用携带 .NET 运行时及后台所需 DLL，目标电脑无需单独安装 .NET。当前打包目标为 Windows x64；发布前仍应配置正式图标和 Windows 代码签名证书。

## 真机检查

1. 首次配对后确认链路已加密，Control TX 与 Audio TX 均成功订阅。
2. 确认界面显示的 PDU 至少为 177，并完成 HELLO / `app=windows` 的 APP_READY。
3. 连续录音 30 秒，验证无丢帧、WAV/ASR 正常且请求结束后不保留原始音频。
4. 覆盖窗口关闭后卡片继续服务、托盘恢复、取消、新会话、卡片重启、蓝牙关闭再打开、系统睡眠唤醒和忘记后重新配对。
5. 覆盖电脑端直接对话的 `started/delta/completed` 流，以及卡片与电脑共用本地会话历史。
6. 断网、错误 API Key、无模型权限时确认错误类别清晰且卡片只显示通用提示。
