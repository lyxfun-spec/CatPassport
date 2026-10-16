#include "cat_scroll.h"

#include <stddef.h>

/*
 * 初速度让轻点约滚动两行；持续按住约一秒滚动一屏。全部使用定点整数，避免
 * ESP32-C3 为简单动画引入浮点开销。最终参数仍需结合实体按键手感真机微调。
 */
#define SCROLL_INITIAL_VELOCITY 140000
#define SCROLL_ACCELERATION 320000
#define SCROLL_MAX_VELOCITY 280000
#define SCROLL_DECELERATION 700000
#define SCROLL_MAX_TICK_MS 50U

static int32_t approach_zero(int32_t value, int32_t amount)
{
    if (value > 0) return value > amount ? value - amount : 0;
    if (value < 0) return value < -amount ? value + amount : 0;
    return 0;
}

void cat_scroll_init(cat_scroll_motion_t *motion)
{
    if (motion == NULL) return;
    *motion = (cat_scroll_motion_t){0};
}

void cat_scroll_press(cat_scroll_motion_t *motion, int direction)
{
    if (motion == NULL || direction == 0) return;
    const int8_t normalized = direction > 0 ? 1 : -1;
    motion->held_direction = normalized;

    /* 反向按下时立即改变方向，不让旧惯性造成按键已经反向、画面仍继续滑动。 */
    if ((normalized > 0 && motion->velocity_milli_px_s < SCROLL_INITIAL_VELOCITY) ||
        (normalized < 0 && motion->velocity_milli_px_s > -SCROLL_INITIAL_VELOCITY)) {
        motion->velocity_milli_px_s = normalized * SCROLL_INITIAL_VELOCITY;
        motion->remainder_milli_px = 0;
    }
}

void cat_scroll_release(cat_scroll_motion_t *motion, int direction)
{
    if (motion == NULL || direction == 0) return;
    const int8_t normalized = direction > 0 ? 1 : -1;
    if (motion->held_direction == normalized) motion->held_direction = 0;
}

int32_t cat_scroll_update(cat_scroll_motion_t *motion, uint32_t elapsed_ms)
{
    if (motion == NULL || elapsed_ms == 0) return 0;
    if (elapsed_ms > SCROLL_MAX_TICK_MS) elapsed_ms = SCROLL_MAX_TICK_MS;

    if (motion->held_direction != 0) {
        const int32_t acceleration =
            (int32_t)(((int64_t)SCROLL_ACCELERATION * elapsed_ms) / 1000);
        motion->velocity_milli_px_s += motion->held_direction * acceleration;
        if (motion->velocity_milli_px_s > SCROLL_MAX_VELOCITY)
            motion->velocity_milli_px_s = SCROLL_MAX_VELOCITY;
        if (motion->velocity_milli_px_s < -SCROLL_MAX_VELOCITY)
            motion->velocity_milli_px_s = -SCROLL_MAX_VELOCITY;
    } else {
        const int32_t deceleration =
            (int32_t)(((int64_t)SCROLL_DECELERATION * elapsed_ms) / 1000);
        motion->velocity_milli_px_s =
            approach_zero(motion->velocity_milli_px_s, deceleration);
    }

    motion->remainder_milli_px +=
        (int32_t)(((int64_t)motion->velocity_milli_px_s * elapsed_ms) / 1000);
    const int32_t pixels = motion->remainder_milli_px / 1000;
    motion->remainder_milli_px -= pixels * 1000;
    return pixels;
}

void cat_scroll_stop(cat_scroll_motion_t *motion)
{
    if (motion == NULL) return;
    motion->velocity_milli_px_s = 0;
    motion->remainder_milli_px = 0;
    motion->held_direction = 0;
}

bool cat_scroll_is_active(const cat_scroll_motion_t *motion)
{
    return motion != NULL &&
           (motion->held_direction != 0 || motion->velocity_milli_px_s != 0);
}
