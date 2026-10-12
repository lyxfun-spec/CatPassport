#include "cat_screen.h"

#include <stddef.h>

bool cat_screen_timeout_valid(uint32_t seconds)
{
    return seconds <= CAT_SCREEN_TIMEOUT_MAX_SECONDS;
}

void cat_screen_init(cat_screen_t *screen, uint32_t timeout_seconds, uint64_t now_ms)
{
    if (screen == NULL) return;
    screen->timeout_seconds = cat_screen_timeout_valid(timeout_seconds)
                                  ? timeout_seconds : CAT_SCREEN_TIMEOUT_DEFAULT_SECONDS;
    screen->last_activity_ms = now_ms;
    screen->off = false;
}

bool cat_screen_set_timeout(cat_screen_t *screen, uint32_t timeout_seconds, uint64_t now_ms)
{
    if (screen == NULL || !cat_screen_timeout_valid(timeout_seconds)) return false;
    screen->timeout_seconds = timeout_seconds;
    screen->last_activity_ms = now_ms;
    screen->off = false;
    return true;
}

void cat_screen_note_activity(cat_screen_t *screen, uint64_t now_ms)
{
    if (screen == NULL) return;
    screen->last_activity_ms = now_ms;
    screen->off = false;
}

bool cat_screen_update(cat_screen_t *screen, uint64_t now_ms, bool keep_awake)
{
    if (screen == NULL || screen->off) return false;
    if (keep_awake) {
        screen->last_activity_ms = now_ms;
        return false;
    }
    if (screen->timeout_seconds == 0) return false;
    const uint64_t timeout_ms = (uint64_t)screen->timeout_seconds * 1000ULL;
    if (now_ms >= screen->last_activity_ms && now_ms - screen->last_activity_ms >= timeout_ms) {
        screen->off = true;
        return true;
    }
    return false;
}

bool cat_screen_is_off(const cat_screen_t *screen)
{
    return screen != NULL && screen->off;
}
