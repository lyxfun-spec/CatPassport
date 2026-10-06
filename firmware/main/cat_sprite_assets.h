#pragma once

#include "lvgl.h"

typedef enum {
    CAT_SPRITE_IDLE,
    CAT_SPRITE_BLINK,
    CAT_SPRITE_LISTEN,
    CAT_SPRITE_THINK,
    CAT_SPRITE_HAPPY,
    CAT_SPRITE_ERROR,
    CAT_SPRITE_COUNT,
} cat_sprite_pose_t;

const lv_image_dsc_t *cat_sprite_get(cat_sprite_pose_t pose);
