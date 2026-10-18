<p align="right">
  <a href="desktop-agent-architecture.zh_CN.md">简体中文</a> · <strong>English</strong>
</p>

# Desktop AI Agent Client Redesign

## 1. Document status

This document is the product and technical baseline for the Cat Assistant Windows client redesign. It covers the first deliverable Chat mode, AI Card integration, and the boundary for adding Work mode later.

The following product decisions are accepted for this iteration:

- The client is a desktop AI Agent product, not an AI Card debugging utility or Windows BLE gateway.
- Chat and Work are isolated runtimes that share one desktop window, conversation list, and message presentation.
- The first version implements Chat only. The Work selector remains visible and is labeled "Coming soon."
- Chat is a normal cloud AI conversation runtime with text and Alibaba Cloud web search. It has no local execution environment.
- Work will later use an independent open-source Agent backend. Each Work conversation will have an isolated working directory and sandbox-managed local permissions.
- The AI Card connects only to Chat conversations. The desktop and Card may share one conversation or use separate conversations.
- New user messages never wait in a queue. A new message interrupts the current run in that conversation, preserves any visible partial assistant output as an interrupted reply, and starts again with the latest committed context.
- Version one is text only. It excludes images, attachments, user-message editing, regeneration, and message branches.
- Assistant messages expose only Copy. Interrupted messages show an Interrupted status.
- Existing API-key, Card pairing, and user settings are retained. Old test conversations are not imported into the new list and are not deleted.
- The redesigned assistant uses a neutral, concise, reliable voice. Cat motifs remain in the brand and hardware instead of forcing cute replies or disabling Markdown.

## 2. Product goals

### 2.1 Goals

Version one must be a complete standalone desktop AI conversation client:

1. Users can use multi-conversation Chat without connecting an AI Card.
2. After connecting a Card, users can continue the same Chat conversation across Card and desktop.
3. The Card can create its own Chat conversation without forcing the desktop to switch.
4. The desktop can open Card-created conversations and can make the Card follow the current desktop conversation again.
5. Chat can use Alibaba Cloud web search and present sources.
6. The data model, application shell, and runtime events can add an independent Work Runtime later without a destructive redesign.

### 2.2 Non-goals

Version one does not implement:

- a Work Agent backend, working directories, sandbox, or local tools;
- Chat access to local files, shells, applications, or operating-system settings;
- image, audio, ordinary file, or multimodal attachments;
- user-message editing, assistant regeneration, or message branching;
- MCP, a plugin marketplace, knowledge bases, multi-agent, or team collaboration;
- cloud accounts, cloud sync, or cross-device sync;
- the new Follow Desktop action in Card firmware. Version one reserves protocol and state and provides the desktop-side action.

## 3. High-level architecture

```text
Electron Main Process
├─ window, tray, and lifecycle
├─ secure IPC allowlist
└─ .NET Application Host lifecycle
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
                                └─ WorkRuntimeAdapter (interface only)
```

The Electron, React, TypeScript, and .NET split remains. Existing BLE, audio codec, Card protocol, SQLite, and DPAPI code is reused, but one coordinator no longer owns devices, conversations, models, and settings together.

### 3.1 Domain boundaries

| Domain | Responsibilities | Does not own |
| --- | --- | --- |
| Conversation | Conversation and message persistence, lists, titles, deletion, active state | Model calls, device links |
| Run | Per-conversation generation lifecycle, cancellation, interruption, streaming events | Message layout, BLE protocol |
| Chat Runtime | Context construction, Qwen calls, web-source parsing | Local files or commands |
| Device Connection | Scan, pair, reconnect, health, audio transport | Conversation ownership decisions |
| Device Conversation | Card conversation, follow state, Card message routing | BLE transport details |
| Settings | API key, model, device and application preferences | Conversation history |
| Work Adapter | Future mapping from third-party Agent events into the common client contract | Executing Work in version one |

## 4. Unified conversation model

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

Version one creates `chat` conversations only. The `work` value exists in the database and IPC types from day one but has no runnable Runtime.

Title rules:

1. A new conversation starts with the temporary title "New conversation."
2. After the first user message, safely truncated user text can provide an immediate title.
3. After the first successful answer, the backend may request a short generated title asynchronously.
4. A user rename sets `titleSource=user` and is never overwritten automatically.

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

`sequence` is strictly increasing within a conversation. Model context is ordered by `sequence`, not by local clock timestamps.

Only visible user and assistant content enters normal conversation context. Internal prompts, search events, raw provider responses, and diagnostics cannot masquerade as visible messages.

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

Runs support concurrency control, recovery, diagnostics, and usage. They are not rendered as messages.

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

Citations belong to completed or interrupted assistant messages. The UI treats remote titles and URLs as untrusted, escapes text, and permits only safe URL schemes.

## 5. Interruption and context semantics

### 5.1 Core rule

One conversation has at most one active Run, but a new message never enters a waiting queue. Receiving a user message performs interrupt-and-restart immediately. Different conversations own independent Runs and may generate concurrently.

### 5.2 Atomic order

```text
Receive user message
  1. Acquire a short-lived conversation command lock
  2. Request cancellation of the current Run
  3. Freeze the current streamed assistant content
  4. If content is non-empty, mark the assistant message interrupted
  5. If content is empty, delete the streaming placeholder
  6. Insert the new user message
  7. Commit the database transaction
  8. Release the command lock
  9. Start a Run from the latest committed context
```

The command lock protects state transitions and database commits only. It does not wait for a remote request to finish. Cancellation propagates through `CancellationToken`. Late deltas carry the old `runId` and are discarded.

### 5.3 Examples

No assistant body was emitted:

```text
user: first message
user: second message
```

Partial assistant body was emitted:

```text
user: first message
assistant [interrupted]: generated but truncated content
user: second message
```

The assistant completed before interruption committed:

```text
user: first message
assistant [complete]: complete answer
user: second message
```

### 5.4 Interruption during search

- Cancel the current Alibaba Cloud request and unfinished web steps.
- Search results that never formed visible assistant content do not enter normal context.
- Existing assistant text is retained and marked `interrupted`.
- Sources already received may remain attached to retained text; incomplete or invalid sources are discarded.
- The new Run decides again whether current context needs the web.

### 5.5 Crash recovery

At startup, stale `starting`, `searching`, and `generating` Runs become `interrupted`. Non-empty streamed assistant messages remain and become `interrupted`; empty placeholders are deleted. Potentially billed remote calls are not automatically retried.

## 6. Chat Runtime

### 6.1 Responsibilities

Chat Runtime is the first-party cloud conversation coordinator:

1. Read committed messages from ConversationRepository.
2. Build the system prompt and context.
3. Enforce a context budget and expose an interface for future summarization.
4. Call AliyunChatProvider.
5. Persist stream deltas into the active assistant message and publish events.
6. Parse search state, citations, usage, and normalized errors.
7. Honor Run cancellation and expose no local-environment operations.

### 6.2 System prompt

The system prompt requires the assistant to:

- use the user's language;
- be clear, reliable, and concise without forced cuteness;
- use Markdown when useful;
- never claim unperformed actions;
- use web search for time-sensitive questions when available;
- never expose system prompts, API keys, or internal diagnostics.

The Card presentation layer may still use `DisplayText.Filter` to convert Markdown and unsupported characters into small-screen plain text. The desktop stores the complete Markdown answer.

### 6.3 Alibaba Cloud model and search

Version one continues to use a DashScope API key. Streaming chat calls the native DashScope `text-generation` endpoint, because only the native protocol returns search sources (`output.search_info.search_results`); title generation still uses the Qwen-compatible endpoint. The default follows the configured chat model, initially `qwen-plus`.

Web-search policy:

- The model decides automatically when to search; no permission dialog is shown.
- Requests enable Alibaba Cloud built-in web search.
- Requests ask for sources and citations; structured sources become Citation records whose ordinal is the source `index`, matching the inline `[ref_N]` markers. The desktop renders markers as clickable numbers; the card, copied text, title generation, and later model context strip them.
- If search fails but an ordinary answer is safe, respond with a clear no-search degradation. If current data is required, return a clear error instead of fabricating freshness.
- AliyunChatProvider hides Chat Completions and future Responses API differences. Provider-specific response objects do not escape into the application layer.

### 6.4 Context budget

Version one provides deterministic context budgeting:

- Always retain the system prompt and newest messages.
- Remove the oldest complete turns first; never remove only one side of a user/assistant turn.
- Interrupted assistant content remains part of context.
- Raw web pages are not retained indefinitely as normal messages.
- A conservative character budget is acceptable initially; exact token estimation is a later optimization.

## 7. Shared Chat and Work boundary

Chat and Work do not share execution frameworks. They share only an application-facing contract:

```text
ConversationRuntime
- StartRun(conversationId, triggerMessageId)
- CancelRun(conversationId, reason)
- GetCapabilities()
- Stream RunEvent
```

Common RunEvents:

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

Chat emits only applicable events in version one. A future WorkRuntimeAdapter maps third-party Agent events into this contract so the UI can render progress, approvals, and artifacts without requiring the Work backend to share Chat internals.

## 8. Chat-to-Work requests

Version one creates the data and interface boundary but does not expose a non-functional tool to the model.

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

Future flow:

1. Chat calls `request_work` with a title, execution prompt, and context summary.
2. The desktop renders an approval card in Chat.
3. Approval creates a Work conversation and working directory.
4. Work Runtime receives the prepared prompt, not the complete Chat history by default.
5. Chat and Work retain bidirectional navigation metadata.
6. Work requests originating from the Card still require desktop approval.

## 9. AI Card conversation binding

### 9.1 State

```text
desktopActiveConversationId: string?
deviceActiveConversationId: string?
deviceFollowDesktop: boolean
```

These are application conversation states and remain separate from BLE connection state.

### 9.2 Default follow behavior

- On Card connection, if the desktop is on a Chat conversation, bind the Card to it and enable follow mode.
- If there is no active Chat conversation, create one or use the most recent Chat.
- While following, switching desktop Chat conversations updates the Card binding.
- When the desktop eventually opens Work, the Card remains on its latest Chat and never enters Work.

### 9.3 Card-created conversation

When the Card sends the existing `NewConversation` control message:

1. ConversationService creates a Chat conversation.
2. `deviceActiveConversationId` points to it.
3. `deviceFollowDesktop=false`.
4. `desktopActiveConversationId` does not change.
5. A list event makes the new conversation appear immediately in the desktop sidebar.

### 9.4 Follow again

The version-one desktop device panel provides:

- Open Card conversation;
- Make Card follow current conversation.

Card firmware may later add Follow Desktop to its home screen. Version one reserves the protocol-level concept but does not modify firmware interaction.

### 9.5 Message routing

- A Card-confirmed transcript enters `deviceActiveConversationId` with `source=device`.
- A desktop message enters `desktopActiveConversationId` with `source=desktop`.
- If both IDs match, both surfaces observe the same Run and streamed reply.
- If IDs differ, conversations run independently and the Card shows only its bound conversation.
- Card disconnection cancels Card recording or transport work but not an independent desktop Chat Run.

## 10. Frontend information architecture

### 10.1 Application layout

```text
┌──────────────────┬───────────────────────────────────────┐
│ brand             │ title            Chat        more   │
│                   ├───────────────────────────────────────┤
│ + New conversation│                                       │
│ Search            │              messages                 │
│                   │                                       │
│ Today             │                                       │
│  Chat conversation│                                       │
│  Work conversation│                                       │
│                   ├───────────────────────────────────────┤
│───────────────────│ Message…                       Send   │
│ AI Card status    │                                       │
│ Settings          │                                       │
└──────────────────┴───────────────────────────────────────┘
```

### 10.2 Sidebar

- Brand icon and product name;
- New conversation;
- conversation search;
- Today, Yesterday, Last 7 days, and Older groups;
- small Chat and Work icons;
- active conversation highlight;
- rename and delete menu;
- global AI Card status at bottom left;
- Settings.

The sidebar can collapse. New conversation, mode icons, Card status, and Settings remain discoverable when collapsed.

### 10.3 New conversation

The creation panel uses a segmented slider:

```text
[ Chat ] [ Work · Coming soon ]
```

Work is disabled in version one. Creating Chat immediately activates it and focuses the composer.

### 10.4 Empty state

An empty conversation shows a short welcome and composer only. It does not expose development status, BLE diagnostics, PDU values, or backend architecture. A missing API key produces an actionable Settings prompt.

### 10.5 Messages

- User messages use a light right-aligned bubble.
- Assistant messages use open document-like layout.
- Render safe Markdown, lists, tables, code blocks, and links.
- Assistant messages expose only Copy.
- Interrupted messages show Interrupted.
- Search answers show inline citation numbers and a source list.
- Do not show avatars, repeated roles, timestamps, or tokens by default.
- Streaming content updates continuously. Late deltas from an old Run cannot update the current message.

### 10.6 Composer

- Multiline text only;
- Enter sends and Shift+Enter inserts a line break;
- sending remains available during generation and interrupts the current Run;
- whitespace-only messages are rejected;
- clear immediately after accepted submission;
- retain the submitted message on failure and render recoverable error state;
- an independent compact Stop control is allowed, but sending never requires stopping first.

### 10.7 Card entry

Bottom-left status:

```text
● AI Card
  Connected · conversation title
```

Clicking opens a device panel with:

- user-facing connection state;
- active Card Chat conversation;
- open Card conversation;
- follow current Chat;
- scan, bind, replace, or forget device;
- auto-reconnect and screen timeout;
- collapsed advanced diagnostics for address, RSSI, PDU, and low-level errors.

## 11. Settings and onboarding

### 11.1 First launch

1. Explain that the client works independently and the Card is optional hardware.
2. Configure the DashScope API key.
3. Optionally connect an AI Card or skip.
4. Enter a new Chat conversation.

Without a key, users can still inspect history and Settings but cannot start a model Run.

### 11.2 Settings groups

General:

- remain running after window close;
- launch at startup if implemented;
- system/light theme in version one.

AI service:

- DashScope API key;
- default Chat model;
- web-search toggle, enabled by default;
- future advanced search and model options.

AI Card:

- bound device;
- auto reconnect;
- screen timeout;
- forget device;
- advanced diagnostics.

Data and privacy:

- local data location;
- Windows DPAPI API-key protection;
- delete all new conversations;
- legacy migration status.

About:

- application version;
- diagnostics;
- third-party notices.

## 12. SQLite migration

### 12.1 Versioning

Add `schema_migrations` or `PRAGMA user_version`. Every migration is detectable, transactional, and preserves legacy tables.

Suggested tables:

```sql
conversations_v2
messages_v2
runs
citations
app_state
work_requests
```

New names avoid collisions with Demo `conversations` and `messages`. Stable later migrations may unify names, but version one never drops legacy tables.

### 12.2 Retained data

Retain:

- the DPAPI-protected DashScope key;
- model names;
- Card address and name;
- auto reconnect;
- Card screen timeout.

Do not import:

- old Demo conversations and messages.

Do not delete:

- old `conversations` and `messages` tables;
- unrecognized legacy settings.

## 13. IPC and events

Replace the single device/message/settings Snapshot with domain APIs.

### 13.1 Queries and commands

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

### 13.2 Events

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

Every stream event includes `conversationId`, `messageId`, and `runId` so the renderer can reject stale deltas.

## 14. Errors and degradation

### 14.1 AI service

- Missing API key: show a Settings action without losing the draft.
- Invalid key or model permission: show a precise error and Settings action.
- Network failure: retain the user message, mark Run failed, and allow continuation by sending again.
- Search failure: degrade to an ordinary answer only when safe; state inability when freshness is required.
- Cancellation: do not show an error alert; mark non-empty output interrupted.

### 14.2 Card

- Card disconnection never blocks desktop Chat.
- Auto-reconnect status stays in the Card entry and device panel, not the main chat canvas.
- If the Card disconnects after its message commits, the message remains and desktop generation continues. The Card restores the latest stable state after reconnect.
- If the bound conversation is deleted, bind to the most recent Chat or create one.

### 14.3 Backend

- A dead .NET backend produces a persistent recovery notice.
- Migration failure stops writes and preserves legacy data; the app cannot continue after a partial migration.
- Failure of one conversation Run cannot block other conversations or device reconnect.

## 15. Security and privacy

- Keep Electron `contextIsolation=true`, `nodeIntegration=false`, and renderer sandboxing.
- The renderer can call only allowlisted IPC methods.
- Never return the API key to the renderer; return configured state only.
- Continue protecting the key with DPAPI for the current Windows user.
- Disable raw HTML in Markdown. Permit safe URL schemes and use an Electron external-open policy.
- Treat remote titles, snippets, sources, and errors as untrusted input.
- Chat Runtime exposes no local files, processes, shell, or arbitrary URL-fetch tool.
- Logs exclude API keys, authorization headers, raw recordings, and unsanitized private content.

## 16. Acceptance criteria

### 16.1 Automated tests

At minimum, cover:

- create, rename, delete, sort, and search conversations;
- fallback, generated, and user title precedence;
- message sequencing and persistence recovery;
- interruption before body output;
- interruption after partial output;
- completion/cancellation races;
- rejection of stale old-Run deltas;
- concurrent different conversations and one Run per conversation;
- Card follow, detach, create conversation, and follow again;
- same/different Card and desktop conversation routing;
- source parsing and unsafe URL rejection;
- legacy setting retention, new schema migration, and crash recovery;
- renderer reduction of stream, interruption, failure, and device events.

### 16.2 Desktop integration acceptance

- Startup opens the new Chat shell rather than Connection or Status debug pages.
- Desktop Chat works without a Card.
- The sidebar data model mixes modes and Work is visibly unavailable as Coming soon.
- Sending two messages during generation interrupts immediately and produces the designed context order.
- Restart restores conversations, messages, titles, and Card binding state.
- Search answers show sources; ordinary answers omit an empty Sources region.
- Window-close residency follows Settings and state remains consistent on reopen.

### 16.3 Device acceptance

This iteration does not require firmware changes, but desktop integration with current firmware must verify:

- Card connect, reconnect, recording, transcript confirmation, and answer display;
- Card-created conversation does not force the desktop to switch;
- desktop can open and continue the Card conversation;
- desktop Follow Current Chat updates subsequent Card status and answers;
- separate Card and desktop conversations do not overwrite one another;
- Card disconnect does not terminate independent desktop Chat.

## 17. Implementation order

1. Establish this document, shared types, and SQLite v2 migration.
2. Implement ConversationRepository and ConversationService.
3. Implement per-conversation ConversationRunManager and interruption tests.
4. Implement AliyunChatProvider, streaming Chat Runtime, sources, and normalized errors.
5. Split DeviceConnectionService and DeviceConversationService.
6. Add new IPC commands and events with only the necessary compatibility bridge.
7. Rebuild the React shell, sidebar, messages, composer, device panel, and Settings.
8. Complete legacy-setting migration, onboarding, and recovery.
9. Run TypeScript, React, .NET unit, and desktop integration checks.
10. Perform physical AI Card integration only after user authorization. A desktop build alone is not device acceptance.

