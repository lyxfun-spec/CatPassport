#include "cat_clock.h"

#include <stddef.h>

#define SECONDS_PER_DAY 86400U

void cat_clock_init(cat_clock_t *clock)
{
    if (clock != NULL) *clock = (cat_clock_t){0};
}

bool cat_clock_sync(cat_clock_t *clock, uint32_t local_seconds, uint64_t now_ms)
{
    if (clock == NULL || local_seconds >= SECONDS_PER_DAY) return false;
    clock->local_seconds = local_seconds;
    clock->synced_at_ms = now_ms;
    clock->synced = true;
    return true;
}

int cat_clock_minute_of_day(const cat_clock_t *clock, uint64_t now_ms)
{
    if (clock == NULL || !clock->synced) return -1;
    const uint64_t elapsed_seconds = now_ms >= clock->synced_at_ms
                                         ? (now_ms - clock->synced_at_ms) / 1000U : 0;
    const uint32_t local = (uint32_t)((clock->local_seconds + elapsed_seconds) % SECONDS_PER_DAY);
    return (int)(local / 60U);
}
