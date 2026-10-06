#pragma once

#include <stddef.h>
#include "cat_state.h"

void cat_ui_init(void);
void cat_ui_render(const cat_app_model_t *model, const char *text, size_t text_length,
                   int battery_percent, int minute_of_day);
void cat_ui_scroll_text(int direction);
