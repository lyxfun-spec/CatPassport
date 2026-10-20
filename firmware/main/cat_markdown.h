#pragma once

#include <stdbool.h>
#include <stddef.h>

/*
 * Markdown-lite formatter for the card's single LVGL label.  The formatter
 * emits LVGL recolor commands and never allocates memory.  The extra space is
 * bounded by the number of styles that can be emitted.
 */
#define CAT_MARKDOWN_STYLE_LIMIT 96U
#define CAT_MARKDOWN_RENDER_EXTRA 2048U

typedef struct {
    size_t length;
    bool formatted;
    bool truncated;
} cat_markdown_result_t;

cat_markdown_result_t cat_markdown_format(const char *input, size_t input_length,
                                          char *output, size_t output_capacity);
