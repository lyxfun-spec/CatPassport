#include "cat_ui.h"

#include <stdio.h>
#include <string.h>

#include "lvgl.h"
#include "cat_protocol.h"
#include "cat_sprite_assets.h"

LV_FONT_DECLARE(cat_font_16);

#define COLOR_BG 0x111417
#define COLOR_INK 0xF3F0E8
#define COLOR_CARD 0x1B2024
#define COLOR_BORDER 0x293238
#define COLOR_ACCENT 0x59C9B2
#define COLOR_MUTED 0x87918F
#define TEXT_BODY_Y 42
/*
 * 可视正文高度必须是行高的整数倍：268 时内容区只有 238px，8 行余 8px，卡片底边永远
 * 会切掉一行的尾巴。262 - 上下边框 2 - 上下 padding 20 = 240 = 正好 8 行。
 */
#define TEXT_BODY_HEIGHT 262
#define CARD_PAD 10
#define CARD_BORDER 1

static lv_obj_t *s_time;
static lv_obj_t *s_status;
static lv_obj_t *s_hint;
static lv_obj_t *s_text;
static lv_obj_t *s_card;
static lv_obj_t *s_battery;
static lv_obj_t *s_cat_image;
static cat_app_state_t s_cat_state = CAT_STATE_DISCONNECTED;
static cat_app_state_t s_layout_state = CAT_STATE_DISCONNECTED;
static uint8_t s_animation_tick;

/* 上一次真正写进 label 的正文指纹：长度、内容散列和是否非空。 */
static uint32_t s_applied_hash;
static size_t s_applied_length;
static uint8_t s_applied_branch;

/* 预录阶段保持原页面，超过 300 ms 后才切换到“正在听”。 */
static cat_app_state_t display_state(const cat_app_model_t *model)
{
    if (model->state == CAT_STATE_RECORDING && !model->recording_active)
        return model->recording_origin_state;
    return model->state;
}

static cat_sprite_pose_t cat_pose(cat_app_state_t state, uint8_t tick)
{
    switch (state) {
    case CAT_STATE_RECORDING:
        return CAT_SPRITE_LISTEN;
    case CAT_STATE_TRANSCRIBING:
    case CAT_STATE_AI_WAITING:
        return CAT_SPRITE_THINK;
    case CAT_STATE_AI_STREAMING:
    case CAT_STATE_ANSWER_READY:
        return CAT_SPRITE_HAPPY;
    case CAT_STATE_ERROR:
        return CAT_SPRITE_ERROR;
    case CAT_STATE_DISCONNECTED:
    case CAT_STATE_IDLE:
    case CAT_STATE_TRANSCRIPT_REVIEW:
    default:
        return tick == 11 ? CAT_SPRITE_BLINK : CAT_SPRITE_IDLE;
    }
}

static void update_cat(void)
{
    lv_image_set_src(s_cat_image, cat_sprite_get(cat_pose(s_cat_state, s_animation_tick)));
    const bool bob = s_cat_state == CAT_STATE_RECORDING ||
                     s_cat_state == CAT_STATE_TRANSCRIBING ||
                     s_cat_state == CAT_STATE_AI_WAITING;
    lv_obj_align(s_cat_image, LV_ALIGN_TOP_MID, 0,
                 42 + (bob && ((s_animation_tick / 2) & 1U) ? 1 : 0));
}

static void cat_animation_cb(lv_timer_t *timer)
{
    (void)timer;
    s_animation_tick = (uint8_t)((s_animation_tick + 1U) % 12U);
    update_cat();
}

static void build_cat(lv_obj_t *parent)
{
    s_cat_image = lv_image_create(parent);
    lv_image_set_src(s_cat_image, cat_sprite_get(CAT_SPRITE_IDLE));
    lv_obj_align(s_cat_image, LV_ALIGN_TOP_MID, 0, 42);
    lv_timer_create(cat_animation_cb, 250, NULL);
}

void cat_ui_init(void)
{
    lv_obj_t *screen = lv_obj_create(NULL);
    lv_obj_remove_style_all(screen);
    lv_obj_set_style_bg_color(screen, lv_color_hex(COLOR_BG), 0);
    lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);

    s_time = lv_label_create(screen);
    lv_obj_set_style_text_font(s_time, &cat_font_16, 0);
    lv_obj_set_style_text_color(s_time, lv_color_hex(COLOR_INK), 0);
    lv_label_set_text(s_time, "--:--");
    lv_obj_align(s_time, LV_ALIGN_TOP_LEFT, 14, 8);

    s_battery = lv_label_create(screen);
    lv_obj_set_style_text_font(s_battery, &cat_font_16, 0);
    lv_obj_set_style_text_color(s_battery, lv_color_hex(COLOR_MUTED), 0);
    lv_obj_align(s_battery, LV_ALIGN_TOP_RIGHT, -14, 8);

    lv_obj_t *divider = lv_obj_create(screen);
    lv_obj_remove_style_all(divider);
    lv_obj_set_pos(divider, 10, 33);
    lv_obj_set_size(divider, 220, 1);
    lv_obj_set_style_bg_color(divider, lv_color_hex(COLOR_BORDER), 0);
    lv_obj_set_style_bg_opa(divider, LV_OPA_COVER, 0);

    build_cat(screen);

    s_status = lv_label_create(screen);
    lv_obj_set_width(s_status, 210);
    lv_obj_set_style_text_font(s_status, &cat_font_16, 0);
    lv_obj_set_style_text_align(s_status, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_text_color(s_status, lv_color_hex(COLOR_ACCENT), 0);
    lv_obj_align(s_status, LV_ALIGN_TOP_MID, 0, 128);

    s_card = lv_obj_create(screen);
    lv_obj_set_pos(s_card, 10, 62);
    lv_obj_set_size(s_card, 220, 220);
    lv_obj_set_style_bg_color(s_card, lv_color_hex(COLOR_CARD), 0);
    lv_obj_set_style_border_color(s_card, lv_color_hex(COLOR_BORDER), 0);
    lv_obj_set_style_border_width(s_card, 1, 0);
    lv_obj_set_style_radius(s_card, 10, 0);
    lv_obj_set_style_pad_all(s_card, CARD_PAD, 0);
    lv_obj_set_scroll_dir(s_card, LV_DIR_VER);
    lv_obj_set_scrollbar_mode(s_card, LV_SCROLLBAR_MODE_AUTO);
    lv_obj_set_style_bg_color(s_card, lv_color_hex(COLOR_ACCENT), LV_PART_SCROLLBAR);
    lv_obj_set_style_bg_opa(s_card, LV_OPA_70, LV_PART_SCROLLBAR);
    lv_obj_set_style_width(s_card, 3, LV_PART_SCROLLBAR);

    s_text = lv_label_create(s_card);
    lv_obj_set_width(s_text, 194);
    lv_obj_set_style_text_font(s_text, &cat_font_16, 0);
    lv_obj_set_style_text_color(s_text, lv_color_hex(COLOR_INK), 0);
    lv_label_set_long_mode(s_text, LV_LABEL_LONG_WRAP);
    lv_label_set_recolor(s_text, false);
    lv_label_set_text(s_text, "");

    s_hint = lv_label_create(screen);
    lv_obj_set_width(s_hint, 216);
    lv_obj_set_style_text_font(s_hint, &cat_font_16, 0);
    lv_obj_set_style_text_align(s_hint, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_text_color(s_hint, lv_color_hex(COLOR_MUTED), 0);
    lv_obj_align(s_hint, LV_ALIGN_BOTTOM_MID, 0, -7);
    lv_screen_load(screen);
}

static void set_cat_state(cat_app_state_t state)
{
    if (state != s_cat_state) s_animation_tick = 0;
    s_cat_state = state;
    update_cat();
}

static bool full_text_state(cat_app_state_t state)
{
    return state == CAT_STATE_TRANSCRIPT_REVIEW || state == CAT_STATE_AI_STREAMING ||
           state == CAT_STATE_ANSWER_READY;
}

static bool answer_text_state(cat_app_state_t state)
{
    return state == CAT_STATE_AI_STREAMING || state == CAT_STATE_ANSWER_READY;
}

static void set_layout(cat_app_state_t state)
{
    const bool full_text = full_text_state(state);
    const bool show_error = state == CAT_STATE_ERROR;
    if (full_text) {
        lv_obj_add_flag(s_cat_image, LV_OBJ_FLAG_HIDDEN);
        lv_obj_remove_flag(s_card, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_width(s_status, 100);
        lv_obj_align(s_status, LV_ALIGN_TOP_MID, 0, 8);
        lv_obj_set_pos(s_card, 10, TEXT_BODY_Y);
        lv_obj_set_size(s_card, 220, TEXT_BODY_HEIGHT);
        lv_obj_add_flag(s_hint, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_obj_remove_flag(s_cat_image, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_width(s_status, 210);
        lv_obj_align(s_status, LV_ALIGN_TOP_MID, 0, 128);
        lv_obj_remove_flag(s_hint, LV_OBJ_FLAG_HIDDEN);
        if (show_error) {
            lv_obj_remove_flag(s_card, LV_OBJ_FLAG_HIDDEN);
            lv_obj_set_pos(s_card, 12, 158);
            lv_obj_set_size(s_card, 216, 104);
        } else {
            lv_obj_add_flag(s_card, LV_OBJ_FLAG_HIDDEN);
        }
    }
    if (state != s_layout_state) {
        const bool preserve_answer_position =
            answer_text_state(state) && answer_text_state(s_layout_state);
        /* 当前页由调用方按面板实际偏移反推，这里只负责复位，不另存状态。 */
        if (!preserve_answer_position) lv_obj_scroll_to_y(s_card, 0, LV_ANIM_OFF);
    }
    s_layout_state = state;
}

/*
 * 阅读布局把可视正文区补到整行的整数倍：多余的零头收进底部 padding，于是任何一页的
 * 底边都落在行与行之间，不会露出被横切的半行。卡片尺寸以后怎么改都成立。
 * 其余布局（例如错误卡片）不分页，恢复基础 padding，免得白白压掉一行正文。
 */
static void snap_answer_viewport(bool reading_layout)
{
    if (s_card == NULL || s_text == NULL) return;
    const lv_font_t *font = lv_obj_get_style_text_font(s_text, LV_PART_MAIN);
    const int32_t line = font == NULL ? 0 : lv_font_get_line_height(font);
    if (line <= 0) return;
    if (!reading_layout) {
        lv_obj_set_style_pad_bottom(s_card, CARD_PAD, 0);
        return;
    }
    const int32_t usable = lv_obj_get_height(s_card) - 2 * CARD_BORDER -
                           lv_obj_get_style_pad_top(s_card, 0) - CARD_PAD;
    lv_obj_set_style_pad_bottom(s_card, CARD_PAD + usable % line, 0);
}

void cat_ui_answer_view(cat_ui_answer_view_t *view)
{
    if (view == NULL) return;
    *view = (cat_ui_answer_view_t){0};
    if (s_card == NULL || s_text == NULL) return;
    lv_obj_update_layout(s_card);
    const lv_font_t *font = lv_obj_get_style_text_font(s_text, LV_PART_MAIN);
    view->line_height = font == NULL ? 0 : (int)lv_font_get_line_height(font);
    view->viewport_height = (int)lv_obj_get_content_height(s_card);
    view->content_height = (int)lv_obj_get_height(s_text);
    view->scroll_y = (int)lv_obj_get_scroll_y(s_card);
}

void cat_ui_show_y(int32_t y)
{
    if (s_card == NULL || lv_obj_has_flag(s_card, LV_OBJ_FLAG_HIDDEN)) return;
    if (y == lv_obj_get_scroll_y(s_card)) return;
    lv_obj_scroll_to_y(s_card, y, LV_ANIM_OFF);
    /* 一次按键一帧：立刻推屏，不等 LVGL 的下一个刷新 tick。偏移没变则完全不重绘。 */
    lv_obj_invalidate(s_card);
    lv_refr_now(lv_obj_get_display(s_card));
}

/*
 * 正文指纹没变时跳过 label 重排。电量、时钟、通知都会触发整段刷新，而一次 12KB 正文的
 * 重排是几十毫秒量级，足以挤掉一次翻页帧。
 */
static void apply_answer_text(const char *text, size_t text_length)
{
    const uint8_t branch = (text == NULL || text_length == 0) ? 0 : 1;
    uint32_t hash = 2166136261U;
    if (text != NULL) {
        for (size_t i = 0; i < text_length; ++i) hash = (hash ^ (uint8_t)text[i]) * 16777619U;
    }
    if (branch != 0 && branch == s_applied_branch && text_length == s_applied_length &&
        hash == s_applied_hash)
        return;
    s_applied_hash = hash;
    s_applied_length = text_length;
    s_applied_branch = branch;
    lv_label_set_text(s_text, branch == 1 ? text : "");
}

void cat_ui_render(const cat_app_model_t *model, const char *text, size_t text_length,
                   int battery_percent, int minute_of_day)
{
    if (model == NULL) return;
    static const char *STATUS[] = {
        "等待手机连接", "随时可以说话", "正在听", "正在识别",
        "识别结果", "正在思考", "AI 回复", "AI 回复", "遇到一点问题",
        "新对话已开启"
    };
    static const char *HINT[] = {
        "请在手机打开小猫助手", "按住 OK 说话", "松开发送", "长按下键取消",
        "", "长按下键取消", "", "",
        "OK返回  长按上键回首页", "可以开始新的聊天了"
    };
    const cat_app_state_t shown_state = display_state(model);
    const unsigned index = (unsigned)shown_state < sizeof(STATUS) / sizeof(STATUS[0])
                               ? (unsigned)shown_state : 0;
    lv_label_set_text(s_status, STATUS[index]);
    lv_label_set_text(s_hint, HINT[index]);
    set_cat_state(shown_state);
    set_layout(shown_state);

    char clock_text[8];
    if (minute_of_day >= 0 && minute_of_day < 24 * 60) {
        snprintf(clock_text, sizeof(clock_text), "%02d:%02d",
                 minute_of_day / 60, minute_of_day % 60);
    } else {
        strcpy(clock_text, "--:--");
    }
    lv_label_set_text(s_time, clock_text);

    char battery[12];
    if (battery_percent >= 0) snprintf(battery, sizeof(battery), "%d%%", battery_percent);
    else strcpy(battery, "--%");
    lv_label_set_text(s_battery, battery);

    apply_answer_text(text, text_length);
    snap_answer_viewport(full_text_state(shown_state));
}
