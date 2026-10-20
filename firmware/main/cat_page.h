#pragma once

#include <stdint.h>

/*
 * 阅读页的整行分页数学。只算整数像素、不访问 LVGL，可在主机测试里覆盖手感与边界。
 *
 * 这块屏只能用 40 行 DMA 缓冲分块顺序推屏，一次整屏重绘要十几毫秒才能落地，连续
 * 滚动会让面板长时间停在"上一帧只推了一半"的状态。翻页把整屏重绘压到每次按键一
 * 次，并且每一页的偏移都是行高的整数倍，因此任何时刻都不会有字被横切。
 *
 * page_step = (每页行数 - 1) × 行高：相邻两页重叠一行，段落不会被页边界硬切。
 */
typedef struct {
    int32_t line_height;
    int32_t page_step;
    int32_t page_count;
    int32_t page;
    int32_t tail_y;
} cat_page_view_t;

void cat_page_reset(cat_page_view_t *view);

/*
 * 度量变化时重算页数并夹取当前页。viewport_height 必须是行高的整数倍（由 UI 层保证）。
 * 度量尚不可用时按单页处理，偏移恒为 0。
 */
void cat_page_set_geometry(cat_page_view_t *view, int32_t line_height, int32_t content_height,
                           int32_t viewport_height);

/* 用面板实际偏移反推当前页，UI 层复位视口时这里会自动回到第 0 页。 */
void cat_page_set_y(cat_page_view_t *view, int32_t y);

void cat_page_move_by(cat_page_view_t *view, int delta);
void cat_page_last(cat_page_view_t *view);
int32_t cat_page_y(const cat_page_view_t *view);
