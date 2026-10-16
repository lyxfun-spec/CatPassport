#pragma once

#include <stdbool.h>
#include <stdint.h>

/*
 * 阅读页按键滚动物理模型。模块只计算像素位移，不访问 LVGL，调用方可在主机测试
 * 中验证手感参数，并在持有 LVGL 锁后把返回位移应用到实际容器。
 */
typedef struct {
    int32_t velocity_milli_px_s;
    int32_t remainder_milli_px;
    int8_t held_direction;
} cat_scroll_motion_t;

void cat_scroll_init(cat_scroll_motion_t *motion);
void cat_scroll_press(cat_scroll_motion_t *motion, int direction);
void cat_scroll_release(cat_scroll_motion_t *motion, int direction);
int32_t cat_scroll_update(cat_scroll_motion_t *motion, uint32_t elapsed_ms);
void cat_scroll_stop(cat_scroll_motion_t *motion);
bool cat_scroll_is_active(const cat_scroll_motion_t *motion);
