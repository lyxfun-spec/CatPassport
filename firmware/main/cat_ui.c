#include "cat_ui.h"

#include <stdio.h>
#include <string.h>

#include "lvgl.h"

LV_FONT_DECLARE(cat_font_16);

#define COLOR_BG 0xFFF7E8
#define COLOR_INK 0x332D2A
#define COLOR_CARD 0xFFFFFF
#define COLOR_PINK 0xFF9EB5
#define COLOR_ORANGE 0xFFB35C
#define COLOR_GREEN 0x5FCB9A
#define COLOR_MUTED 0x8D817B

static lv_obj_t *s_status;
static lv_obj_t *s_hint;
static lv_obj_t *s_text;
static lv_obj_t *s_battery;
static lv_obj_t *s_cat_face;
static lv_obj_t *s_left_eye;
static lv_obj_t *s_right_eye;
static lv_obj_t *s_mouth;

static lv_obj_t *block(lv_obj_t *parent, int x, int y, int width, int height, uint32_t color)
{
    lv_obj_t *object = lv_obj_create(parent);
    lv_obj_remove_style_all(object);
    lv_obj_set_pos(object, x, y);
    lv_obj_set_size(object, width, height);
    lv_obj_set_style_bg_color(object, lv_color_hex(color), 0);
    lv_obj_set_style_bg_opa(object, LV_OPA_COVER, 0);
    return object;
}

static void build_cat(lv_obj_t *parent)
{
    lv_obj_t *cat = lv_obj_create(parent);
    lv_obj_remove_style_all(cat);
    lv_obj_set_size(cat, 106, 96);
    lv_obj_align(cat, LV_ALIGN_TOP_MID, 0, 35);
    block(cat, 12, 4, 24, 28, COLOR_ORANGE);
    block(cat, 70, 4, 24, 28, COLOR_ORANGE);
    block(cat, 18, 0, 12, 12, COLOR_ORANGE);
    block(cat, 76, 0, 12, 12, COLOR_ORANGE);
    s_cat_face = block(cat, 10, 20, 86, 68, COLOR_ORANGE);
    lv_obj_set_style_radius(s_cat_face, 12, 0);
    block(cat, 2, 44, 12, 28, COLOR_ORANGE);
    block(cat, 92, 44, 12, 28, COLOR_ORANGE);
    block(cat, 43, 27, 20, 8, 0xFFF0D2);
    s_left_eye = block(cat, 28, 48, 8, 10, COLOR_INK);
    s_right_eye = block(cat, 70, 48, 8, 10, COLOR_INK);
    block(cat, 49, 58, 8, 6, COLOR_PINK);
    s_mouth = block(cat, 45, 69, 16, 4, COLOR_INK);
}

void cat_ui_init(void)
{
    lv_obj_t *screen = lv_obj_create(NULL);
    lv_obj_remove_style_all(screen);
    lv_obj_set_style_bg_color(screen, lv_color_hex(COLOR_BG), 0);
    lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);

    lv_obj_t *title = lv_label_create(screen);
    lv_obj_set_style_text_font(title, &cat_font_16, 0);
    lv_obj_set_style_text_color(title, lv_color_hex(COLOR_INK), 0);
    lv_label_set_text(title, "小猫助手");
    lv_obj_align(title, LV_ALIGN_TOP_LEFT, 18, 10);

    s_battery = lv_label_create(screen);
    lv_obj_set_style_text_font(s_battery, &cat_font_16, 0);
    lv_obj_set_style_text_color(s_battery, lv_color_hex(COLOR_MUTED), 0);
    lv_obj_align(s_battery, LV_ALIGN_TOP_RIGHT, -18, 10);

    build_cat(screen);

    s_status = lv_label_create(screen);
    lv_obj_set_width(s_status, 210);
    lv_obj_set_style_text_font(s_status, &cat_font_16, 0);
    lv_obj_set_style_text_align(s_status, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_text_color(s_status, lv_color_hex(COLOR_INK), 0);
    lv_obj_align(s_status, LV_ALIGN_TOP_MID, 0, 132);

    lv_obj_t *card = lv_obj_create(screen);
    lv_obj_set_pos(card, 12, 160);
    lv_obj_set_size(card, 216, 124);
    lv_obj_set_style_bg_color(card, lv_color_hex(COLOR_CARD), 0);
    lv_obj_set_style_border_color(card, lv_color_hex(0xE9DACC), 0);
    lv_obj_set_style_border_width(card, 2, 0);
    lv_obj_set_style_radius(card, 14, 0);
    lv_obj_set_style_pad_all(card, 10, 0);
    lv_obj_set_scroll_dir(card, LV_DIR_VER);
    lv_obj_set_scrollbar_mode(card, LV_SCROLLBAR_MODE_AUTO);

    s_text = lv_label_create(card);
    lv_obj_set_width(s_text, 192);
    lv_obj_set_style_text_font(s_text, &cat_font_16, 0);
    lv_obj_set_style_text_color(s_text, lv_color_hex(COLOR_INK), 0);
    lv_label_set_long_mode(s_text, LV_LABEL_LONG_WRAP);
    lv_label_set_text(s_text, "");

    s_hint = lv_label_create(screen);
    lv_obj_set_width(s_hint, 216);
    lv_obj_set_style_text_font(s_hint, &cat_font_16, 0);
    lv_obj_set_style_text_align(s_hint, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_text_color(s_hint, lv_color_hex(COLOR_MUTED), 0);
    lv_obj_align(s_hint, LV_ALIGN_BOTTOM_MID, 0, -10);
    lv_screen_load(screen);
}

static void set_expression(cat_app_state_t state)
{
    const bool busy = state == CAT_STATE_TRANSCRIBING || state == CAT_STATE_AI_WAITING ||
                      state == CAT_STATE_AI_STREAMING;
    lv_obj_set_height(s_left_eye, busy ? 4 : 10);
    lv_obj_set_height(s_right_eye, busy ? 4 : 10);
    lv_obj_set_style_bg_color(s_cat_face,
        lv_color_hex(state == CAT_STATE_ERROR ? COLOR_PINK : COLOR_ORANGE), 0);
    lv_obj_set_width(s_mouth, state == CAT_STATE_RECORDING ? 8 : 16);
}

void cat_ui_render(const cat_app_model_t *model, const char *text, size_t text_length,
                   int battery_percent)
{
    if (model == NULL) return;
    static const char *STATUS[] = {
        "等待手机连接", "我在听哦", "正在录音", "正在识别……",
        "请确认文字", "小猫正在想……", "小猫正在回答", "回答完成", "遇到了一点问题"
    };
    static const char *HINT[] = {
        "请在手机打开小猫助手", "按住 OK 说话", "松开 OK 发送录音", "可按下键取消",
        "OK 发送  上键重录  下键取消", "下键取消", "下键停止", "按住 OK 继续说话", "OK 返回"
    };
    const unsigned index = (unsigned)model->state < sizeof(STATUS) / sizeof(STATUS[0])
                               ? (unsigned)model->state : 0;
    lv_label_set_text(s_status, STATUS[index]);
    lv_label_set_text(s_hint, HINT[index]);
    set_expression(model->state);

    char battery[12];
    if (battery_percent >= 0) snprintf(battery, sizeof(battery), "%d%%", battery_percent);
    else strcpy(battery, "--%");
    lv_label_set_text(s_battery, battery);

    if (text != NULL && text_length > 0) {
        lv_label_set_text(s_text, text);
    } else if (model->state == CAT_STATE_DISCONNECTED) {
        lv_label_set_text(s_text, "蓝牙连接后，小猫会在这里陪你聊天。");
    } else {
        lv_label_set_text(s_text, "今天想聊点什么？");
    }
}
