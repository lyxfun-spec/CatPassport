# Work 模式代码分析、实施设计与验收记录

本文是《Work 模式需求文档》的工程实施记录。产品范围和行为以正式需求文档为准；本文记录代码复用、技术选型、文件落点、安全边界和验收证据，不新增产品需求。

## 1. 现有架构分析与复用

- `ConversationService`、`SqliteRepository` 和现有会话模型继续负责 Chat/Work 对话、消息、激活状态和持久化。Chat 的流式生成、搜索引用和设备绑定路径保持原样，Work 通过独立 `WorkAgentService` 接入。
- `DashScopeClient` 继续作为模型 API 客户端。Chat 仍走原有流式接口；Work 新增兼容 Chat Completions 的原生 function tool calling，保存 `tool_call_id` 并回传对应 tool result。
- Electron 主进程、受限 preload RPC、React 会话界面和托盘生命周期全部复用。Work 只新增模式入口、附件、停止、进度、审批和成果胶囊。
- SQLite 继续作为唯一状态存储；新增 Work context、tool call、approval 和 artifact 表，没有替换或迁移 Chat 表。
- 联网搜索拆成独立能力：Chat 继续使用模型自带的 `enable_search`；Work 的 `web_search` 工具改为调用阿里云 IQS 独立搜索 API（`POST /search/unified`），模型只负责决策与综合，不另建浏览器自动化或抓取平台。

## 2. 已确定需求与工程决策

### 产品文档已确定

Work 是文件中心的自主 Agent；支持原生工具调用、同批独立工具并发、中途消息、三目录工作区、外部操作审批、停止、联网、内置 Skill、办公运行时、多对话后台执行和成果胶囊。第一版不包含 GUI 自动化、多 Agent、强制计划器、用户 Skill 管理、自动上下文摘要或系统审批通知。

### 实施阶段确定的技术方案

- 隔离：每个 Work 对话使用独立 Windows AppContainer profile/private storage。进程以 AppContainer security capabilities 启动，并放入 `KILL_ON_JOB_CLOSE` Job Object；创建进程时使用显式 handle allowlist。
- 网络：沙箱命令默认带 `internetClient` capability；这只提供文件边界，不宣称具备数据防泄漏能力。
- 外部授权：不动态放宽 AppContainer ACL。可信后端在用户批准后代理执行明确文件操作或明确命令；文件目录 grant 与外部命令权限分离。task-directory grant 仅存在于当前 run 的可信内存状态，结束即撤销。
- 链接防绕过：工作区 ACL 不授予普通外部目录；外部文件代理拒绝目标现有路径链上的 reparse point，交付与定位也只接受 `outputs` 下的普通非 reparse 文件。
- 运行时：打包 Python 3.13 x64 与固定版本办公依赖，发行资源只读共享；额外依赖安装到当前对话 `work/python-packages`。
- 模型：Chat 与 Work 使用独立配置；Work 默认使用 `qwen3.8-max`，不会因调整普通聊天模型而降级。
- 自主规划：不增加强制计划器或面向 Python/PPT 等场景的专用工具。系统提示只要求长任务在开始及重要节点简短报告当前进度和下一步；简单任务直接执行，不要求先输出计划。
- 环境适配：每轮 Work 开始前只向模型说明 Windows 工作区绝对路径、`inputs/work/outputs` 的用途和 Python 是否可用，不在系统提示中展开办公依赖或目录权限。命令执行环境仍预置 `PATH`、`PYTHONPATH`、`PIP_TARGET` 等通用约定，使 PowerShell 和 CMD 中的标准 `python`/`pip` 命令自然落入当前对话工作区。PowerShell 会为当前工作目录建立会话内文件系统驱动器，以确保 AppContainer 中的直接原生调用和 `Start-Process` 都获得有效的 PowerShell 工作位置；模型可按任务需要自主选择 PowerShell 或 CMD。
- Skill：内置 `documents`、`presentations`、`spreadsheets`、`pdf`。模型先 `list_skills` 获取摘要，再按需 `load_skill` 获取完整 `SKILL.md`。
- 上下文：第一版达到字符预算即显式失败并告知用户，没有静默截断或自动摘要。
- Chat 转 Work：Chat 原生请求只暴露 `request_work`，先持久化为批准卡片；桌面批准后由可信后端创建并后台启动 Work。批准状态与目标对话在同一 SQLite 事务中关联，重启后自动续跑尚未启动的已批准请求。目标对话保存 `origin_conversation_id`，请求保存目标对话 ID，实现双向跳转；完整 Chat 历史不复制到 Work。工具调用参数跨 SSE 分片累积后才提议，参数非法或字段缺失只落一条 `failed` 请求并在卡片上说明，不打断当轮回答；派发 run 前先写入 `started`，因此崩溃重启不会重放已批准的 prompt；同一 tool call 的重新提议复用原记录，被拒绝的请求可以再次提议。

这些是实现选择，不反向改变正式需求。并发上限仍受模型单轮工具批次和机器资源约束；未引入额外调度平台。

## 3. 模块和主要文件

| 模块 | 主要文件 | 职责 |
| --- | --- | --- |
| Agent loop | `Services/WorkRuntime/WorkAgentService.cs` | 模型轮次、批次并发、消息排队、停止与恢复 |
| 模型协议 | `Services/DashScopeClient.cs` | 原生 tool calls、tool results、并行声明 |
| 持久化 | `Services/SqliteRepository.cs`, `Models/WorkModels.cs` | Work context、工具、审批、成果 |
| 工作区 | `Services/WorkWorkspaceService.cs` | `inputs/work/outputs`、输入复制 |
| OS 隔离 | `Services/Sandbox/WindowsSandboxController.cs` | AppContainer、Job Object、能力和句柄边界 |
| 权限代理 | `Services/WorkRuntime/WorkApprovalService.cs` | 持续等待、批准/拒绝、run 范围 grant |
| 工具 | `Services/WorkTools/*.cs` | 命令、文件、搜索、Skill、交付 |
| 运行时打包 | `Desktop/scripts/Prepare-WorkRuntime.ps1`, `forge.config.ts` | 下载校验、固定依赖、发行资源 |
| UI/RPC | `Program.cs`, `Desktop/src/{main,preload,renderer,shared}.ts*` | 最小 RPC、审批、进度、附件、停止、成果定位 |

## 4. 分阶段实施记录

1. 领域模型与原生 tool calling：已完成；保持 Chat 路径不变。
2. Windows 安全阻塞项：已完成 AppContainer/Job Object 原型、真实实现和隔离探针后，才接入命令工具。
3. 基础工具和审批：已完成文件/命令、持续审批、单次与任务目录 grant、停止联动。
4. Skill、搜索、运行环境和成果：已完成内置 Skill 渐进加载、搜索、只读办公运行时和交付。
5. UI 与生命周期：已完成简洁/展开进度、审批、附件、停止、成果胶囊、托盘后台和重新同步。
6. 回归与发行验收：自动化测试和 x64 打包已完成；真实云模型效果与人工视觉交互保留为发布前人工验收。
7. Chat 发起 Work：已完成请求持久化、桌面批准/拒绝、幂等后台创建、来源摘要注入和双向跳转；随后补齐健壮性（跨分片工具调用组装、非法提议降级为失败卡片、`started` 先于派发写入、重新提议复用记录、前端即时应用 `work.request.*` 事件）。真实云模型对 `request_work` 与联网搜索组合的选择效果保留为发布前人工验收。

## 5. A01–A21 验收矩阵

| 项 | 自动化或代码证据 | 当前结果 |
| --- | --- | --- |
| A01 | Chat 使用 `IChatProvider.StreamAsync`，Work 工具注册只注入 Work Agent；Chat 回归测试 | 通过 |
| A02 | `WorkProviderTests`、持久化 context/tool result 测试 | 通过 |
| A03–A04 | `ToolBatchRunsInParallelAndQueuedMessageEntersNextRoundInOrder` | 通过 |
| A05 | `WorkspaceCreatesThreeDirectoriesAndKeepsDuplicateInputs` | 通过 |
| A06–A07 | AppContainer profile 隔离、外部文件、junction 和子进程探针 | 通过 |
| A08–A09 | `WorkApprovalServiceTests`，审批状态持久化；窗口关闭仅隐藏且后端继续 | 通过 |
| A10–A11 | stop/cancel 工具测试、Job Object 子进程测试、停止后继续测试 | 通过 |
| A12 | `DifferentConversationsRunConcurrentlyWithoutSharingWorkspaces`；托盘保持后端 | 通过 |
| A13 | Coordinator/Agent dispose 取消所有 run；SQLite/工作区持久化 | 通过 |
| A14 | `DeliverFilesToolTests`；artifact-id 后端校验后才交给 Explorer | 通过 |
| A15 | `SkillToolTests` 和四个内置 Skill | 通过 |
| A16 | 打包运行时导入探针、运行时写入拒绝、每对话依赖目录约定 | 通过 |
| A17 | AppContainer 有/无网络 capability 对照探针；文件越权仍拒绝 | 通过 |
| A18 | Work activity、任务进度/下一步说明、展开工具详情、真实状态/错误 UI | 代码与类型检查通过，需人工视觉确认 |
| A19 | 工具错误进入下一轮 context；Agent loop 无硬编码业务重试次数 | 通过 |
| A20 | `ContextLimitFailsExplicitlyWithoutDroppingHistoryOrCallingModel` | 通过 |
| A21 | `ChatToolCallPersistsProposalWithoutCreatingWorkBeforeApproval`、`ApprovalCreatesOneBackgroundWorkConversationWithBidirectionalOrigin`、`InitializeResumesApprovedRequestWithoutCreatingDuplicateWorkConversation`、`InvalidWorkProposalFailsGracefullyWithoutInterruptingTheChatRun`、`ReproposingARejectedWorkRequestRevivesTheSameRecord`、`InitializeDoesNotReplayARequestThatAlreadyStarted`、`NativeStreamAssemblesFunctionCallArgumentsFromFragments`；批准卡片与双向来源 UI | 自动化通过，需真实模型与人工视觉确认 |

## 6. 安全边界与发布前人工检查

- AppContainer 允许联网，因此沙箱内可读的工作区内容可能被发送到网络；这不是 DLP。
- 外部批准命令以当前用户身份由可信后端执行，但仍受 Job Object 管理。UI 必须展示明确命令，文件目录授权不会自动授权它。
- reparse point 校验防止 Agent 借已有链接扩大目录 grant。和所有同用户态代理一样，它不试图对抗同时拥有同一 Windows 用户账户并能竞争修改路径的恶意本地进程。
- 发布前用真实 API Key 人工完成一次 DOCX/PPTX/XLSX/PDF 任务、一次拒绝/批准流程、一次中途修改和一次停止；检查生成文件内容与视觉质量。此项验证模型和办公成果质量，不替代安全自动化测试。
