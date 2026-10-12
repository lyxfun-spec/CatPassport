#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define CAT_PROTOCOL_MAGIC 0xCA71U
#define CAT_AUDIO_MAGIC 0xCA72U
#define CAT_PROTOCOL_VERSION 1U
#define CAT_CONTROL_HEADER_SIZE 10U
#define CAT_AUDIO_HEADER_SIZE 14U
#define CAT_TRANSCRIPT_MAX 2048U
#define CAT_ANSWER_MAX 12288U

typedef enum {
    CAT_MSG_HELLO = 0x01,
    CAT_MSG_RECORD_START = 0x10,
    CAT_MSG_RECORD_END = 0x11,
    CAT_MSG_RECORD_CANCEL = 0x12,
    CAT_MSG_TRANSCRIPT_CONFIRM = 0x13,
    CAT_MSG_TRANSCRIPT_RETRY = 0x14,
    CAT_MSG_TRANSCRIPT_CANCEL = 0x15,
    CAT_MSG_NEW_CONVERSATION = 0x20,
    CAT_MSG_CANCEL_TURN = 0x21,
    CAT_MSG_PING = 0x7e,
    CAT_MSG_APP_READY = 0x81,
    CAT_MSG_ASR_STARTED = 0x82,
    CAT_MSG_ASR_RESULT = 0x83,
    CAT_MSG_ASR_ERROR = 0x84,
    CAT_MSG_CHAT_STARTED = 0x85,
    CAT_MSG_CHAT_DELTA = 0x86,
    CAT_MSG_CHAT_DONE = 0x87,
    CAT_MSG_CHAT_ERROR = 0x88,
    CAT_MSG_SESSION_RESET = 0x89,
    CAT_MSG_APP_STATUS = 0x8a,
    CAT_MSG_TIME_SYNC = 0x8b,
    CAT_MSG_SCREEN_TIMEOUT = 0x8c,
    CAT_MSG_PONG = 0xfe,
} cat_message_type_t;

typedef enum {
    CAT_PROTOCOL_ERROR = -1,
    CAT_PROTOCOL_INCOMPLETE = 0,
    CAT_PROTOCOL_COMPLETE = 1,
} cat_protocol_result_t;

/*
 * 单消息顺序重组器。缓冲由调用方提供并覆盖整个连接生命周期；模块不分配堆。
 * GATT write 必须按 offset 顺序到达。新 message_id 的 offset=0 分片会安全放弃旧消息。
 */
typedef struct {
    uint8_t *buffer;
    size_t capacity;
    size_t received;
    size_t total;
    uint16_t message_id;
    uint8_t message_type;
    bool active;
} cat_protocol_reassembler_t;

typedef struct {
    uint8_t type;
    uint16_t message_id;
    const uint8_t *payload;
    size_t payload_length;
} cat_protocol_message_t;

void cat_protocol_reassembler_init(cat_protocol_reassembler_t *state,
                                   uint8_t *buffer, size_t capacity);
cat_protocol_result_t cat_protocol_receive(cat_protocol_reassembler_t *state,
                                           const uint8_t *fragment, size_t length,
                                           cat_protocol_message_t *message);
size_t cat_protocol_write_fragment(uint8_t *output, size_t output_size,
                                   uint8_t type, uint16_t message_id,
                                   size_t offset, const uint8_t *payload,
                                   size_t payload_length, size_t total_length);
bool cat_protocol_utf8_valid(const uint8_t *text, size_t length);
size_t cat_protocol_utf8_prefix(const uint8_t *text, size_t length, size_t limit);
