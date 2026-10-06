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
#include "cat_protocol.h"
#include "cat_state.h"
#include "cat_ui.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "nvs_flash.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"

#define APP_QUEUE_DEPTH 10
#define AUDIO_SAMPLES 320U
#define AUDIO_PAYLOAD_BYTES 160U
#define AUDIO_PACKET_BYTES (CAT_AUDIO_HEADER_SIZE + AUDIO_PAYLOAD_BYTES)
#define AUDIO_MAX_FRAMES 1500U
#define BATTERY_REFRESH_MS 10000ULL

typedef enum {
    INPUT_BUTTON, INPUT_BLE_READY, INPUT_BLE_DISCONNECTED,
    INPUT_BLE_FRAGMENT, INPUT_AUDIO_FINISHED,
} input_type_t;

typedef struct {
    input_type_t type;
    union {
        struct { bsp_btn_t button; bsp_btn_ev_t event; } key;
        struct { uint16_t length; uint8_t bytes[244]; } fragment;
        struct { uint16_t recording_id; uint16_t frames; bool canceled; bool failed; bool limit; } audio;
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
static atomic_bool s_cancel_recording;
static uint16_t s_message_id;
static uint64_t s_last_battery_ms;
static int s_battery = -1;

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

static uint16_t next_message_id(void)
{
    if (++s_message_id == 0) ++s_message_id;
    return s_message_id;
}

static void render(void)
{
    if (!bsp_lvgl_lock(200)) return;
    cat_ui_render(&s_model, s_text, s_text_length, s_battery);
    bsp_lvgl_unlock();
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

static void queue_input(const input_t *input)
{
    if (s_queue != NULL) (void)xQueueSend(s_queue, input, 0);
}

static void on_button(bsp_btn_t button, bsp_btn_ev_t event, void *context)
{
    (void)context;
    const input_t input = {.type = INPUT_BUTTON,
                           .data.key = {.button = button, .event = event}};
    queue_input(&input);
}

static void on_ble(const cat_ble_event_t *event, void *context)
{
    (void)context;
    input_t input = {0};
    if (event->type == CAT_BLE_EVENT_READY) input.type = INPUT_BLE_READY;
    else if (event->type == CAT_BLE_EVENT_DISCONNECTED) input.type = INPUT_BLE_DISCONNECTED;
    else {
        if (event->length > sizeof(input.data.fragment.bytes)) return;
        input.type = INPUT_BLE_FRAGMENT;
        input.data.fragment.length = (uint16_t)event->length;
        memcpy(input.data.fragment.bytes, event->data, event->length);
    }
    queue_input(&input);
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

static void audio_task(void *context)
{
    (void)context;
    int16_t pcm[AUDIO_SAMPLES];
    uint8_t pending[AUDIO_PACKET_BYTES];
    uint8_t current[AUDIO_PACKET_BYTES];
    for (;;) {
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
        const uint16_t recording_id = s_model.recording_id;
        cat_adpcm_state_t adpcm = {0};
        uint16_t frames = 0;
        bool have_pending = false;
        bool failed = false;

        while (atomic_load_explicit(&s_recording, memory_order_acquire) &&
               frames < AUDIO_MAX_FRAMES) {
            if (bsp_audio_read(pcm, sizeof(pcm)) != ESP_OK) { failed = true; break; }
            int16_t predictor;
            uint8_t step_index;
            const size_t encoded = cat_adpcm_encode(
                pcm, AUDIO_SAMPLES, &adpcm, &predictor, &step_index,
                current + CAT_AUDIO_HEADER_SIZE, AUDIO_PAYLOAD_BYTES);
            if (encoded != AUDIO_PAYLOAD_BYTES) { failed = true; break; }
            fill_audio_header(current, frames == 0 ? 0x01 : 0x00,
                              recording_id, frames, predictor, step_index);
            if (have_pending && cat_ble_send_audio(pending, sizeof(pending)) != ESP_OK) {
                failed = true;
                break;
            }
            memcpy(pending, current, sizeof(pending));
            have_pending = true;
            ++frames;
        }
        atomic_store_explicit(&s_recording, false, memory_order_release);
        if (!failed && have_pending) {
            pending[3] |= 0x02;
            if (cat_ble_send_audio(pending, sizeof(pending)) != ESP_OK) failed = true;
        }
        const input_t done = {
            .type = INPUT_AUDIO_FINISHED,
            .data.audio = {
                .recording_id = recording_id,
                .frames = frames,
                .canceled = atomic_load_explicit(&s_cancel_recording, memory_order_acquire),
                .failed = failed,
                .limit = frames >= AUDIO_MAX_FRAMES,
            },
        };
        queue_input(&done);
    }
}

static void send_record_start(void)
{
    uint8_t payload[9];
    write_le16(payload, s_model.recording_id);
    write_le32(payload + 2, 16000);
    write_le16(payload + 6, AUDIO_SAMPLES);
    payload[8] = 1; /* frame-independent IMA-ADPCM */
    if (cat_ble_send_control(CAT_MSG_RECORD_START, next_message_id(), payload,
                             sizeof(payload)) != ESP_OK) {
        (void)cat_state_apply(&s_model, CAT_EVENT_BLE_DISCONNECTED);
        return;
    }
    atomic_store_explicit(&s_cancel_recording, false, memory_order_release);
    atomic_store_explicit(&s_recording, true, memory_order_release);
    xTaskNotifyGive(s_audio_task);
}

static void execute(uint32_t actions)
{
    if (actions & CAT_ACTION_CLEAR_TEXT) set_text(NULL, 0);
    if (actions & CAT_ACTION_START_RECORDING) send_record_start();
    if (actions & CAT_ACTION_STOP_RECORDING) {
        atomic_store_explicit(&s_recording, false, memory_order_release);
    }
    if (actions & CAT_ACTION_CANCEL_RECORDING) {
        atomic_store_explicit(&s_cancel_recording, true, memory_order_release);
        atomic_store_explicit(&s_recording, false, memory_order_release);
    }
    uint8_t recording[2];
    write_le16(recording, s_model.recording_id);
    if (actions & CAT_ACTION_CONFIRM_TRANSCRIPT)
        (void)cat_ble_send_control(CAT_MSG_TRANSCRIPT_CONFIRM, next_message_id(), recording, 2);
    if (actions & CAT_ACTION_REQUEST_RERECORD)
        (void)cat_ble_send_control(CAT_MSG_TRANSCRIPT_RETRY, next_message_id(), recording, 2);
    if (actions & CAT_ACTION_CANCEL_TURN)
        (void)cat_ble_send_control(CAT_MSG_CANCEL_TURN, next_message_id(), recording, 2);
    if (actions & CAT_ACTION_NEW_CONVERSATION) {
        uint8_t token[4];
        write_le32(token, (uint32_t)(esp_timer_get_time() / 1000));
        (void)cat_ble_send_control(CAT_MSG_NEW_CONVERSATION, next_message_id(), token, 4);
    }
    if (actions & CAT_ACTION_REFRESH_UI) render();
}

static void apply(cat_app_event_t event)
{
    execute(cat_state_apply(&s_model, event));
}

static void handle_message(const cat_protocol_message_t *message)
{
    switch (message->type) {
    case CAT_MSG_APP_READY:
        apply(CAT_EVENT_BLE_CONNECTED);
        break;
    case CAT_MSG_ASR_RESULT:
        if (message->payload_length <= CAT_TRANSCRIPT_MAX &&
            cat_protocol_utf8_valid(message->payload, message->payload_length)) {
            set_text(message->payload, message->payload_length);
            apply(CAT_EVENT_ASR_RESULT);
        }
        break;
    case CAT_MSG_ASR_ERROR:
        set_text(message->payload, message->payload_length);
        apply(CAT_EVENT_ASR_ERROR);
        break;
    case CAT_MSG_CHAT_STARTED:
        set_text(NULL, 0);
        apply(CAT_EVENT_CHAT_STARTED);
        break;
    case CAT_MSG_CHAT_DELTA:
        append_text(message->payload, message->payload_length);
        apply(CAT_EVENT_CHAT_DELTA);
        break;
    case CAT_MSG_CHAT_DONE:
        apply(CAT_EVENT_CHAT_DONE);
        break;
    case CAT_MSG_CHAT_ERROR:
        if (s_text_length == 0) set_text(message->payload, message->payload_length);
        apply(CAT_EVENT_CHAT_ERROR);
        break;
    case CAT_MSG_SESSION_RESET:
        set_text(NULL, 0);
        apply(CAT_EVENT_SESSION_RESET);
        break;
    case CAT_MSG_PING:
        (void)cat_ble_send_control(CAT_MSG_PONG, next_message_id(),
                                   message->payload, message->payload_length);
        break;
    default:
        break;
    }
}

static void handle_button(bsp_btn_t button, bsp_btn_ev_t event)
{
    if (button == BSP_BTN_OK && event == BSP_BTN_PRESS) apply(CAT_EVENT_OK_DOWN);
    else if (button == BSP_BTN_OK && event == BSP_BTN_RELEASE) apply(CAT_EVENT_OK_UP);
    else if (button == BSP_BTN_UP && event == BSP_BTN_CLICK) apply(CAT_EVENT_UP_CLICK);
    else if (button == BSP_BTN_DOWN && event == BSP_BTN_CLICK) apply(CAT_EVENT_DOWN_CLICK);
    else if (button == BSP_BTN_UP && event == BSP_BTN_LONG) apply(CAT_EVENT_NEW_CONVERSATION);
}

static void handle_audio_finished(const input_t *input)
{
    if (input->data.audio.limit && s_model.state == CAT_STATE_RECORDING)
        apply(CAT_EVENT_RECORD_LIMIT);
    uint8_t payload[8];
    write_le16(payload, input->data.audio.recording_id);
    write_le32(payload + 2, (uint32_t)input->data.audio.frames * 20U);
    write_le16(payload + 6, input->data.audio.frames);
    const uint8_t type = (input->data.audio.canceled || input->data.audio.failed)
                             ? CAT_MSG_RECORD_CANCEL : CAT_MSG_RECORD_END;
    (void)cat_ble_send_control(type, next_message_id(), payload, sizeof(payload));
    if (input->data.audio.failed && s_model.state != CAT_STATE_DISCONNECTED) {
        static const char error[] = "录音中断，请重试。";
        set_text((const uint8_t *)error, strlen(error));
        apply(CAT_EVENT_ASR_ERROR);
    }
}

static void app_task(void *context)
{
    (void)context;
    input_t input;
    for (;;) {
        if (xQueueReceive(s_queue, &input, pdMS_TO_TICKS(1000)) == pdTRUE) {
            switch (input.type) {
            case INPUT_BUTTON:
                handle_button(input.data.key.button, input.data.key.event);
                break;
            case INPUT_BLE_READY: {
                static const char hello[] =
                    "{\"name\":\"小猫助手\",\"protocol\":1,\"audio\":\"ima-adpcm-16k-mono\",\"max_record_ms\":30000}";
                (void)cat_ble_send_control(CAT_MSG_HELLO, next_message_id(),
                                           (const uint8_t *)hello, strlen(hello));
                break;
            }
            case INPUT_BLE_DISCONNECTED:
                atomic_store_explicit(&s_cancel_recording, true, memory_order_release);
                atomic_store_explicit(&s_recording, false, memory_order_release);
                apply(CAT_EVENT_BLE_DISCONNECTED);
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
        const uint64_t now = (uint64_t)esp_timer_get_time() / 1000ULL;
        if (now - s_last_battery_ms >= BATTERY_REFRESH_MS) {
            s_last_battery_ms = now;
            s_battery = bsp_battery_soc();
            render();
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
    bsp_display_backlight(80);
    cat_state_init(&s_model);
    cat_protocol_reassembler_init(&s_reassembler, s_rx_buffer, sizeof(s_rx_buffer));
    (void)bsp_battery_init();
    s_battery = bsp_battery_soc();
    if (bsp_lvgl_lock(1000)) {
        cat_ui_init();
        cat_ui_render(&s_model, NULL, 0, s_battery);
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
