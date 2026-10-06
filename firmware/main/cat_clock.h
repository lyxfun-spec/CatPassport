#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    uint32_t local_seconds;
    uint64_t synced_at_ms;
    bool synced;
} cat_clock_t;

void cat_clock_init(cat_clock_t *clock);
bool cat_clock_sync(cat_clock_t *clock, uint32_t local_seconds, uint64_t now_ms);
int cat_clock_minute_of_day(const cat_clock_t *clock, uint64_t now_ms);
