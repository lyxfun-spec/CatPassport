#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "bsp_audio.h"
#include "bsp_battery.h"
#include "bsp_button.h"
#include "bsp_display.h"
#include "bsp_i2c.h"
#include "cat_adpcm.h"
#include "cat_ble.h"
#include "cat_clock.h"
#include "cat_protocol.h"
#include "cat_scroll.h"
#include "cat_screen.h"
#include "cat_state.h"
#include "cat_ui.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"

#define APP_QUEUE_DEPTH 10
#define AUDIO_SAMPLES 320U
#define AUDIO_PAYLOAD_BYTES 160U
#define AUDIO_PACKET_BYTES (CAT_AUDIO_HEADER_SIZE + AUDIO_PAYLOAD_BYTES)
#define AUDIO_PREROLL_FRAMES 15U /* 15 x 20 ms = 300 ms，约 2.6 KiB。 */
#define AUDIO_MAX_FRAMES 1500U
#define BLE_SEND_RETRY_COUNT 20U
#define BLE_SEND_RETRY_MS 5U
#define OK_HOLD_THRESHOLD_MS 300ULL
#define INTERACTION_TICK_MS 16U
#define BATTERY_REFRESH_MS 10000ULL
#define HELLO_RETRY_MS 2000ULL
#define NOTICE_DURATION_MS 1500ULL
#define SCREEN_ACTIVE_BRIGHTNESS 80
#define SCREEN_NVS_NAMESPACE "cat_cfg"
#define SCREEN_NVS_KEY "screen_s"

typedef enum {
    INPUT_BUTTON, INPUT_BLE_STATE, INPUT_BLE_FRAGMENT, INPUT_AUDIO_FINISHED,
} input_type_t;

typedef struct {
    input_type_t type;
    union {
        struct { bsp_btn_t button; bsp_btn_ev_t event; } key;
        struct { uint16_t length; uint8_t bytes[244]; } fragment;
        struct {
            uint16_t recording_id;
            uint16_t frames;
            bool activated;
            bool canceled;
            bool failed;
            bool limit;
        } audio;
    } data;
} input_t;

static const char *TAG = "cat_app";
static QueueHandle_t s_queue;
static TaskHandle_t s_audio_task;
static cat_app_model_t s_model;
static cat_protocol_reassembler_t s_reassembler;
static uint8_t s_rx_buffer[CAT_ANSWER_MAX];
static char s_text[CAT_ANSWER_MAX + 1U];
static size_t s_text_length;
static atomic_bool s_recording;
static atomic_bool s_recording_active;
static atomic_bool s_cancel_recording;
static atomic_bool s_ble_ready_pending;
static atomic_bool s_ble_disconnected_pending;
/* 预录缓冲由 audio_task 独占写入；激活前不会通过 BLE 暴露一次短按。 */
static uint8_t s_audio_preroll[AUDIO_PREROLL_FRAMES][AUDIO_PACKET_BYTES];
static uint16_t s_message_id;
static uint64_t s_last_battery_ms;
static uint64_t s_last_hello_ms;
static int s_battery = -1;
static cat_clock_t s_clock;
static cat_screen_t s_screen;
static int s_clock_minute = -1;
static uint64_t s_notice_until_ms;
static bool s_suppress_button_action[3];
static bool s_ok_held;
static uint64_t s_ok_pressed_at_ms;
static uint64_t s_scroll_tick_ms;
static cat_scroll_motion_t s_scroll_motion;

static void apply(cat_app_event_t event);

static void write_le16(uint8_t *data, uint16_t value)
{
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
}

static void write_le32(uint8_t *data, uint32_t value)
{
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
    data[2] = (uint8_t)(value >> 16);
    data[3] = (uint8_t)(value >> 24);
}

static uint32_t read_le32(const uint8_t *data)
{
    return (uint32_t)data[0] | ((uint32_t)data[1] << 8) |
           ((uint32_t)data[2] << 16) | ((uint32_t)data[3] << 24);
}

static uint64_t monotonic_ms(void)
{
    return (uint64_t)esp_timer_get_time() / 1000ULL;
}

static uint32_t load_screen_timeout(void)
{
    uint32_t seconds = CAT_SCREEN_TIMEOUT_DEFAULT_SECONDS;
    nvs_handle_t handle;
    if (nvs_open(SCREEN_NVS_NAMESPACE, NVS_READONLY, &handle) == ESP_OK) {
        uint32_t stored;
        if (nvs_get_u32(handle, SCREEN_NVS_KEY, &stored) == ESP_OK &&
            cat_screen_timeout_valid(stored)) {
            seconds = stored;
        }
        nvs_close(handle);
    }
    return seconds;
}

static void save_screen_timeout(uint32_t seconds)
{
    nvs_handle_t handle;
    esp_err_t result = nvs_open(SCREEN_NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (result == ESP_OK) {
        result = nvs_set_u32(handle, SCREEN_NVS_KEY, seconds);
        if (result == ESP_OK) result = nvs_commit(handle);
        nvs_close(handle);
    }
    if (result != ESP_OK) ESP_LOGW(TAG, "保存熄屏时间失败: %s", esp_err_to_name(result));
}

static void note_screen_activity(void)
{
    const bool was_off = cat_screen_is_off(&s_screen);
    cat_screen_note_activity(&s_screen, monotonic_ms());
    if (was_off) bsp_display_backlight(SCREEN_ACTIVE_BRIGHTNESS);
}

static bool screen_must_stay_awake(void)
{
    return s_model.state == CAT_STATE_RECORDING || s_model.state == CAT_STATE_TRANSCRIBING ||
           s_model.state == CAT_STATE_AI_WAITING || s_model.state == CAT_STATE_AI_STREAMING;
}

static uint16_t next_message_id(void)
{
    if (++s_message_id == 0) ++s_message_id;
    return s_message_id;
}

static void render(void)
{
    if (!bsp_lvgl_lock(200)) return;
    cat_ui_render(&s_model, s_text, s_text_length, s_battery,
                  cat_clock_minute_of_day(&s_clock, monotonic_ms()));
    bsp_lvgl_unlock();
}

static bool scroll_text_pixels(int pixels)
{
    if (!bsp_lvgl_lock(100)) return false;
    const bool moved = cat_ui_scroll_text_pixels(pixels);
    bsp_lvgl_unlock();
    return moved;
}

static void set_text(const uint8_t *text, size_t length)
{
    const size_t safe = cat_protocol_utf8_prefix(text, length, CAT_ANSWER_MAX);
    if (safe == 0 && length != 0) return;
    if (safe != 0) memcpy(s_text, text, safe);
    s_text[safe] = '\0';
    s_text_length = safe;
}

static void append_text(const uint8_t *text, size_t length)
{
    if (!cat_protocol_utf8_valid(text, length)) return;
    const size_t safe = cat_protocol_utf8_prefix(text, length, CAT_ANSWER_MAX - s_text_length);
    if (safe != 0) memcpy(s_text + s_text_length, text, safe);
    s_text_length += safe;
    s_text[s_text_length] = '\0';
}

static bool queue_input(const input_t *input)
{
    if (s_queue != NULL && xQueueSend(s_queue, input, 0) == pdTRUE) return true;
    ESP_LOGW(TAG, "input queue full, rejected type=%d", input->type);
    return false;
}

static void on_button(bsp_btn_t button, bsp_btn_ev_t event, void *context)
{
    (void)context;
    const input_t input = {.type = INPUT_BUTTON,
                           .data.key = {.button = button, .event = event}};
    (void)queue_input(&input);
}

static bool on_ble(const cat_ble_event_t *event, void *context)
{
    (void)context;
    input_t input = {0};
    if (event->type == CAT_BLE_EVENT_READY) {
        atomic_store_explicit(&s_ble_ready_pending, true, memory_order_release);
        input.type = INPUT_BLE_STATE;
        (void)queue_input(&input);
        return true;
    }
    if (event->type == CAT_BLE_EVENT_DISCONNECTED) {
        atomic_store_explicit(&s_ble_ready_pending, false, memory_order_release);
        atomic_store_explicit(&s_ble_disconnected_pending, true, memory_order_release);
        input.type = INPUT_BLE_STATE;
        (void)queue_input(&input);
        return true;
    }
    {
        if (event->length > sizeof(input.data.fragment.bytes)) return false;
        input.type = INPUT_BLE_FRAGMENT;
        input.data.fragment.length = (uint16_t)event->length;
        memcpy(input.data.fragment.bytes, event->data, event->length);
    }
    return queue_input(&input);
}

static void fill_audio_header(uint8_t *packet, uint8_t flags, uint16_t recording_id,
                              uint16_t sequence, int16_t predictor, uint8_t step_index)
{
    write_le16(packet, CAT_AUDIO_MAGIC);
    packet[2] = CAT_PROTOCOL_VERSION;
    packet[3] = flags;
    write_le16(packet + 4, recording_id);
    write_le16(packet + 6, sequence);
    write_le16(packet + 8, AUDIO_SAMPLES);
    write_le16(packet + 10, (uint16_t)predictor);
    packet[12] = step_index;
    packet[13] = 0;
}

static esp_err_t send_audio_with_retry(const uint8_t *frame, size_t length)
{
    esp_err_t result = ESP_FAIL;
    for (unsigned attempt = 0; attempt < BLE_SEND_RETRY_COUNT; ++attempt) {
        result = cat_ble_send_audio(frame, length);
        if (result == ESP_OK || result != ESP_ERR_NO_MEM) return result;
        vTaskDelay(pdMS_TO_TICKS(BLE_SEND_RETRY_MS));
    }
    return result;
}

static esp_err_t send_control_with_retry(uint8_t type, uint16_t message_id,
                                         const uint8_t *payload, size_t length)
{
    esp_err_t result = ESP_FAIL;
    for (unsigned attempt = 0; attempt < BLE_SEND_RETRY_COUNT; ++attempt) {
        result = cat_ble_send_control(type, message_id, payload, length);
        if (result == ESP_OK || result != ESP_ERR_NO_MEM) return result;
        vTaskDelay(pdMS_TO_TICKS(BLE_SEND_RETRY_MS));
    }
    return result;
}

static void recover_ble_send_failure(const char *operation, esp_err_t result)
{
    ESP_LOGE(TAG, "%s failed, resetting BLE session: %s", operation, esp_err_to_name(result));
    atomic_store_explicit(&s_cancel_recording, true, memory_order_release);
    atomic_store_explicit(&s_recording, false, memory_order_release);
    (void)cat_ble_disconnect();
    if (s_model.state != CAT_STATE_DISCONNECTED) apply(CAT_EVENT_BLE_DISCONNECTED);
}

static void audio_task(void *context)
{
    (void)context;
    int16_t pcm[AUDIO_SAMPLES];
    uint8_t pending[AUDIO_PACKET_BYTES];
    uint8_t current[AUDIO_PACKET_BYTES];
    for (;;) {
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
        const uint16_t recording_id = s_model.armed_recording_id;
        cat_adpcm_state_t adpcm = {0};
        uint16_t frames = 0;
        uint16_t preroll_frames = 0;
        bool have_pending = false;
        bool failed = false;

        while (atomic_load_explicit(&s_recording, memory_order_acquire) &&
               frames < AUDIO_MAX_FRAMES) {
            /*
             * 预录最多保留 300 ms。阈值事件通常与第 15 帧同时到达；若应用任务
             * 短暂繁忙，这里让出 CPU，而不是覆盖开头语音或扩大无界缓存。
             */
            if (!atomic_load_explicit(&s_recording_active, memory_order_acquire) &&
                preroll_frames >= AUDIO_PREROLL_FRAMES) {
                vTaskDelay(pdMS_TO_TICKS(1));
                continue;
            }
            if (bsp_audio_read(pcm, sizeof(pcm)) != ESP_OK) { failed = true; break; }
            int16_t predictor;
            uint8_t step_index;
            const size_t encoded = cat_adpcm_encode(
                pcm, AUDIO_SAMPLES, &adpcm, &predictor, &step_index,
                current + CAT_AUDIO_HEADER_SIZE, AUDIO_PAYLOAD_BYTES);
            if (encoded != AUDIO_PAYLOAD_BYTES) { failed = true; break; }
            fill_audio_header(current, frames == 0 ? 0x01 : 0x00,
                              recording_id, frames, predictor, step_index);

            if (!atomic_load_explicit(&s_recording_active, memory_order_acquire)) {
                memcpy(s_audio_preroll[preroll_frames++], current, sizeof(current));
                ++frames;
                continue;
            }

            /* RECORD_START 已经发出，先把固定预录片段按原序送出。 */
            for (uint16_t i = 0; i < preroll_frames; ++i) {
                if (have_pending && send_audio_with_retry(pending, sizeof(pending)) != ESP_OK) {
                    failed = true;
                    break;
                }
                if (have_pending) vTaskDelay(pdMS_TO_TICKS(1));
                memcpy(pending, s_audio_preroll[i], sizeof(pending));
                have_pending = true;
            }
            preroll_frames = 0;
            if (failed) break;
            if (have_pending && send_audio_with_retry(pending, sizeof(pending)) != ESP_OK) {
                failed = true;
                break;
            }
            memcpy(pending, current, sizeof(pending));
            have_pending = true;
            ++frames;
        }
        atomic_store_explicit(&s_recording, false, memory_order_release);
        const bool activated =
            atomic_load_explicit(&s_recording_active, memory_order_acquire);
        if (activated && !failed) {
            for (uint16_t i = 0; i < preroll_frames; ++i) {
                if (have_pending && send_audio_with_retry(pending, sizeof(pending)) != ESP_OK) {
                    failed = true;
                    break;
                }
                if (have_pending) vTaskDelay(pdMS_TO_TICKS(1));
                memcpy(pending, s_audio_preroll[i], sizeof(pending));
                have_pending = true;
            }
        }
        if (activated && !failed && have_pending) {
            pending[3] |= 0x02;
            if (send_audio_with_retry(pending, sizeof(pending)) != ESP_OK) failed = true;
        }
        const input_t done = {
            .type = INPUT_AUDIO_FINISHED,
            .data.audio = {
                .recording_id = recording_id,
                .frames = frames,
                .activated = activated,
                .canceled = atomic_load_explicit(&s_cancel_recording, memory_order_acquire),
                .failed = failed,
                .limit = frames >= AUDIO_MAX_FRAMES,
            },
        };
        /* 这是发送 RECORD_END/RECORD_CANCEL 的唯一触发点，绝不能静默丢弃。 */
        if (xQueueSend(s_queue, &done, portMAX_DELAY) != pdTRUE)
            ESP_LOGE(TAG, "audio completion queue failed");
    }
}

/* 开始本地预录，不发送控制消息；短按时可完全静默丢弃。 */
static void arm_recording(void)
{
    atomic_store_explicit(&s_cancel_recording, false, memory_order_release);
    atomic_store_explicit(&s_recording_active, false, memory_order_release);
    atomic_store_explicit(&s_recording, true, memory_order_release);
    xTaskNotifyGive(s_audio_task);
}

/* 达到 300 ms 后先宣布录音，再允许 audio_task 刷出预录帧。 */
static void activate_recording(void)
{
    uint8_t payload[9];
    write_le16(payload, s_model.recording_id);
    write_le32(payload + 2, 16000);
    write_le16(payload + 6, AUDIO_SAMPLES);
    payload[8] = 1; /* frame-independent IMA-ADPCM */
    const esp_err_t result = send_control_with_retry(
        CAT_MSG_RECORD_START, next_message_id(), payload, sizeof(payload));
    if (result != ESP_OK) {
        recover_ble_send_failure("RECORD_START", result);
        return;
    }
    atomic_store_explicit(&s_recording_active, true, memory_order_release);
}

static void execute(uint32_t actions)
{
    if (actions & CAT_ACTION_CLEAR_TEXT) set_text(NULL, 0);
    uint8_t recording[2];
    write_le16(recording, s_model.recording_id);
    if (actions & CAT_ACTION_REQUEST_RERECORD) {
        const esp_err_t result = send_control_with_retry(
            CAT_MSG_TRANSCRIPT_RETRY, next_message_id(), recording, 2);
        if (result != ESP_OK) { recover_ble_send_failure("TRANSCRIPT_RETRY", result); return; }
    }
    if (actions & CAT_ACTION_START_RECORDING) arm_recording();
    if (actions & CAT_ACTION_ACTIVATE_RECORDING) activate_recording();
    if (actions & CAT_ACTION_STOP_RECORDING) {
        atomic_store_explicit(&s_recording, false, memory_order_release);
    }
    if (actions & CAT_ACTION_CANCEL_RECORDING) {
        atomic_store_explicit(&s_cancel_recording, true, memory_order_release);
        atomic_store_explicit(&s_recording, false, memory_order_release);
    }
    if (actions & CAT_ACTION_CONFIRM_TRANSCRIPT) {
        const esp_err_t result = send_control_with_retry(
            CAT_MSG_TRANSCRIPT_CONFIRM, next_message_id(), recording, 2);
        if (result != ESP_OK) { recover_ble_send_failure("TRANSCRIPT_CONFIRM", result); return; }
    }
    if (actions & CAT_ACTION_CANCEL_TURN) {
        const esp_err_t result = send_control_with_retry(
            CAT_MSG_CANCEL_TURN, next_message_id(), recording, 2);
        if (result != ESP_OK) { recover_ble_send_failure("CANCEL_TURN", result); return; }
    }
    if (actions & CAT_ACTION_NEW_CONVERSATION) {
        uint8_t token[4];
        write_le32(token, (uint32_t)(esp_timer_get_time() / 1000));
        const esp_err_t result = send_control_with_retry(
            CAT_MSG_NEW_CONVERSATION, next_message_id(), token, 4);
        if (result != ESP_OK) { recover_ble_send_failure("NEW_CONVERSATION", result); return; }
    }
    if (actions & CAT_ACTION_REFRESH_UI) render();
}

static void apply(cat_app_event_t event)
{
    const cat_app_state_t previous = s_model.state;
    const uint32_t actions = cat_state_apply(&s_model, event);
    const bool notice = s_model.state == CAT_STATE_NEW_CONVERSATION;
    if (notice && s_model.state != previous) {
        s_notice_until_ms = monotonic_ms() + NOTICE_DURATION_MS;
    } else if (!notice) {
        s_notice_until_ms = 0;
    }
    execute(actions);
}

static bool text_is_scrollable(void)
{
    return s_model.state == CAT_STATE_TRANSCRIPT_REVIEW ||
           s_model.state == CAT_STATE_AI_STREAMING ||
           s_model.state == CAT_STATE_ANSWER_READY;
}

/*
 * 处理 OK 的 300 ms 分流。IDLE/ANSWER_READY/TRANSCRIPT_REVIEW 会先进入本地
 * 预录；AI_STREAMING 则把 OK 作为停止生成，不启动录音。
 */
static void handle_ok_button(bsp_btn_ev_t event, uint64_t now)
{
    if (event == BSP_BTN_PRESS) {
        apply(CAT_EVENT_OK_DOWN);
        if (s_model.state == CAT_STATE_RECORDING && !s_model.recording_active) {
            s_ok_held = true;
            s_ok_pressed_at_ms = now;
        }
        return;
    }
    if (event != BSP_BTN_RELEASE || !s_ok_held) return;

    if (s_model.state == CAT_STATE_RECORDING) {
        if (!s_model.recording_active && now - s_ok_pressed_at_ms >= OK_HOLD_THRESHOLD_MS)
            apply(CAT_EVENT_RECORD_ACTIVATE);
        apply(CAT_EVENT_OK_UP);
    }
    s_ok_held = false;
}

static void update_interactions(uint64_t now)
{
    if (s_ok_held && s_model.state == CAT_STATE_RECORDING &&
        !s_model.recording_active && now - s_ok_pressed_at_ms >= OK_HOLD_THRESHOLD_MS) {
        apply(CAT_EVENT_RECORD_ACTIVATE);
    }
    if (s_ok_held && s_model.state != CAT_STATE_RECORDING) s_ok_held = false;

    if (!text_is_scrollable()) {
        cat_scroll_stop(&s_scroll_motion);
        s_scroll_tick_ms = now;
        return;
    }
    if (!cat_scroll_is_active(&s_scroll_motion)) {
        s_scroll_tick_ms = now;
        return;
    }
    const uint32_t elapsed = s_scroll_tick_ms == 0 || now <= s_scroll_tick_ms
                                 ? INTERACTION_TICK_MS
                                 : (uint32_t)(now - s_scroll_tick_ms);
    s_scroll_tick_ms = now;
    const int32_t pixels = cat_scroll_update(&s_scroll_motion, elapsed);
    if (pixels != 0 && !scroll_text_pixels(pixels)) cat_scroll_stop(&s_scroll_motion);
}

static void handle_message(const cat_protocol_message_t *message)
{
    switch (message->type) {
    case CAT_MSG_APP_READY:
        note_screen_activity();
        apply(CAT_EVENT_BLE_CONNECTED);
        break;
    case CAT_MSG_ASR_RESULT:
        note_screen_activity();
        if (message->payload_length <= CAT_TRANSCRIPT_MAX &&
            cat_protocol_utf8_valid(message->payload, message->payload_length)) {
            set_text(message->payload, message->payload_length);
            apply(CAT_EVENT_ASR_RESULT);
        }
        break;
    case CAT_MSG_ASR_ERROR:
        note_screen_activity();
        set_text(message->payload, message->payload_length);
        apply(CAT_EVENT_ASR_ERROR);
        break;
    case CAT_MSG_CHAT_STARTED:
        note_screen_activity();
        set_text(NULL, 0);
        apply(CAT_EVENT_CHAT_STARTED);
        break;
    case CAT_MSG_CHAT_DELTA:
        note_screen_activity();
        append_text(message->payload, message->payload_length);
        apply(CAT_EVENT_CHAT_DELTA);
        break;
    case CAT_MSG_CHAT_DONE:
        note_screen_activity();
        apply(CAT_EVENT_CHAT_DONE);
        break;
    case CAT_MSG_CHAT_ERROR:
        note_screen_activity();
        if (s_text_length == 0) set_text(message->payload, message->payload_length);
        apply(CAT_EVENT_CHAT_ERROR);
        break;
    case CAT_MSG_SESSION_RESET:
        note_screen_activity();
        set_text(NULL, 0);
        apply(CAT_EVENT_SESSION_RESET);
        break;
    case CAT_MSG_TIME_SYNC:
        if (message->payload_length == 4 &&
            cat_clock_sync(&s_clock, read_le32(message->payload), monotonic_ms())) {
            s_clock_minute = -1;
            render();
        }
        break;
    case CAT_MSG_SCREEN_TIMEOUT:
        if (message->payload_length == 4) {
            const uint32_t seconds = read_le32(message->payload);
            const uint32_t previous = s_screen.timeout_seconds;
            const bool was_off = cat_screen_is_off(&s_screen);
            if (cat_screen_set_timeout(&s_screen, seconds, monotonic_ms())) {
                if (was_off) bsp_display_backlight(SCREEN_ACTIVE_BRIGHTNESS);
                if (seconds != previous) save_screen_timeout(seconds);
            }
        }
        break;
    case CAT_MSG_PING:
        {
            const esp_err_t result = send_control_with_retry(
                CAT_MSG_PONG, next_message_id(), message->payload, message->payload_length);
            if (result != ESP_OK) recover_ble_send_failure("PONG", result);
        }
        break;
    default:
        break;
    }
}

static void handle_button(bsp_btn_t button, bsp_btn_ev_t event)
{
    if ((unsigned)button >= 3U) return;
    if (s_suppress_button_action[button]) {
        if (event == BSP_BTN_CLICK || event == BSP_BTN_LONG) {
            s_suppress_button_action[button] = false;
        }
        return;
    }
    if (cat_screen_is_off(&s_screen) && event == BSP_BTN_PRESS) {
        note_screen_activity();
        if (button != BSP_BTN_OK) {
            s_suppress_button_action[button] = true;
            return;
        }
    }
    note_screen_activity();
    if (text_is_scrollable() && (button == BSP_BTN_UP || button == BSP_BTN_DOWN)) {
        const int direction = button == BSP_BTN_DOWN ? 1 : -1;
        if (event == BSP_BTN_PRESS) {
            cat_scroll_press(&s_scroll_motion, direction);
            s_scroll_tick_ms = monotonic_ms();
        } else if (event == BSP_BTN_RELEASE) {
            cat_scroll_release(&s_scroll_motion, direction);
        }
        /* CLICK/LONG 在阅读页全部被消费，永远不会落入业务状态机。 */
        return;
    }
    if (button == BSP_BTN_OK) handle_ok_button(event, monotonic_ms());
    else if (button == BSP_BTN_UP && event == BSP_BTN_LONG) apply(CAT_EVENT_UP_LONG);
    else if (button == BSP_BTN_DOWN && event == BSP_BTN_LONG) apply(CAT_EVENT_DOWN_LONG);
}

static void handle_audio_finished(const input_t *input)
{
    if (!input->data.audio.activated) {
        if (input->data.audio.failed && s_model.state == CAT_STATE_RECORDING &&
            s_model.recording_id == input->data.audio.recording_id) {
            apply(CAT_EVENT_RECORD_ABORT);
            s_ok_held = false;
        }
        return;
    }
    if (input->data.audio.limit && s_model.state == CAT_STATE_RECORDING)
        apply(CAT_EVENT_RECORD_LIMIT);
    uint8_t payload[8];
    write_le16(payload, input->data.audio.recording_id);
    write_le32(payload + 2, (uint32_t)input->data.audio.frames * 20U);
    write_le16(payload + 6, input->data.audio.frames);
    const bool too_short = !input->data.audio.canceled && !input->data.audio.failed &&
                           cat_recording_is_too_short(input->data.audio.frames);
    const uint8_t type = (input->data.audio.canceled || input->data.audio.failed || too_short)
                             ? CAT_MSG_RECORD_CANCEL : CAT_MSG_RECORD_END;
    const esp_err_t send_result = input->data.audio.activated
                                      ? send_control_with_retry(type, next_message_id(), payload,
                                                                sizeof(payload))
                                      : ESP_OK;
    if (send_result != ESP_OK && s_model.state != CAT_STATE_DISCONNECTED) {
        recover_ble_send_failure("recording terminal message", send_result);
        return;
    }
    if (too_short && s_model.state == CAT_STATE_TRANSCRIBING) {
        apply(CAT_EVENT_RECORD_TOO_SHORT);
    } else if (input->data.audio.failed && s_model.state != CAT_STATE_DISCONNECTED) {
        static const char error[] = "录音中断，请重试。";
        set_text((const uint8_t *)error, strlen(error));
        apply(CAT_EVENT_ASR_ERROR);
    }
}

static void send_hello(void)
{
    static const char hello[] =
        "{\"name\":\"小猫助手\",\"protocol\":1,\"audio\":\"ima-adpcm-16k-mono\",\"max_record_ms\":30000}";
    s_last_hello_ms = monotonic_ms();
    const esp_err_t result = send_control_with_retry(
        CAT_MSG_HELLO, next_message_id(), (const uint8_t *)hello, strlen(hello));
    ESP_LOGI(TAG, "HELLO send result=%s", esp_err_to_name(result));
    if (result != ESP_OK) recover_ble_send_failure("HELLO", result);
}

static void handle_pending_ble_state(void)
{
    if (atomic_exchange_explicit(&s_ble_disconnected_pending, false, memory_order_acq_rel)) {
        atomic_store_explicit(&s_cancel_recording, true, memory_order_release);
        atomic_store_explicit(&s_recording, false, memory_order_release);
        s_ok_held = false;
        s_last_hello_ms = 0;
        cat_scroll_stop(&s_scroll_motion);
        cat_protocol_reassembler_init(&s_reassembler, s_rx_buffer, sizeof(s_rx_buffer));
        apply(CAT_EVENT_BLE_DISCONNECTED);
    }
    if (atomic_exchange_explicit(&s_ble_ready_pending, false, memory_order_acq_rel)) {
        send_hello();
    }
}

static void app_task(void *context)
{
    (void)context;
    input_t input;
    for (;;) {
        const bool interaction_active = s_ok_held || cat_scroll_is_active(&s_scroll_motion);
        const TickType_t wait = pdMS_TO_TICKS(interaction_active ? INTERACTION_TICK_MS : 250U);
        if (xQueueReceive(s_queue, &input, wait) == pdTRUE) {
            switch (input.type) {
            case INPUT_BUTTON:
                handle_button(input.data.key.button, input.data.key.event);
                break;
            case INPUT_BLE_STATE:
                handle_pending_ble_state();
                break;
            case INPUT_BLE_FRAGMENT: {
                cat_protocol_message_t message;
                if (cat_protocol_receive(&s_reassembler, input.data.fragment.bytes,
                                         input.data.fragment.length, &message) == CAT_PROTOCOL_COMPLETE)
                    handle_message(&message);
                break;
            }
            case INPUT_AUDIO_FINISHED:
                handle_audio_finished(&input);
                break;
            }
        }
        handle_pending_ble_state();
        const uint64_t now = monotonic_ms();
        // Windows can restore CCCD subscriptions before the application has
        // installed its notification handlers. Repeat HELLO until APP_READY so
        // reconnect success does not depend on that startup race.
        if (cat_ble_ready() && !s_model.app_ready &&
            now - s_last_hello_ms >= HELLO_RETRY_MS)
            send_hello();
        update_interactions(now);
        if (s_notice_until_ms != 0 && now >= s_notice_until_ms) {
            s_notice_until_ms = 0;
            apply(CAT_EVENT_NOTICE_DONE);
        }
        if (now - s_last_battery_ms >= BATTERY_REFRESH_MS) {
            s_last_battery_ms = now;
            s_battery = bsp_battery_soc();
            render();
        }
        const int minute = cat_clock_minute_of_day(&s_clock, now);
        if (minute != s_clock_minute) {
            s_clock_minute = minute;
            render();
        }
        if (cat_screen_update(&s_screen, now, screen_must_stay_awake())) {
            bsp_display_backlight(0);
        }
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "小猫助手启动");
    const esp_err_t nvs_result = nvs_flash_init();
    if (nvs_result != ESP_OK) {
        ESP_LOGE(TAG, "NVS 初始化失败，不会自动擦除已有数据: %s",
                 esp_err_to_name(nvs_result));
        return;
    }
    if (bsp_i2c_init() != ESP_OK || bsp_display_init() != ESP_OK || bsp_lvgl_init() == NULL) {
        ESP_LOGE(TAG, "基础硬件初始化失败");
        return;
    }
    bsp_display_backlight(SCREEN_ACTIVE_BRIGHTNESS);
    cat_state_init(&s_model);
    cat_scroll_init(&s_scroll_motion);
    cat_clock_init(&s_clock);
    cat_screen_init(&s_screen, load_screen_timeout(), monotonic_ms());
    cat_protocol_reassembler_init(&s_reassembler, s_rx_buffer, sizeof(s_rx_buffer));
    (void)bsp_battery_init();
    s_battery = bsp_battery_soc();
    if (bsp_lvgl_lock(1000)) {
        cat_ui_init();
        cat_ui_render(&s_model, NULL, 0, s_battery, -1);
        bsp_lvgl_unlock();
    }

    s_queue = xQueueCreate(APP_QUEUE_DEPTH, sizeof(input_t));
    if (s_queue == NULL || bsp_audio_init() != ESP_OK ||
        bsp_audio_set_format(16000, 16, 1) != ESP_OK ||
        xTaskCreate(audio_task, "cat_audio", 4096, NULL, 6, &s_audio_task) != pdPASS ||
        xTaskCreate(app_task, "cat_app", 6144, NULL, 5, NULL) != pdPASS ||
        bsp_button_init(on_button, NULL) != ESP_OK ||
        cat_ble_init(on_ble, NULL) != ESP_OK) {
        ESP_LOGE(TAG, "小猫助手服务初始化失败");
    }
}
