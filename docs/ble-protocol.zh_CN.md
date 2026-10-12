# 小猫助手 BLE 协议 v1

## GATT 布局

设备作为 BLE peripheral，只允许一个手机连接。

| 特征 | 方向 | 属性 | 用途 |
| --- | --- | --- | --- |
| Control RX | App → 设备 | Write / Write Without Response | App 状态、ASR 文本、AI 文本和错误 |
| Control TX | 设备 → App | Notify | 录音生命周期、用户确认、新会话和设备状态 |
| Audio TX | 设备 → App | Notify | IMA-ADPCM 音频帧 |

所有特征要求已加密链路。App 连接后请求较大 MTU；MTU 不能容纳完整音频帧时，设备报告 `MTU_TOO_SMALL` 并拒绝录音。

## 控制分片

控制消息使用小端二进制头，后接 UTF-8 或类型专用负载：

```text
offset  size  field
0       2     magic = 0xCA71
2       1     version = 1
3       1     message_type
4       2     message_id
6       2     fragment_offset
8       2     total_length
10      N     payload fragment
```

- 同一方向的 `message_id` 单调递增，溢出后从 1 继续。
- 第一片 offset 为 0；后续 offset 必须等于当前已接收长度。
- 接收方只维护一个正在重组的消息，新消息会放弃旧的不完整消息。
- 长度超限、offset 不连续、版本不支持或 UTF-8 非法时拒绝整条消息。
- BLE 已提供链路层 CRC；控制层不重复添加 CRC。

## 控制消息类型

设备发送：

| 值 | 名称 | 负载 |
| ---: | --- | --- |
| `0x01` | HELLO | 设备能力 JSON |
| `0x10` | RECORD_START | 录音元数据 |
| `0x11` | RECORD_END | 录音 ID、时长、帧数 |
| `0x12` | RECORD_CANCEL | 录音 ID、原因码 |
| `0x13` | TRANSCRIPT_CONFIRM | 录音 ID |
| `0x14` | TRANSCRIPT_RETRY | 录音 ID |
| `0x15` | TRANSCRIPT_CANCEL | 录音 ID |
| `0x20` | NEW_CONVERSATION | 本地会话代号 |
| `0x21` | CANCEL_TURN | 当前录音 ID |
| `0x7E` | PING | 单调毫秒计数低 32 位 |

App 发送：

| 值 | 名称 | 负载 |
| ---: | --- | --- |
| `0x81` | APP_READY | App 能力 JSON |
| `0x82` | ASR_STARTED | 录音 ID |
| `0x83` | ASR_RESULT | 完整 UTF-8 转写文本 |
| `0x84` | ASR_ERROR | 用户可读 UTF-8 错误 |
| `0x85` | CHAT_STARTED | 录音 ID |
| `0x86` | CHAT_DELTA | UTF-8 增量文本 |
| `0x87` | CHAT_DONE | 完整回复 UTF-8 字节数 |
| `0x88` | CHAT_ERROR | 用户可读 UTF-8 错误 |
| `0x89` | SESSION_RESET | 新会话代号 |
| `0x8A` | APP_STATUS | 网络/API 状态 JSON |
| `0x8B` | TIME_SYNC | 手机当地午夜后的秒数 `u32` |
| `0x8C` | SCREEN_TIMEOUT | 自动熄屏秒数 `u32`；`0` 表示永不 |
| `0xFE` | PONG | 原样返回 PING 负载 |

## 类型专用负载

所有整数均为小端：

- `RECORD_START`：`recording_id:u16, sample_rate:u32, samples_per_frame:u16, codec:u8`；`codec=1` 表示帧独立 IMA-ADPCM。
- `RECORD_END`：`recording_id:u16, duration_ms:u32, frame_count:u16`。
- `RECORD_CANCEL`：与 `RECORD_END` 相同的 8 字节结构，帧数可为 0。
- `TRANSCRIPT_CONFIRM` / `TRANSCRIPT_RETRY` / `TRANSCRIPT_CANCEL` / `CANCEL_TURN`：`recording_id:u16`。
- `NEW_CONVERSATION` / `SESSION_RESET`：新会话代号 `u32`；App 也可以对 `SESSION_RESET` 发送空负载。
- `TIME_SYNC`：手机当地午夜后的秒数 `u32`，范围 `0..86399`。App 在每次握手后发送；设备按单调时钟继续计时，重启且尚未同步时显示 `--:--`。
- `SCREEN_TIMEOUT`：自动熄屏秒数 `u32`，范围 `0..86400`。`0` 表示永不熄屏。App 在握手后及设置变更时发送；设备保存到 NVS。

## 显示字符范围

App 发给卡片的 ASR 和 AI 文本只保留固件字库覆盖的 ASCII、CJK 常用汉字、CJK 标点和全角字符。Emoji、颜文字中的扩展符号及其它未覆盖码点在发送前移除，避免设备显示缺字方框；模型系统提示同时要求使用简体中文纯文本和常见标点。

## 音频帧

每个 Audio TX 通知包含一个 20 ms IMA-ADPCM 帧：

```text
offset  size  field
0       2     magic = 0xCA72
2       1     version = 1
3       1     flags；bit0=首帧，bit1=末帧
4       2     recording_id
6       2     sequence
8       2     sample_count，通常为 320
10      2     首样本 predictor，int16 little-endian
12      1     初始 step_index，0..88
13      1     reserved = 0
14      N     从第二个样本开始，每样本 4 bit，低半字节在前
```

320 个采样的帧大小为 174 字节。App 用录音 ID 和序号检测缺帧；发现缺帧后整次录音失败，不把损坏音频提交 ASR。

## 错误恢复

- BLE 断开：设备停止录音并清空队列；App 丢弃未完成音频。
- ASR 失败：设备显示错误并允许重录。
- AI 流中断：保留已收到文本并标记不完整，允许从本轮问题重试。
- API Key 无效：设备只显示通用配置提示，具体服务错误留在 App，避免泄露鉴权信息。
