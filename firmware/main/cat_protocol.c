#include "cat_protocol.h"

#include <string.h>

static uint16_t read_le16(const uint8_t *data)
{
    return (uint16_t)data[0] | ((uint16_t)data[1] << 8);
}

static void write_le16(uint8_t *data, uint16_t value)
{
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
}

void cat_protocol_reassembler_init(cat_protocol_reassembler_t *state,
                                   uint8_t *buffer, size_t capacity)
{
    if (state == NULL) return;
    *state = (cat_protocol_reassembler_t){
        .buffer = buffer,
        .capacity = capacity,
    };
}

/*
 * 接收一个完整 GATT value。成功完成时 message.payload 指向重组器缓冲，直到下一片到来。
 * 返回 ERROR 会清空当前重组状态，避免把损坏消息的尾片拼到下一条消息。
 */
cat_protocol_result_t cat_protocol_receive(cat_protocol_reassembler_t *state,
                                           const uint8_t *fragment, size_t length,
                                           cat_protocol_message_t *message)
{
    if (state == NULL || fragment == NULL || message == NULL ||
        state->buffer == NULL || length < CAT_CONTROL_HEADER_SIZE) {
        return CAT_PROTOCOL_ERROR;
    }

    const uint16_t magic = read_le16(fragment);
    const uint8_t version = fragment[2];
    const uint8_t type = fragment[3];
    const uint16_t message_id = read_le16(fragment + 4);
    const size_t offset = read_le16(fragment + 6);
    const size_t total = read_le16(fragment + 8);
    const size_t chunk = length - CAT_CONTROL_HEADER_SIZE;

    if (magic != CAT_PROTOCOL_MAGIC || version != CAT_PROTOCOL_VERSION ||
        total > state->capacity || offset > total || chunk > total - offset) {
        state->active = false;
        return CAT_PROTOCOL_ERROR;
    }

    if (offset == 0) {
        state->active = true;
        state->received = 0;
        state->total = total;
        state->message_id = message_id;
        state->message_type = type;
    } else if (!state->active || state->message_id != message_id ||
               state->message_type != type || state->total != total ||
               state->received != offset) {
        state->active = false;
        return CAT_PROTOCOL_ERROR;
    }

    if (chunk > 0) memcpy(state->buffer + offset, fragment + CAT_CONTROL_HEADER_SIZE, chunk);
    state->received += chunk;
    if (state->received != state->total) return CAT_PROTOCOL_INCOMPLETE;

    *message = (cat_protocol_message_t){
        .type = state->message_type,
        .message_id = state->message_id,
        .payload = state->buffer,
        .payload_length = state->total,
    };
    state->active = false;
    return CAT_PROTOCOL_COMPLETE;
}

/*
 * 生成单个控制分片。payload 指向完整消息；offset 指定本片起点。
 * 返回写入字节数；参数或空间不合法时返回 0。
 */
size_t cat_protocol_write_fragment(uint8_t *output, size_t output_size,
                                   uint8_t type, uint16_t message_id,
                                   size_t offset, const uint8_t *payload,
                                   size_t payload_length, size_t total_length)
{
    if (output == NULL || output_size < CAT_CONTROL_HEADER_SIZE ||
        total_length > UINT16_MAX || offset > total_length ||
        payload_length > total_length - offset ||
        payload_length > output_size - CAT_CONTROL_HEADER_SIZE ||
        (payload_length > 0 && payload == NULL)) {
        return 0;
    }

    write_le16(output, CAT_PROTOCOL_MAGIC);
    output[2] = CAT_PROTOCOL_VERSION;
    output[3] = type;
    write_le16(output + 4, message_id);
    write_le16(output + 6, (uint16_t)offset);
    write_le16(output + 8, (uint16_t)total_length);
    if (payload_length > 0) memcpy(output + CAT_CONTROL_HEADER_SIZE,
                                   payload + offset, payload_length);
    return CAT_CONTROL_HEADER_SIZE + payload_length;
}

/* 严格 UTF-8 校验：拒绝过长编码、代理区和 U+10FFFF 以上码点。 */
bool cat_protocol_utf8_valid(const uint8_t *text, size_t length)
{
    if (text == NULL && length != 0) return false;
    size_t i = 0;
    while (i < length) {
        const uint8_t first = text[i++];
        if (first <= 0x7f) continue;

        unsigned continuation;
        uint32_t codepoint;
        uint32_t minimum;
        if ((first & 0xe0) == 0xc0) {
            continuation = 1; codepoint = first & 0x1f; minimum = 0x80;
        } else if ((first & 0xf0) == 0xe0) {
            continuation = 2; codepoint = first & 0x0f; minimum = 0x800;
        } else if ((first & 0xf8) == 0xf0) {
            continuation = 3; codepoint = first & 0x07; minimum = 0x10000;
        } else {
            return false;
        }
        if (i + continuation > length) return false;
        while (continuation-- > 0) {
            const uint8_t next = text[i++];
            if ((next & 0xc0) != 0x80) return false;
            codepoint = (codepoint << 6) | (next & 0x3f);
        }
        if (codepoint < minimum || codepoint > 0x10ffff ||
            (codepoint >= 0xd800 && codepoint <= 0xdfff)) return false;
    }
    return true;
}

/* 返回不超过 limit 且落在完整 UTF-8 字符边界上的最长前缀。 */
size_t cat_protocol_utf8_prefix(const uint8_t *text, size_t length, size_t limit)
{
    if (text == NULL) return 0;
    size_t end = length < limit ? length : limit;
    if (end == length) return cat_protocol_utf8_valid(text, end) ? end : 0;
    while (end > 0 && (text[end] & 0xc0) == 0x80) --end;
    if (cat_protocol_utf8_valid(text, end)) return end;
    while (end > 0) {
        --end;
        if ((text[end] & 0xc0) != 0x80 && cat_protocol_utf8_valid(text, end)) return end;
    }
    return 0;
}
