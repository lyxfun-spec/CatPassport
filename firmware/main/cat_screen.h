#pragma once

#include <stdbool.h>
#include <stdint.h>

#define CAT_SCREEN_TIMEOUT_DEFAULT_SECONDS 60U
#define CAT_SCREEN_TIMEOUT_MAX_SECONDS 86400U

typedef struct {
    uint32_t timeout_seconds;
    uint64_t last_activity_ms;
    bool off;
} cat_screen_t;

bool cat_screen_timeout_valid(uint32_t seconds);
void cat_screen_init(cat_screen_t *screen, uint32_t timeout_seconds, uint64_t now_ms);
bool cat_screen_set_timeout(cat_screen_t *screen, uint32_t timeout_seconds, uint64_t now_ms);
void cat_screen_note_activity(cat_screen_t *screen, uint64_t now_ms);
bool cat_screen_update(cat_screen_t *screen, uint64_t now_ms, bool keep_awake);
bool cat_screen_is_off(const cat_screen_t *screen);
