<p align="right">
  <strong>简体中文</strong> · <a href="desktop-agent-architecture.md">English</a>
</p>

# 桌面 AI Agent 客户端重构设计

## 1. 文档状态

本文档是 Cat Assistant Windows 客户端重构的产品与技术基线，覆盖首个可交付版本的 Chat 模式、AI 卡片集成，以及未来 Work 模式的接入边界。

本轮已经确认的产品决定如下：

- 客户端是一个桌面 AI Agent 产品，不再定位为 AI 卡片调试工具或 Windows BLE 网关。
- Chat 与 Work 是两套隔离的运行框架，但共享同一个桌面窗口、会话列表和消息界面。
- 第一版只实现 Chat；Work 选择器保留并显示为“即将推出”。
- Chat 是普通云端 AI 对话框架，只提供纯文字对话和阿里云联网搜索，不存在本地运行环境。
- Work 后续接入独立的开源 Agent 后端，每个 Work 会话拥有独立工作目录并由沙箱管理本地权限。
- AI 卡片只连接 Chat 会话；桌面和卡片既可共享同一对话，也可分别使用不同对话。
- 新用户消息不会排队等待。它会立即打断同一会话的当前生成，将已输出的 AI 文字作为被截断回复保留，再使用最新上下文重新生成。
- 第一版仅支持文字，不支持图片、附件、编辑用户消息、重新生成或消息分支。
- AI 消息只提供“复制”操作；被打断的消息显示“已中断”。
- 保留并迁移现有 API Key、卡片配对和用户设置；旧测试对话不导入新版会话列表，也不主动删除旧数据。
- 新版助手采用中性、简洁、可靠的表达风格；小猫元素只用于品牌与硬件，不再强制回复可爱化或禁用 Markdown。

## 2. 产品目标

### 2.1 目标

第一版必须成为一个可独立使用的桌面 AI 对话客户端：

1. 用户无需连接 AI 卡片即可完成完整的多会话 Chat 体验。
2. 用户连接卡片后，可以在卡片和桌面之间自然地继续同一 Chat 对话。
3. 卡片也可以创建自己的 Chat 对话，而不强迫桌面切换过去。
4. 桌面可以查看并打开卡片创建的对话，也可以让卡片重新跟随桌面当前对话。
5. Chat 可以使用阿里云联网搜索并展示来源。
6. 数据模型、前端外壳和运行事件能够在以后无破坏地接入独立 Work Runtime。

### 2.2 非目标

第一版不实现：

- Work Agent 后端、工作目录、沙箱或本地工具；
- Chat 对本地文件、Shell、应用或系统设置的访问；
- 图片、音频附件、普通文件附件或多模态输入；
- 用户消息编辑、AI 回复重新生成、对话分支；
- MCP、插件市场、知识库、多 Agent 或团队协作；
- 云同步、用户账户或跨设备同步；
- 卡片固件中的“跟随电脑”新操作。第一版只预留协议和状态，桌面端提供重新跟随入口。

## 3. 总体架构

```text
Electron Main Process
├─ 窗口、托盘与生命周期
├─ 安全 IPC 白名单
└─ .NET Application Host 生命周期
              │
              ▼
React Renderer ── typed IPC ── .NET Application Host
                                ├─ ConversationService
                                ├─ ConversationRepository
                                ├─ ConversationRunManager
                                ├─ ChatRuntime
                                │  └─ AliyunChatProvider
                                ├─ DeviceConversationService
                                ├─ DeviceConnectionService
                                ├─ SettingsService
                                └─ WorkRuntimeAdapter（仅接口）
```

保留 Electron + React + TypeScript 前端和 .NET 后台。现有 BLE、音频编解码、卡片协议、SQLite 与 DPAPI 实现继续复用，但不再由单个协调器同时管理设备、会话、模型和设置。

### 3.1 领域边界

| 领域 | 职责 | 不负责 |
| --- | --- | --- |
| Conversation | 会话与消息持久化、列表、标题、删除、激活状态 | 调用模型、连接设备 |
| Run | 同一会话的生成生命周期、取消、打断、流式事件 | 消息排版、BLE 协议 |
| Chat Runtime | 组装上下文、调用千问、解析联网来源 | 本地文件与命令 |
| Device Connection | 扫描、绑定、重连、健康检查、音频传输 | 决定会话归属 |
| Device Conversation | 卡片当前会话、跟随状态、卡片消息路由 | BLE 传输细节 |
| Settings | API Key、模型、设备与应用偏好 | 会话历史 |
| Work Adapter | 未来将统一客户端事件映射到第三方 Agent | 在第一版中执行任何 Work 任务 |

## 4. 统一会话模型

### 4.1 Conversation

```text
Conversation
- id: string
- mode: chat | work
- title: string
- titleSource: fallback | generated | user
- createdAt: timestamp
- updatedAt: timestamp
- archivedAt: timestamp?
- originConversationId: string?
- runtimeMetadata: JSON?
```

第一版仅创建 `chat` 会话。`work` 值从第一天进入数据库与 IPC 类型，但没有可运行的 Runtime。

标题规则：

1. 新会话创建时使用“新对话”作为临时标题。
2. 第一条用户消息提交后，可以先用安全截断文本作为即时标题。
3. 第一轮成功回答后，后台异步请求模型生成简短标题。
4. 用户手动重命名后，`titleSource=user`，不再自动覆盖。

### 4.2 Message

```text
Message
- id: string
- conversationId: string
- role: user | assistant | system
- source: desktop | device | runtime
- status: complete | streaming | interrupted | failed
- sequence: integer
- content: string
- createdAt: timestamp
- completedAt: timestamp?
- runId: string?
- metadata: JSON?
```

`sequence` 是一个会话内严格递增的顺序号。模型上下文按 `sequence` 读取，不依赖本地时钟排序。

只有用户与 AI 的可见正文进入正常对话上下文。内部系统提示、搜索运行事件、原始服务响应和诊断信息不能伪装成可见消息。

### 4.3 Run

```text
Run
- id: string
- conversationId: string
- runtime: chat | work
- state: starting | searching | generating | completed | interrupted | failed
- triggerMessageId: string
- assistantMessageId: string?
- startedAt: timestamp
- completedAt: timestamp?
- finishReason: stop | interrupted | error
- errorCode: string?
- usage: JSON?
```

Run 用于并发控制、恢复、诊断和统计，不直接显示成消息。

### 4.4 Citation

```text
Citation
- id: string
- messageId: string
- ordinal: integer
- title: string
- url: string
- snippet: string?
- sourceName: string?
```

引用属于最终或被截断的 AI 消息。前端不能信任远程标题或 URL；必须转义文本并只允许安全协议。

## 5. 消息打断与上下文语义

### 5.1 核心规则

同一会话永远只有一个活动 Run，但新消息不进入等待队列。收到新用户消息后立即执行“打断并重启”。不同会话拥有独立 Run，可以并行生成。

### 5.2 原子顺序

```text
收到新用户消息
  1. 为会话加短生命周期命令锁
  2. 请求取消当前 Run
  3. 固化当前 AI 流式正文
  4. 若正文非空，标记 assistant 消息为 interrupted
  5. 若正文为空，删除仅用于流式占位的 assistant 消息
  6. 写入新的 user 消息
  7. 提交数据库事务
  8. 释放命令锁
  9. 以最新已提交上下文启动新 Run
```

命令锁只保护状态切换和数据库提交，不等待远程模型请求完全返回。取消通过 `CancellationToken` 传播；迟到的流式片段带有旧 `runId`，必须被丢弃。

### 5.3 示例

AI 尚未输出正文：

```text
user: 第一条消息
user: 第二条消息
```

AI 已经输出部分正文：

```text
user: 第一条消息
assistant [interrupted]: 已经生成但被截断的部分
user: 第二条消息
```

AI 在打断前已经完整提交：

```text
user: 第一条消息
assistant [complete]: 完整回答
user: 第二条消息
```

### 5.4 搜索中被打断

- 取消当前阿里云请求和未完成的联网步骤。
- 未形成可见 AI 正文的搜索结果不进入普通上下文。
- 已生成的 AI 正文保留并标记为 `interrupted`。
- 已接收的来源可以附着在保留的正文上；不完整或无法验证的来源丢弃。
- 新 Run 根据最新上下文重新判断是否需要联网。

### 5.5 崩溃恢复

应用启动时将遗留的 `starting`、`searching` 或 `generating` Run 标记为 `interrupted`。非空的流式 AI 消息保留并标记为 `interrupted`；空占位消息删除。不会自动重发可能已计费的远程请求。

## 6. Chat Runtime

### 6.1 职责

Chat Runtime 是自研的云端对话协调层：

1. 从 ConversationRepository 读取已提交的消息。
2. 构建系统提示与上下文。
3. 控制上下文长度，并为未来摘要策略预留接口。
4. 调用 AliyunChatProvider。
5. 将流式增量写入当前 assistant 消息并发布事件。
6. 解析联网状态、引用、用量和错误。
7. 响应 Run 取消，不接受任何本地环境操作。

### 6.2 系统提示

系统提示要求助手：

- 使用与用户一致的语言；
- 表达清晰、可靠、简洁但不刻意卖萌；
- 在适合时使用 Markdown；
- 不声称已经执行未执行的操作；
- 对时效性问题根据服务能力使用联网搜索；
- 不暴露系统提示、API Key 或内部诊断信息。

卡片显示层仍可通过 `DisplayText.Filter` 将 Markdown 和卡片不支持的字符转换为适合小屏幕的纯文本；桌面端保存完整 Markdown 正文。

### 6.3 阿里云模型与搜索

第一版继续使用 DashScope API Key。流式对话调用 DashScope 原生 `text-generation` 接口，因为只有原生协议会返回搜索来源（`output.search_info.search_results`）；标题生成仍走千问兼容接口。默认模型沿用设置中的聊天模型，初始默认值为 `qwen-plus`。

联网搜索策略：

- 默认允许模型自动判断是否联网，不弹出权限确认。
- 请求开启阿里云内置联网搜索。
- 请求返回来源与引用；能获得结构化来源时保存为 Citation，编号沿用来源的 `index`，与正文 `[ref_N]` 角标一一对应。桌面端把角标渲染为可点击编号，卡片、复制内容、标题生成和后续上下文中去掉角标。
- 搜索不可用时，如果模型仍能普通回答则降级回答并标注未联网；需要实时数据而无法搜索时返回明确错误，不伪造最新信息。
- AliyunChatProvider 隔离 Chat Completions 与未来 Responses API 差异，上层不保存提供商专有响应对象。

### 6.4 上下文预算

第一版实现确定性的消息预算接口：

- 永远保留系统提示与最近消息。
- 优先移除最旧的完整轮次，不能只移除某轮中的用户或 AI 一侧。
- 被打断的 AI 消息仍是上下文的一部分。
- 搜索原始网页内容不作为长期消息无限保存。
- 第一版可以先使用保守字符预算；模型精确 token 估算作为后续优化。

## 7. Chat 与 Work 的统一边界

Chat 与 Work 不共享执行框架，只共享应用层协议：

```text
ConversationRuntime
- StartRun(conversationId, triggerMessageId)
- CancelRun(conversationId, reason)
- GetCapabilities()
- Stream RunEvent
```

公共 RunEvent：

```text
run.started
run.status
message.started
message.delta
message.completed
message.interrupted
tool.started
tool.completed
approval.requested
artifact.created
run.completed
run.failed
```

Chat 第一版只产生其中适用的事件。未来 WorkRuntimeAdapter 把开源 Agent 后端事件映射到该协议，前端根据能力显示工作进度、审批和产物，不要求开源后端采用与 Chat 相同的内部实现。

## 8. Chat 发起 Work 请求

第一版只建立数据和接口，不向模型暴露不可执行的工具。

```text
WorkRequest
- id
- sourceConversationId
- sourceMessageId
- title
- prompt
- contextSummary
- state: proposed | approved | rejected | started | failed
- targetConversationId?
```

Work 上线后的流程：

1. Chat AI 通过 `request_work` 生成标题、执行提示词和上下文摘要。
2. 桌面 Chat 中显示批准卡片。
3. 用户批准后创建 Work 会话和工作目录。
4. Work Runtime 接收整理后的提示词，不默认复制完整 Chat 历史。
5. Chat 与 Work 双方保存可跳转的来源关系。
6. 卡片上触发的请求必须在桌面批准。

## 9. AI 卡片会话绑定

### 9.1 状态

```text
desktopActiveConversationId: string?
deviceActiveConversationId: string?
deviceFollowDesktop: boolean
```

这些是应用会话状态，不与 BLE 连接状态混为一体。

### 9.2 默认跟随

- 卡片连接成功时，如果桌面当前是 Chat 会话，则卡片绑定该会话并进入跟随状态。
- 如果桌面当前没有 Chat 会话，则创建或选择最近的 Chat 会话。
- 跟随状态下，桌面切换到另一个 Chat 会话时同步更新卡片绑定。
- 桌面未来切换到 Work 会话时，卡片保持最后一个 Chat 会话，不进入 Work。

### 9.3 卡片新建会话

卡片发送现有 `NewConversation` 控制消息后：

1. ConversationService 创建新 Chat 会话。
2. `deviceActiveConversationId` 指向新会话。
3. `deviceFollowDesktop=false`。
4. 不修改 `desktopActiveConversationId`。
5. 发布会话列表更新事件，桌面侧边栏立即出现新对话。

### 9.4 重新跟随

第一版桌面设备面板提供：

- “打开卡片当前对话”；
- “让卡片跟随当前对话”。

卡片固件以后在主界面增加“跟随电脑”操作。第一版只在协议模型中保留相应命令，不改固件交互。

### 9.5 消息路由

- 卡片转写确认后，消息写入 `deviceActiveConversationId`，`source=device`。
- 桌面消息写入 `desktopActiveConversationId`，`source=desktop`。
- 如果两个 ID 相同，双方观察同一 Run 和流式回复。
- 如果 ID 不同，两条会话可独立运行，卡片只显示其绑定会话的状态。
- 卡片断开只取消正在依赖卡片录音或传输的操作，不取消纯桌面 Chat Run。

## 10. 前端信息架构

### 10.1 应用布局

```text
┌──────────────────┬───────────────────────────────────────┐
│ 品牌              │ 会话标题       Chat           更多   │
│                   ├───────────────────────────────────────┤
│ ＋ 新对话         │                                       │
│ 搜索对话          │               消息                    │
│                   │                                       │
│ 今天              │                                       │
│  Chat 对话        │                                       │
│  Work 对话        │                                       │
│                   ├───────────────────────────────────────┤
│───────────────────│ 输入消息……                    发送   │
│ AI 卡片状态       │                                       │
│ 设置              │                                       │
└──────────────────┴───────────────────────────────────────┘
```

### 10.2 侧边栏

- 品牌图标与产品名；
- 新对话按钮；
- 会话搜索；
- 按“今天、昨天、最近 7 天、更早”分组的会话；
- Chat / Work 小图标；
- 当前会话高亮；
- 会话更多菜单：重命名、删除；
- 左下角全局 AI 卡片状态；
- 设置入口。

侧边栏可折叠。折叠后保留新对话、模式图标、卡片状态和设置图标。

### 10.3 新建对话

新建面板使用分段滑块：

```text
[ Chat ] [ Work · 即将推出 ]
```

第一版 Work 禁用。创建 Chat 后立即切换到新会话并聚焦输入框。

### 10.4 空状态

空对话只显示简短欢迎语和输入框，不展示开发状态、BLE 诊断、PDU 或后台架构说明。未配置 API Key 时显示可操作的设置引导。

### 10.5 消息

- 用户消息使用轻量气泡并靠右；
- AI 消息为开放正文，不使用厚重气泡；
- 支持安全 Markdown、列表、表格、代码块和链接；
- AI 消息底部只有“复制”；
- `interrupted` 消息显示“已中断”；
- 联网回答在正文引用位置显示编号，并在消息下方列出来源；
- 不默认显示头像、角色名、时间或 token；
- 流式正文持续更新，迟到的旧 Run 增量不得写入当前消息。

### 10.6 输入框

- 支持多行纯文字；
- Enter 发送，Shift+Enter 换行；
- 生成过程中仍允许发送，新消息直接打断当前 Run；
- 禁止发送空白内容；
- 提交后立即清空输入框；失败时消息仍保留并显示可重试的错误状态；
- 可以提供独立的小型停止按钮，但发送新消息不要求用户先停止。

### 10.7 卡片入口

左下角展示：

```text
● AI 卡片
  已连接 · 当前对话标题
```

点击打开设备面板：

- 用户可读连接状态；
- 卡片当前 Chat 对话；
- 打开卡片对话；
- 让卡片跟随当前 Chat；
- 搜索、绑定、更换或忘记设备；
- 自动重连和屏幕休眠；
- 折叠的高级诊断（地址、RSSI、PDU、底层错误）。

## 11. 设置与首次启动

### 11.1 首次启动

首次启动流程：

1. 欢迎页说明客户端可独立使用，卡片是可选硬件。
2. 配置 DashScope API Key。
3. 可选连接 AI 卡片，也可以跳过。
4. 进入新 Chat 对话。

未配置 Key 时仍可查看历史和设置，但不能发起模型请求。

### 11.2 设置分组

常规：

- 关闭窗口后驻留；
- 开机启动（如果实现）；
- 主题（第一版可只提供系统/浅色）。

AI 服务：

- DashScope API Key；
- 默认 Chat 模型；
- 联网搜索开关，默认开启；
- 高级搜索与模型选项以后扩展。

AI 卡片：

- 当前绑定设备；
- 自动重连；
- 屏幕休眠；
- 忘记设备；
- 高级诊断。

数据与隐私：

- 本地保存位置说明；
- API Key 使用 Windows DPAPI 加密；
- 删除全部新版会话；
- 旧版数据迁移状态。

关于：

- 应用版本；
- 诊断信息；
- 第三方声明。

## 12. SQLite 迁移

### 12.1 版本化

新增 `schema_migrations` 或 `PRAGMA user_version`，所有迁移必须可重复判断、在事务中执行并保留旧表。

建议的新表：

```sql
conversations_v2
messages_v2
runs
citations
app_state
work_requests
```

使用新表名避免与旧 Demo 的 `conversations`、`messages` 结构冲突。完成稳定迁移后再决定是否统一命名，不在第一版删除旧表。

### 12.2 保留数据

保留：

- DPAPI 中的 DashScope API Key；
- 模型名称；
- 卡片地址和名称；
- 自动重连；
- 卡片屏幕休眠。

不导入：

- 旧 Demo 的测试会话和消息。

不删除：

- 旧 `conversations` 与 `messages` 表；
- 无法识别的旧设置。

## 13. IPC 与事件

避免继续用一个包含设备、消息和设置的巨大 Snapshot。IPC 按领域划分。

### 13.1 查询与命令

```text
app.bootstrap

conversations.list
conversations.get
conversations.create
conversations.rename
conversations.delete
conversations.activate
conversations.send
conversations.cancel

device.getState
device.scan
device.connect
device.forget
device.followDesktop
device.openConversation

settings.get
settings.save
```

### 13.2 事件

```text
conversations.changed
conversation.changed
message.started
message.delta
message.completed
message.interrupted
run.status
device.changed
settings.changed
backend.error
```

每个流式事件必须包含 `conversationId`、`messageId` 和 `runId`，前端据此拒绝过期增量。

## 14. 错误与降级

### 14.1 AI 服务

- API Key 缺失：输入区显示设置引导，不丢失草稿。
- Key 无效或无模型权限：显示明确错误和设置入口。
- 网络错误：保留用户消息，Run 标记失败，允许通过再次发送继续。
- 搜索失败：能安全普通回答时降级；实时问题不能可靠回答时明确说明。
- 取消：不作为错误弹窗；有正文则标记已中断。

### 14.2 卡片

- 卡片断开不阻止桌面 Chat。
- 自动重连状态只出现在卡片入口和设备面板，不占据主聊天区域。
- 卡片消息发送后断开：已提交的用户消息仍属于会话，桌面继续生成；卡片重连后恢复最新稳定状态。
- 卡片绑定会话被删除：卡片绑定最近 Chat 会话；没有可用会话则创建一个。

### 14.3 后台

- .NET 后台退出：主界面显示不可忽略的恢复提示。
- 数据库迁移失败：停止写入并保留旧数据，不能部分迁移后继续运行。
- 单个会话 Run 失败不能阻塞其他会话或设备重连。

## 15. 安全与隐私

- Electron 保持 `contextIsolation=true`、`nodeIntegration=false` 和渲染进程沙箱。
- 渲染进程只能调用显式白名单 IPC。
- API Key 不返回渲染进程，只返回“已配置”状态。
- API Key 继续由当前 Windows 用户的 DPAPI 保护。
- Markdown 禁止原始 HTML，链接使用安全协议并通过 Electron 外部打开策略处理。
- 远程标题、摘要、来源和错误文本全部视为不可信输入。
- Chat Runtime 不暴露本地文件、进程、Shell 或任意 URL 请求工具。
- 日志不记录 API Key、完整授权头、原始录音或未脱敏的隐私内容。

## 16. 验收标准

### 16.1 自动测试

至少覆盖：

- 会话创建、重命名、删除、排序和搜索；
- 临时标题、自动标题与用户标题优先级；
- 消息顺序号和持久化恢复；
- 无正文时的生成打断；
- 有部分正文时的生成打断；
- 完成与取消竞态；
- 旧 Run 迟到增量被拒绝；
- 不同会话并行、同一会话单 Run；
- 卡片跟随、脱离、创建会话与重新跟随；
- 卡片与桌面在同一或不同会话的消息路由；
- 搜索来源解析和不安全 URL 拒绝；
- 旧设置保留、新表迁移和崩溃恢复；
- reducer 对流式、已中断、失败和设备事件的处理。

### 16.2 桌面集成验收

- 启动后进入新版 Chat 外壳，不再进入连接或状态调试页。
- 未连接卡片时可以完整聊天。
- 左侧列表混合模式数据结构正确，Work 显示为不可选的“即将推出”。
- 生成过程中连续发送两条消息，第一轮立即停止且上下文顺序符合本设计。
- 重启后恢复会话、消息标题和卡片绑定状态。
- 搜索回答显示来源，普通回答不显示空来源区域。
- 关闭窗口后后台按设置驻留，重新打开后状态一致。

### 16.3 设备验收

本轮不要求修改固件，但桌面与现有固件集成需验证：

- 卡片连接、重连、录音、转写确认与回答显示；
- 卡片新建对话不会强迫桌面切换；
- 桌面打开卡片对话后可继续上下文；
- 桌面让卡片跟随当前 Chat 后，后续状态与回复同步；
- 卡片与桌面使用不同对话时互不覆盖；
- 卡片断开不终止独立桌面 Chat。

## 17. 实施顺序

1. 建立本文档、公共类型和 SQLite v2 迁移。
2. 实现 ConversationRepository 与 ConversationService。
3. 实现逐会话 ConversationRunManager 和打断测试。
4. 实现 AliyunChatProvider、流式 Chat Runtime、搜索来源和错误映射。
5. 拆分 DeviceConnectionService 与 DeviceConversationService。
6. 建立新版 IPC 命令与事件，保留必要的旧接口兼容期。
7. 重写 React 应用外壳、会话列表、消息区、输入框、设备面板和设置。
8. 完成旧设置迁移、首次启动和恢复流程。
9. 运行 TypeScript、React、.NET 单元测试和桌面集成检查。
10. 在用户授权后进行 AI 卡片真机联调；不因桌面构建成功而宣称设备验收通过。

