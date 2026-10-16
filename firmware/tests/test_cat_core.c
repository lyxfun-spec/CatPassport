#include <assert.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cat_adpcm.h"
#include "cat_clock.h"
#include "cat_protocol.h"
#include "cat_scroll.h"
#include "cat_screen.h"
#include "cat_state.h"

static void test_protocol(void)
{
    static const uint8_t text[] = "\xe5\xb0\x8f\xe7\x8c\xab assistant";
    uint8_t storage[64];
    uint8_t packet[24];
    cat_protocol_reassembler_t state;
    cat_protocol_message_t message = {0};
    cat_protocol_reassembler_init(&state, storage, sizeof(storage));

    size_t first = cat_protocol_write_fragment(packet, sizeof(packet), CAT_MSG_ASR_RESULT,
                                               7, 0, text, 8, sizeof(text) - 1);
    assert(first == CAT_CONTROL_HEADER_SIZE + 8);
    assert(cat_protocol_receive(&state, packet, first, &message) == CAT_PROTOCOL_INCOMPLETE);
    size_t second = cat_protocol_write_fragment(packet, sizeof(packet), CAT_MSG_ASR_RESULT,
                                                7, 8, text, sizeof(text) - 1 - 8,
                                                sizeof(text) - 1);
    assert(cat_protocol_receive(&state, packet, second, &message) == CAT_PROTOCOL_COMPLETE);
    assert(message.type == CAT_MSG_ASR_RESULT && message.message_id == 7);
    assert(message.payload_length == sizeof(text) - 1);
    assert(memcmp(message.payload, text, sizeof(text) - 1) == 0);
    assert(cat_protocol_utf8_valid(text, sizeof(text) - 1));
    assert(cat_protocol_utf8_prefix(text, sizeof(text) - 1, 4) == 3);

    packet[6] = 9;
    assert(cat_protocol_receive(&state, packet, second, &message) == CAT_PROTOCOL_ERROR);
    static const uint8_t invalid[] = {0xe4, 0xb8};
    assert(!cat_protocol_utf8_valid(invalid, sizeof(invalid)));
}

static void test_clock(void)
{
    cat_clock_t clock;
    cat_clock_init(&clock);
    assert(cat_clock_minute_of_day(&clock, 1000) == -1);
    assert(!cat_clock_sync(&clock, 86400, 1000));
    assert(cat_clock_sync(&clock, 23 * 3600 + 59 * 60 + 30, 2000));
    assert(cat_clock_minute_of_day(&clock, 2000) == 23 * 60 + 59);
    assert(cat_clock_minute_of_day(&clock, 32000) == 0);
    assert(cat_clock_minute_of_day(&clock, 92000) == 1);
}

static void test_screen_timeout(void)
{
    cat_screen_t screen;
    cat_screen_init(&screen, 60, 1000);
    assert(!cat_screen_update(&screen, 60999, false));
    assert(cat_screen_update(&screen, 61000, false));
    assert(cat_screen_is_off(&screen));

    cat_screen_note_activity(&screen, 62000);
    assert(!cat_screen_is_off(&screen));
    assert(!cat_screen_update(&screen, 200000, true));
    assert(!cat_screen_update(&screen, 259999, false));
    assert(cat_screen_update(&screen, 260000, false));

    assert(cat_screen_set_timeout(&screen, 0, 300000));
    assert(!cat_screen_update(&screen, UINT64_MAX, false));
    assert(!cat_screen_is_off(&screen));
    assert(!cat_screen_set_timeout(&screen, CAT_SCREEN_TIMEOUT_MAX_SECONDS + 1U, 0));
}

static void test_adpcm(void)
{
    enum { SAMPLES = 320 };
    int16_t input[SAMPLES];
    int16_t decoded[SAMPLES];
    uint8_t encoded[160];
    for (int i = 0; i < SAMPLES; ++i) {
        input[i] = (int16_t)(sin((double)i * 0.11) * 12000.0);
    }
    cat_adpcm_state_t state = {0};
    int16_t predictor;
    uint8_t index;
    size_t bytes = cat_adpcm_encode(input, SAMPLES, &state, &predictor, &index,
                                    encoded, sizeof(encoded));
    assert(bytes == 160);
    assert(cat_adpcm_decode(predictor, index, encoded, bytes, decoded, SAMPLES) == SAMPLES);
    long long absolute_error = 0;
    for (int i = 0; i < SAMPLES; ++i) absolute_error += llabs((long long)input[i] - decoded[i]);
    assert(absolute_error / SAMPLES < 1600);
}

static void test_state(void)
{
    cat_app_model_t model;
    cat_state_init(&model);
    assert(model.state == CAT_STATE_DISCONNECTED);
    assert(cat_state_apply(&model, CAT_EVENT_OK_DOWN) == CAT_ACTION_NONE);
    cat_state_apply(&model, CAT_EVENT_BLE_CONNECTED);
    assert(model.state == CAT_STATE_IDLE);
    uint32_t actions = cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    assert(model.state == CAT_STATE_RECORDING && !model.recording_active);
    assert(actions & CAT_ACTION_START_RECORDING);
    actions = cat_state_apply(&model, CAT_EVENT_OK_UP);
    assert(model.state == CAT_STATE_IDLE && (actions & CAT_ACTION_CANCEL_RECORDING));

    cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    actions = cat_state_apply(&model, CAT_EVENT_RECORD_ACTIVATE);
    assert(model.state == CAT_STATE_RECORDING && model.recording_active);
    assert(actions & CAT_ACTION_ACTIVATE_RECORDING);
    actions = cat_state_apply(&model, CAT_EVENT_OK_UP);
    assert(model.state == CAT_STATE_TRANSCRIBING && (actions & CAT_ACTION_STOP_RECORDING));
    cat_state_apply(&model, CAT_EVENT_ASR_RESULT);
    assert(model.state == CAT_STATE_TRANSCRIPT_REVIEW && model.has_transcript);
    const uint16_t transcript_id = model.recording_id;
    actions = cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    assert(model.state == CAT_STATE_RECORDING && (actions & CAT_ACTION_START_RECORDING));
    assert(model.recording_id == transcript_id);
    assert(model.armed_recording_id != transcript_id);
    actions = cat_state_apply(&model, CAT_EVENT_OK_UP);
    assert(model.state == CAT_STATE_AI_WAITING && (actions & CAT_ACTION_CONFIRM_TRANSCRIPT));
    assert(model.recording_id == transcript_id);
    cat_state_apply(&model, CAT_EVENT_CHAT_DELTA);
    assert(model.state == CAT_STATE_AI_STREAMING && model.has_answer);
    cat_state_apply(&model, CAT_EVENT_CHAT_DONE);
    assert(model.state == CAT_STATE_ANSWER_READY);
    actions = cat_state_apply(&model, CAT_EVENT_SESSION_RESET);
    assert(model.state == CAT_STATE_IDLE && (actions & CAT_ACTION_CLEAR_TEXT));
    cat_state_apply(&model, CAT_EVENT_BLE_DISCONNECTED);
    assert(model.state == CAT_STATE_DISCONNECTED && !model.has_answer);
}

static void test_button_interactions(void)
{
    assert(cat_recording_is_too_short(0));
    assert(cat_recording_is_too_short(14));
    assert(!cat_recording_is_too_short(15));

    cat_app_model_t model;
    cat_state_init(&model);
    cat_state_apply(&model, CAT_EVENT_BLE_CONNECTED);

    uint32_t actions = cat_state_apply(&model, CAT_EVENT_UP_LONG);
    assert(model.state == CAT_STATE_NEW_CONVERSATION);
    assert(actions & CAT_ACTION_NEW_CONVERSATION);
    cat_state_apply(&model, CAT_EVENT_SESSION_RESET);
    assert(model.state == CAT_STATE_NEW_CONVERSATION);
    cat_state_apply(&model, CAT_EVENT_NOTICE_DONE);
    assert(model.state == CAT_STATE_IDLE);

    cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    const uint16_t previous_recording_id = model.recording_id;
    assert(model.armed_recording_id != previous_recording_id);
    cat_state_apply(&model, CAT_EVENT_RECORD_ACTIVATE);
    assert(model.recording_id == model.armed_recording_id);
    assert(model.recording_id != previous_recording_id);
    cat_state_apply(&model, CAT_EVENT_OK_UP);
    cat_state_apply(&model, CAT_EVENT_ASR_RESULT);
    actions = cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    assert(model.state == CAT_STATE_RECORDING &&
           model.recording_origin_state == CAT_STATE_TRANSCRIPT_REVIEW);
    actions = cat_state_apply(&model, CAT_EVENT_RECORD_ACTIVATE);
    assert(actions & CAT_ACTION_REQUEST_RERECORD);
    assert(actions & CAT_ACTION_ACTIVATE_RECORDING);
    cat_state_apply(&model, CAT_EVENT_OK_UP);
    assert(model.state == CAT_STATE_TRANSCRIBING);

    cat_state_apply(&model, CAT_EVENT_ASR_RESULT);
    cat_state_apply(&model, CAT_EVENT_OK_DOWN); /* 短按确认识别文字。 */
    cat_state_apply(&model, CAT_EVENT_OK_UP);
    cat_state_apply(&model, CAT_EVENT_CHAT_DELTA);
    actions = cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    assert(model.state == CAT_STATE_ANSWER_READY && (actions & CAT_ACTION_CANCEL_TURN));

    model.has_answer = true;
    cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    actions = cat_state_apply(&model, CAT_EVENT_OK_UP);
    assert(model.state == CAT_STATE_IDLE && model.has_answer);
    assert(actions & CAT_ACTION_CANCEL_RECORDING);

    model.state = CAT_STATE_ANSWER_READY;
    cat_state_apply(&model, CAT_EVENT_OK_DOWN);
    cat_state_apply(&model, CAT_EVENT_RECORD_ACTIVATE);
    cat_state_apply(&model, CAT_EVENT_OK_UP);
    cat_state_apply(&model, CAT_EVENT_RECORD_TOO_SHORT);
    assert(model.state == CAT_STATE_ANSWER_READY);
}

static void test_scroll_motion(void)
{
    cat_scroll_motion_t motion;
    cat_scroll_init(&motion);
    assert(!cat_scroll_is_active(&motion));

    cat_scroll_press(&motion, 1);
    assert(cat_scroll_is_active(&motion));
    assert(cat_scroll_update(&motion, 16) > 0);

    int32_t held_distance = 0;
    for (int i = 0; i < 50; ++i) held_distance += cat_scroll_update(&motion, 20);
    assert(held_distance > 180 && held_distance < 350);

    cat_scroll_press(&motion, -1);
    assert(cat_scroll_update(&motion, 16) < 0);
    cat_scroll_release(&motion, -1);
    for (int i = 0; i < 100 && cat_scroll_is_active(&motion); ++i)
        (void)cat_scroll_update(&motion, 20);
    assert(!cat_scroll_is_active(&motion));
}

int main(void)
{
    test_protocol();
    test_clock();
    test_screen_timeout();
    test_adpcm();
    test_state();
    test_button_interactions();
    test_scroll_motion();
    puts("Cat assistant core tests: PASS");
    return 0;
}
