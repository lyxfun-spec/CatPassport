#include "cat_markdown.h"

#include <ctype.h>
#include <string.h>

#define COLOR_ACCENT "59C9B2"
#define COLOR_MUTED "87918F"
#define COLOR_CODE "9CCFD8"

typedef struct {
    char *data;
    size_t capacity;
    size_t length;
    size_t reserved_closers;
    unsigned styles_left;
    bool formatted;
    bool truncated;
} writer_t;

static bool room_for(const writer_t *writer, size_t bytes)
{
    return writer->capacity != 0 &&
           writer->length + bytes + writer->reserved_closers < writer->capacity;
}

static void write_bytes(writer_t *writer, const char *bytes, size_t length)
{
    if (length == 0 || writer->truncated) return;
    if (!room_for(writer, length)) {
        writer->truncated = true;
        return;
    }
    memcpy(writer->data + writer->length, bytes, length);
    writer->length += length;
}

static void write_display(writer_t *writer, const char *text, size_t length)
{
    for (size_t i = 0; i < length && !writer->truncated;) {
        const unsigned char c = (unsigned char)text[i];
        if (c == '\r') {
            ++i;
            continue;
        }
        if (c == '\t') {
            write_bytes(writer, "    ", 4);
            ++i;
            continue;
        }
        if (c == '#') {
            /* A literal '#' would be consumed as an LVGL recolor command. */
            write_bytes(writer, "\xef\xbc\x83", 3); /* U+FF03 FULLWIDTH NUMBER SIGN */
            ++i;
            continue;
        }
        size_t width = 1;
        if ((c & 0xE0U) == 0xC0U) width = 2;
        else if ((c & 0xF0U) == 0xE0U) width = 3;
        else if ((c & 0xF8U) == 0xF0U) width = 4;
        if (width > length - i) width = length - i;
        write_bytes(writer, text + i, width);
        i += width;
    }
}

static bool style_begin(writer_t *writer, const char color[7])
{
    if (writer->styles_left == 0 || writer->truncated || !room_for(writer, 9))
        return false;
    write_bytes(writer, "#", 1);
    write_bytes(writer, color, 6);
    write_bytes(writer, " ", 1);
    if (writer->truncated) return false;
    --writer->styles_left;
    ++writer->reserved_closers;
    writer->formatted = true;
    return true;
}

static void style_end(writer_t *writer, bool active)
{
    if (!active) return;
    if (writer->reserved_closers != 0) --writer->reserved_closers;
    if (writer->capacity != 0 && writer->length + 1 < writer->capacity) {
        writer->data[writer->length++] = '#';
    } else {
        writer->truncated = true;
    }
}

static const char *find_bytes(const char *text, size_t length,
                              const char *needle, size_t needle_length)
{
    if (needle_length == 0 || length < needle_length) return NULL;
    for (size_t i = 0; i <= length - needle_length; ++i) {
        if (memcmp(text + i, needle, needle_length) == 0) return text + i;
    }
    return NULL;
}

static void write_styled(writer_t *writer, const char *color,
                         const char *text, size_t length)
{
    const bool styled = style_begin(writer, color);
    write_display(writer, text, length);
    style_end(writer, styled);
}

static void write_inline(writer_t *writer, const char *text, size_t length)
{
    size_t i = 0;
    while (i < length && !writer->truncated) {
        if (text[i] == '\\' && i + 1 < length && strchr("\\`*_[]()~#", text[i + 1])) {
            write_display(writer, text + i + 1, 1);
            i += 2;
            writer->formatted = true;
            continue;
        }

        if (text[i] == '`') {
            const char *end = find_bytes(text + i + 1, length - i - 1, "`", 1);
            if (end != NULL) {
                write_styled(writer, COLOR_CODE, text + i + 1,
                             (size_t)(end - (text + i + 1)));
                i = (size_t)(end - text) + 1;
                continue;
            }
        }

        const bool strong = i + 1 < length &&
                            ((text[i] == '*' && text[i + 1] == '*') ||
                             (text[i] == '_' && text[i + 1] == '_'));
        if (strong) {
            char delimiter[3] = {text[i], text[i], '\0'};
            const char *end = find_bytes(text + i + 2, length - i - 2, delimiter, 2);
            if (end != NULL && end != text + i + 2) {
                write_styled(writer, COLOR_ACCENT, text + i + 2,
                             (size_t)(end - (text + i + 2)));
                i = (size_t)(end - text) + 2;
                continue;
            }
        }

        if (i + 1 < length && text[i] == '~' && text[i + 1] == '~') {
            const char *end = find_bytes(text + i + 2, length - i - 2, "~~", 2);
            if (end != NULL && end != text + i + 2) {
                write_styled(writer, COLOR_MUTED, text + i + 2,
                             (size_t)(end - (text + i + 2)));
                i = (size_t)(end - text) + 2;
                continue;
            }
        }

        if ((text[i] == '*' || text[i] == '_') && i + 1 < length) {
            const char delimiter[2] = {text[i], '\0'};
            const char *end = find_bytes(text + i + 1, length - i - 1, delimiter, 1);
            if (end != NULL && end != text + i + 1) {
                write_styled(writer, COLOR_MUTED, text + i + 1,
                             (size_t)(end - (text + i + 1)));
                i = (size_t)(end - text) + 1;
                continue;
            }
        }

        if (text[i] == '[') {
            const char *label_end = find_bytes(text + i + 1, length - i - 1, "](", 2);
            if (label_end != NULL) {
                const size_t url_offset = (size_t)(label_end - text) + 2;
                const char *url_end = find_bytes(text + url_offset, length - url_offset, ")", 1);
                if (url_end != NULL && label_end != text + i + 1) {
                    write_styled(writer, COLOR_ACCENT, text + i + 1,
                                 (size_t)(label_end - (text + i + 1)));
                    i = (size_t)(url_end - text) + 1;
                    continue;
                }
            }
        }

        size_t next = i + 1;
        while (next < length && strchr("\\`*_~[", text[next]) == NULL) ++next;
        write_display(writer, text + i, next - i);
        i = next;
    }
}

static size_t leading_spaces(const char *line, size_t length)
{
    size_t count = 0;
    while (count < length && count < 3 && line[count] == ' ') ++count;
    return count;
}

static bool fence_line(const char *line, size_t length, char *marker, size_t *content_offset)
{
    const size_t indent = leading_spaces(line, length);
    if (length - indent < 3 || (line[indent] != '`' && line[indent] != '~')) return false;
    const char c = line[indent];
    size_t count = 0;
    while (indent + count < length && line[indent + count] == c) ++count;
    if (count < 3) return false;
    *marker = c;
    *content_offset = indent + count;
    return true;
}

static bool horizontal_rule(const char *line, size_t length)
{
    const size_t indent = leading_spaces(line, length);
    if (indent == length || (line[indent] != '-' && line[indent] != '*' && line[indent] != '_'))
        return false;
    const char marker = line[indent];
    unsigned count = 0;
    for (size_t i = indent; i < length; ++i) {
        if (line[i] == marker) ++count;
        else if (line[i] != ' ' && line[i] != '\t') return false;
    }
    return count >= 3;
}

static void write_line(writer_t *writer, const char *line, size_t length,
                       bool *in_fence, char *fence_marker)
{
    char marker = 0;
    size_t offset = 0;
    if (fence_line(line, length, &marker, &offset)) {
        if (!*in_fence) {
            *in_fence = true;
            *fence_marker = marker;
            writer->formatted = true;
            while (offset < length && line[offset] == ' ') ++offset;
            const bool styled = style_begin(writer, COLOR_MUTED);
            write_bytes(writer, "[code", 5);
            if (offset < length) {
                write_bytes(writer, ": ", 2);
                write_display(writer, line + offset, length - offset);
            }
            write_bytes(writer, "]", 1);
            style_end(writer, styled);
        } else if (marker == *fence_marker) {
            *in_fence = false;
            writer->formatted = true;
        } else {
            write_styled(writer, COLOR_CODE, line, length);
        }
        return;
    }

    if (*in_fence) {
        const bool styled = style_begin(writer, COLOR_CODE);
        write_bytes(writer, "  ", 2);
        write_display(writer, line, length);
        style_end(writer, styled);
        return;
    }

    const size_t indent = leading_spaces(line, length);
    size_t heading = 0;
    while (indent + heading < length && heading < 3 && line[indent + heading] == '#')
        ++heading;
    if (heading != 0 && indent + heading < length && line[indent + heading] == ' ') {
        size_t content = indent + heading + 1;
        while (content < length && line[content] == ' ') ++content;
        write_styled(writer, COLOR_ACCENT, line + content, length - content);
        writer->formatted = true;
        return;
    }

    if (horizontal_rule(line, length)) {
        write_styled(writer, COLOR_MUTED, "----------------", 16);
        writer->formatted = true;
        return;
    }

    if (indent < length && line[indent] == '>') {
        size_t content = indent + 1;
        if (content < length && line[content] == ' ') ++content;
        write_styled(writer, COLOR_ACCENT, "| ", 2);
        write_inline(writer, line + content, length - content);
        writer->formatted = true;
        return;
    }

    if (indent + 1 < length && strchr("-*+", line[indent]) != NULL &&
        line[indent + 1] == ' ') {
        write_display(writer, line, indent);
        write_styled(writer, COLOR_ACCENT, "- ", 2);
        write_inline(writer, line + indent + 2, length - indent - 2);
        writer->formatted = true;
        return;
    }

    size_t digits = indent;
    while (digits < length && isdigit((unsigned char)line[digits])) ++digits;
    if (digits > indent && digits + 1 < length && line[digits] == '.' && line[digits + 1] == ' ') {
        write_display(writer, line, indent);
        write_styled(writer, COLOR_ACCENT, line + indent, digits + 2 - indent);
        write_inline(writer, line + digits + 2, length - digits - 2);
        writer->formatted = true;
        return;
    }

    write_inline(writer, line, length);
}

cat_markdown_result_t cat_markdown_format(const char *input, size_t input_length,
                                          char *output, size_t output_capacity)
{
    cat_markdown_result_t result = {0};
    if (output == NULL || output_capacity == 0) {
        result.truncated = input_length != 0;
        return result;
    }
    output[0] = '\0';
    if (input == NULL || input_length == 0) return result;

    writer_t writer = {
        .data = output,
        .capacity = output_capacity,
        .styles_left = CAT_MARKDOWN_STYLE_LIMIT,
    };
    bool in_fence = false;
    char fence_marker = 0;
    size_t line_start = 0;
    while (line_start < input_length && !writer.truncated) {
        size_t line_end = line_start;
        while (line_end < input_length && input[line_end] != '\n') ++line_end;
        size_t line_length = line_end - line_start;
        if (line_length != 0 && input[line_start + line_length - 1] == '\r') --line_length;
        write_line(&writer, input + line_start, line_length, &in_fence, &fence_marker);
        if (line_end < input_length) write_bytes(&writer, "\n", 1);
        line_start = line_end + 1;
    }

    if (!writer.formatted) {
        const size_t copy = input_length < output_capacity - 1 ? input_length : output_capacity - 1;
        memcpy(output, input, copy);
        output[copy] = '\0';
        result.length = copy;
        result.truncated = copy != input_length;
        return result;
    }

    output[writer.length] = '\0';
    result.length = writer.length;
    result.formatted = true;
    result.truncated = writer.truncated;
    return result;
}
