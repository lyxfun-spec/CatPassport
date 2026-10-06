#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"

/* UUIDs are encoded in NimBLE's little-endian byte order. */
#define CAT_BLE_SERVICE_UUID_BYTES \
    0x6d,0x8f,0x4e,0x5b,0x33,0xc1,0x47,0xa0,0x91,0x1b,0x71,0xca,0x00,0x00,0xf0,0x10
#define CAT_BLE_CONTROL_RX_UUID_BYTES \
    0x6d,0x8f,0x4e,0x5b,0x33,0xc1,0x47,0xa0,0x91,0x1b,0x71,0xca,0x01,0x00,0xf0,0x10
#define CAT_BLE_CONTROL_TX_UUID_BYTES \
    0x6d,0x8f,0x4e,0x5b,0x33,0xc1,0x47,0xa0,0x91,0x1b,0x71,0xca,0x02,0x00,0xf0,0x10
#define CAT_BLE_AUDIO_TX_UUID_BYTES \
    0x6d,0x8f,0x4e,0x5b,0x33,0xc1,0x47,0xa0,0x91,0x1b,0x71,0xca,0x03,0x00,0xf0,0x10

typedef enum {
    CAT_BLE_EVENT_READY,
    CAT_BLE_EVENT_DISCONNECTED,
    CAT_BLE_EVENT_CONTROL_FRAGMENT,
} cat_ble_event_type_t;

typedef struct {
    cat_ble_event_type_t type;
    const uint8_t *data;
    size_t length;
} cat_ble_event_t;

/* Callback storage is borrowed and valid only for the duration of the call. */
typedef void (*cat_ble_event_cb_t)(const cat_ble_event_t *event, void *context);

esp_err_t cat_ble_init(cat_ble_event_cb_t callback, void *context);
esp_err_t cat_ble_send_control(uint8_t type, uint16_t message_id,
                               const uint8_t *payload, size_t length);
esp_err_t cat_ble_send_audio(const uint8_t *frame, size_t length);
bool cat_ble_ready(void);

