#pragma once

#include <stddef.h>
#include <stdint.h>
#include "cat_state.h"

/*
 * 正文分页所需的面板度量，单位像素。两个函数都要求调用方持有 LVGL 锁：前者读几何，
 * 后者把视口移到某一页并立即推屏。
 */
typedef struct {
    int line_height;      /* 正文行高；0 表示还度量不出来 */
    int viewport_height;  /* 可视正文高，已对齐到整行 */
    int content_height;   /* 正文总高 */
    int scroll_y;         /* 面板当前偏移 */
} cat_ui_answer_view_t;

void cat_ui_init(void);
void cat_ui_render(const cat_app_model_t *model, const char *text, size_t text_length,
                   int battery_percent, int minute_of_day);
void cat_ui_answer_view(cat_ui_answer_view_t *view);
void cat_ui_show_y(int32_t y);
