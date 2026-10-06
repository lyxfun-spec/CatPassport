#include "cat_state.h"

#include <stddef.h>

#define CAT_RECORDING_MIN_FRAMES 15U /* 15 x 20 ms = 300 ms */

bool cat_recording_is_too_short(uint16_t frames)
{
    return frames < CAT_RECORDING_MIN_FRAMES;
}

void cat_state_init(cat_app_model_t *model)
{
    if (model == NULL) return;
    *model = (cat_app_model_t){
        .state = CAT_STATE_DISCONNECTED,
        .error_return_state = CAT_STATE_IDLE,
    };
}

/*
 * 纯状态归约器：不访问硬件、不分配内存。调用方按返回 action 在工作任务中执行慢操作。
 * 不合法或迟到的云端事件被忽略，防止上一轮响应污染当前页面。
 */
uint32_t cat_state_apply(cat_app_model_t *model, cat_app_event_t event)
{
    if (model == NULL) return CAT_ACTION_NONE;
    uint32_t actions = CAT_ACTION_NONE;

    if (event == CAT_EVENT_BLE_DISCONNECTED) {
        if (model->state == CAT_STATE_RECORDING) actions |= CAT_ACTION_CANCEL_RECORDING;
        model->state = CAT_STATE_DISCONNECTED;
        model->app_ready = false;
        model->has_transcript = false;
        model->has_answer = false;
        return actions | CAT_ACTION_CLEAR_TEXT | CAT_ACTION_REFRESH_UI;
    }
    if (event == CAT_EVENT_BLE_CONNECTED) {
        model->state = CAT_STATE_IDLE;
        model->app_ready = true;
        return CAT_ACTION_REFRESH_UI;
    }
    if (model->state == CAT_STATE_DISCONNECTED) return CAT_ACTION_NONE;
    if (event == CAT_EVENT_SESSION_RESET) {
        if (model->state != CAT_STATE_NEW_CONVERSATION) model->state = CAT_STATE_IDLE;
        model->has_transcript = false;
        model->has_answer = false;
        return CAT_ACTION_CLEAR_TEXT | CAT_ACTION_REFRESH_UI;
    }

    switch (model->state) {
    case CAT_STATE_IDLE:
    case CAT_STATE_ANSWER_READY:
        if (event == CAT_EVENT_OK_DOWN) {
            model->recording_id++;
            if (model->recording_id == 0) model->recording_id = 1;
            model->state = CAT_STATE_RECORDING;
            model->has_transcript = false;
            model->has_answer = false;
            actions = CAT_ACTION_CLEAR_TEXT | CAT_ACTION_START_RECORDING;
        } else if (event == CAT_EVENT_UP_LONG) {
            actions = CAT_ACTION_NEW_CONVERSATION | CAT_ACTION_CLEAR_TEXT;
            model->has_transcript = false;
            model->has_answer = false;
            model->state = CAT_STATE_NEW_CONVERSATION;
        }
        break;
    case CAT_STATE_RECORDING:
        if (event == CAT_EVENT_OK_UP || event == CAT_EVENT_RECORD_LIMIT) {
            model->state = CAT_STATE_TRANSCRIBING;
            actions = CAT_ACTION_STOP_RECORDING;
        } else if (event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_IDLE;
            actions = CAT_ACTION_CANCEL_RECORDING;
        }
        break;
    case CAT_STATE_TRANSCRIBING:
        if (event == CAT_EVENT_RECORD_TOO_SHORT) {
            model->state = CAT_STATE_RECORD_TOO_SHORT;
        } else if (event == CAT_EVENT_ASR_RESULT) {
            model->state = CAT_STATE_TRANSCRIPT_REVIEW;
            model->has_transcript = true;
        } else if (event == CAT_EVENT_ASR_ERROR) {
            model->state = CAT_STATE_ERROR;
            model->error_return_state = CAT_STATE_IDLE;
        } else if (event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_IDLE;
            actions = CAT_ACTION_CANCEL_TURN;
        }
        break;
    case CAT_STATE_TRANSCRIPT_REVIEW:
        if (event == CAT_EVENT_OK_DOWN) {
            model->state = CAT_STATE_AI_WAITING;
            actions = CAT_ACTION_CONFIRM_TRANSCRIPT;
        } else if (event == CAT_EVENT_UP_LONG) {
            model->state = CAT_STATE_IDLE;
            actions = CAT_ACTION_REQUEST_RERECORD | CAT_ACTION_CLEAR_TEXT;
        } else if (event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_IDLE;
            actions = CAT_ACTION_CANCEL_TURN | CAT_ACTION_CLEAR_TEXT;
        }
        break;
    case CAT_STATE_AI_WAITING:
        if (event == CAT_EVENT_CHAT_STARTED || event == CAT_EVENT_CHAT_DELTA) {
            model->state = CAT_STATE_AI_STREAMING;
            model->has_answer = event == CAT_EVENT_CHAT_DELTA;
        } else if (event == CAT_EVENT_CHAT_ERROR) {
            model->state = CAT_STATE_ERROR;
            model->error_return_state = CAT_STATE_TRANSCRIPT_REVIEW;
        } else if (event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_TRANSCRIPT_REVIEW;
            actions = CAT_ACTION_CANCEL_TURN;
        }
        break;
    case CAT_STATE_AI_STREAMING:
        if (event == CAT_EVENT_CHAT_DELTA) {
            model->has_answer = true;
        } else if (event == CAT_EVENT_CHAT_DONE) {
            model->state = CAT_STATE_ANSWER_READY;
        } else if (event == CAT_EVENT_CHAT_ERROR) {
            model->state = CAT_STATE_ERROR;
            model->error_return_state = CAT_STATE_ANSWER_READY;
        } else if (event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_ANSWER_READY;
            actions = CAT_ACTION_CANCEL_TURN;
        }
        break;
    case CAT_STATE_ERROR:
        if (event == CAT_EVENT_OK_DOWN || event == CAT_EVENT_DOWN_LONG) {
            model->state = model->error_return_state;
        } else if (event == CAT_EVENT_UP_LONG) {
            model->state = CAT_STATE_IDLE;
            actions = CAT_ACTION_CLEAR_TEXT;
        }
        break;
    case CAT_STATE_RECORD_TOO_SHORT:
        if (event == CAT_EVENT_OK_DOWN) {
            model->recording_id++;
            if (model->recording_id == 0) model->recording_id = 1;
            model->state = CAT_STATE_RECORDING;
            actions = CAT_ACTION_START_RECORDING;
        } else if (event == CAT_EVENT_NOTICE_DONE || event == CAT_EVENT_DOWN_LONG) {
            model->state = CAT_STATE_IDLE;
        }
        break;
    case CAT_STATE_NEW_CONVERSATION:
        if (event == CAT_EVENT_OK_DOWN) {
            model->recording_id++;
            if (model->recording_id == 0) model->recording_id = 1;
            model->state = CAT_STATE_RECORDING;
            actions = CAT_ACTION_START_RECORDING;
        } else if (event == CAT_EVENT_NOTICE_DONE) {
            model->state = CAT_STATE_IDLE;
        }
        break;
    default:
        break;
    }
    return actions | CAT_ACTION_REFRESH_UI;
}
