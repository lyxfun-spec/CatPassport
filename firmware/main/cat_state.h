#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    CAT_STATE_DISCONNECTED,
    CAT_STATE_IDLE,
    CAT_STATE_RECORDING,
    CAT_STATE_TRANSCRIBING,
    CAT_STATE_TRANSCRIPT_REVIEW,
    CAT_STATE_AI_WAITING,
    CAT_STATE_AI_STREAMING,
    CAT_STATE_ANSWER_READY,
    CAT_STATE_ERROR,
    CAT_STATE_RECORD_TOO_SHORT,
    CAT_STATE_NEW_CONVERSATION,
} cat_app_state_t;

typedef enum {
    CAT_EVENT_BLE_CONNECTED,
    CAT_EVENT_BLE_DISCONNECTED,
    CAT_EVENT_OK_DOWN,
    CAT_EVENT_OK_UP,
    CAT_EVENT_UP_LONG,
    CAT_EVENT_DOWN_LONG,
    CAT_EVENT_ASR_STARTED,
    CAT_EVENT_ASR_RESULT,
    CAT_EVENT_ASR_ERROR,
    CAT_EVENT_CHAT_STARTED,
    CAT_EVENT_CHAT_DELTA,
    CAT_EVENT_CHAT_DONE,
    CAT_EVENT_CHAT_ERROR,
    CAT_EVENT_RECORD_LIMIT,
    CAT_EVENT_RECORD_TOO_SHORT,
    CAT_EVENT_NOTICE_DONE,
    CAT_EVENT_SESSION_RESET,
} cat_app_event_t;

enum {
    CAT_ACTION_NONE = 0,
    CAT_ACTION_START_RECORDING = 1U << 0,
    CAT_ACTION_STOP_RECORDING = 1U << 1,
    CAT_ACTION_CANCEL_RECORDING = 1U << 2,
    CAT_ACTION_CONFIRM_TRANSCRIPT = 1U << 3,
    CAT_ACTION_REQUEST_RERECORD = 1U << 4,
    CAT_ACTION_CANCEL_TURN = 1U << 5,
    CAT_ACTION_CLEAR_TEXT = 1U << 6,
    CAT_ACTION_NEW_CONVERSATION = 1U << 7,
    CAT_ACTION_REFRESH_UI = 1U << 8,
};

typedef struct {
    cat_app_state_t state;
    cat_app_state_t error_return_state;
    uint16_t recording_id;
    bool app_ready;
    bool has_transcript;
    bool has_answer;
} cat_app_model_t;

void cat_state_init(cat_app_model_t *model);
uint32_t cat_state_apply(cat_app_model_t *model, cat_app_event_t event);
bool cat_recording_is_too_short(uint16_t frames);
