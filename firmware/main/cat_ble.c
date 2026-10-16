#include "cat_ble.h"

#include <string.h>

#include "cat_protocol.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "host/ble_att.h"
#include "host/ble_gap.h"
#include "host/ble_gatt.h"
#include "host/ble_hs.h"
#include "host/ble_hs_mbuf.h"
#include "host/ble_store.h"
#include "host/util/util.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "os/os_mbuf.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

#define CAT_BLE_NAME "FoloToy Cat"
#define CAT_BLE_TX_MAX 244U
#define CAT_BLE_AUDIO_ATT_MTU 177U

static const char *TAG = "cat_ble";
static const ble_uuid128_t SERVICE_UUID = BLE_UUID128_INIT(CAT_BLE_SERVICE_UUID_BYTES);
static const ble_uuid128_t CONTROL_RX_UUID = BLE_UUID128_INIT(CAT_BLE_CONTROL_RX_UUID_BYTES);
static const ble_uuid128_t CONTROL_TX_UUID = BLE_UUID128_INIT(CAT_BLE_CONTROL_TX_UUID_BYTES);
static const ble_uuid128_t AUDIO_TX_UUID = BLE_UUID128_INIT(CAT_BLE_AUDIO_TX_UUID_BYTES);

static SemaphoreHandle_t s_lock;
static cat_ble_event_cb_t s_callback;
static void *s_context;
static uint16_t s_connection = BLE_HS_CONN_HANDLE_NONE;
static uint16_t s_control_handle;
static uint16_t s_audio_handle;
static uint8_t s_address_type;
static bool s_encrypted;
static bool s_control_subscribed;
static bool s_audio_subscribed;
static bool s_ready_emitted;
static int64_t s_connected_at_us;
static uint8_t s_tx[CAT_BLE_TX_MAX];

static bool emit(cat_ble_event_type_t type, const uint8_t *data, size_t length)
{
    if (s_callback != NULL) {
        const cat_ble_event_t event = {.type = type, .data = data, .length = length};
        return s_callback(&event, s_context);
    }
    return false;
}

static bool link_ready_locked(void)
{
    return s_connection != BLE_HS_CONN_HANDLE_NONE && s_encrypted &&
           s_control_subscribed && s_audio_subscribed &&
           ble_att_mtu(s_connection) >= CAT_BLE_AUDIO_ATT_MTU;
}

bool cat_ble_ready(void)
{
    if (s_lock == NULL) return false;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const bool ready = link_ready_locked();
    xSemaphoreGive(s_lock);
    return ready;
}

static int gatt_access(uint16_t connection, uint16_t attribute,
                       struct ble_gatt_access_ctxt *context, void *argument)
{
    (void)attribute;
    (void)argument;
    if (context->op != BLE_GATT_ACCESS_OP_WRITE_CHR) return BLE_ATT_ERR_UNLIKELY;

    if (xSemaphoreTake(s_lock, 0) != pdTRUE) return BLE_ATT_ERR_INSUFFICIENT_RES;
    const bool allowed = connection == s_connection && s_encrypted;
    xSemaphoreGive(s_lock);
    if (!allowed) return BLE_ATT_ERR_INSUFFICIENT_ENC;

    uint8_t value[CAT_BLE_TX_MAX];
    const uint16_t length = OS_MBUF_PKTLEN(context->om);
    if (length > sizeof(value) || os_mbuf_copydata(context->om, 0, length, value) != 0) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    if (!emit(CAT_BLE_EVENT_CONTROL_FRAGMENT, value, length))
        return BLE_ATT_ERR_INSUFFICIENT_RES;
    return 0;
}

static const struct ble_gatt_svc_def SERVICES[] = {{
    .type = BLE_GATT_SVC_TYPE_PRIMARY,
    .uuid = &SERVICE_UUID.u,
    .characteristics = (struct ble_gatt_chr_def[]) {
        {.uuid = &CONTROL_RX_UUID.u, .access_cb = gatt_access,
         .flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP | BLE_GATT_CHR_F_WRITE_ENC},
        {.uuid = &CONTROL_TX_UUID.u, .access_cb = gatt_access,
         .flags = BLE_GATT_CHR_F_NOTIFY | BLE_GATT_CHR_F_READ_ENC,
         .val_handle = &s_control_handle},
        {.uuid = &AUDIO_TX_UUID.u, .access_cb = gatt_access,
         .flags = BLE_GATT_CHR_F_NOTIFY | BLE_GATT_CHR_F_READ_ENC,
         .val_handle = &s_audio_handle},
        {0},
    },
}, {0}};

static int advertise(void);

static int gap_event(struct ble_gap_event *event, void *argument)
{
    (void)argument;
    switch (event->type) {
    case BLE_GAP_EVENT_CONNECT:
        ESP_LOGI(TAG, "gap connect status=%d handle=%u", event->connect.status,
                 (unsigned)event->connect.conn_handle);
        if (event->connect.status == 0) {
            xSemaphoreTake(s_lock, portMAX_DELAY);
            s_connection = event->connect.conn_handle;
            s_encrypted = false;
            s_control_subscribed = false;
            s_audio_subscribed = false;
            s_ready_emitted = false;
            s_connected_at_us = esp_timer_get_time();
            xSemaphoreGive(s_lock);
            /* The Windows central owns security negotiation.  Starting encryption
             * here with a retained peripheral bond races a fresh Windows pairing:
             * after Windows has forgotten its key it rejects our old-key request
             * and disconnects before it can send a Pairing Request.  Waiting for
             * the central lets normal reconnects restore their bond and lets the
             * repeat-pairing event below replace a stale peripheral bond. */
            ESP_LOGI(TAG, "waiting for central security handle=%u",
                     (unsigned)event->connect.conn_handle);
        } else {
            const int advertise_result = advertise();
            if (advertise_result != 0) ESP_LOGE(TAG, "advertising restart failed: %d", advertise_result);
        }
        return 0;
    case BLE_GAP_EVENT_DISCONNECT:
        xSemaphoreTake(s_lock, portMAX_DELAY);
        const bool encrypted_before_disconnect = s_encrypted;
        const int64_t connected_for_us = esp_timer_get_time() - s_connected_at_us;
        xSemaphoreGive(s_lock);
        const bool stale_bond_rejected =
            !encrypted_before_disconnect &&
            connected_for_us >= 0 && connected_for_us < 3000000 &&
            event->disconnect.reason == BLE_HS_HCI_ERR(BLE_ERR_REM_USER_CONN_TERM);
        int stale_bond_delete_result = BLE_HS_ENOENT;
        if (stale_bond_rejected) {
            /* Windows can forget its key while the card still retains the peer
             * bond.  In that state Windows terminates almost immediately, before
             * sending a new SMP Pairing Request, so REPEAT_PAIRING cannot run.
             * Remove only this peer's record; the desktop retries transparently. */
            stale_bond_delete_result =
                ble_store_util_delete_peer(&event->disconnect.conn.peer_id_addr);
        }
        ESP_LOGI(TAG,
                 "gap disconnect handle=%u reason=%d encrypted=%d duration_ms=%lld stale_delete=%d",
                 (unsigned)event->disconnect.conn.conn_handle, event->disconnect.reason,
                 encrypted_before_disconnect, (long long)(connected_for_us / 1000),
                 stale_bond_delete_result);
        xSemaphoreTake(s_lock, portMAX_DELAY);
        s_connection = BLE_HS_CONN_HANDLE_NONE;
        s_encrypted = false;
        s_control_subscribed = false;
        s_audio_subscribed = false;
        s_ready_emitted = false;
        s_connected_at_us = 0;
        xSemaphoreGive(s_lock);
        (void)emit(CAT_BLE_EVENT_DISCONNECTED, NULL, 0);
        const int advertise_result = advertise();
        if (advertise_result != 0) ESP_LOGE(TAG, "advertising restart failed: %d", advertise_result);
        return 0;
    case BLE_GAP_EVENT_ENC_CHANGE: {
        struct ble_gap_conn_desc descriptor;
        const int find_result = ble_gap_conn_find(event->enc_change.conn_handle, &descriptor);
        const bool encrypted = event->enc_change.status == 0 && find_result == 0 &&
                               descriptor.sec_state.encrypted;
        xSemaphoreTake(s_lock, portMAX_DELAY);
        s_encrypted = encrypted;
        const bool ready = link_ready_locked();
        const bool emit_ready = ready && !s_ready_emitted;
        s_ready_emitted = ready;
        xSemaphoreGive(s_lock);
        ESP_LOGI(TAG,
                 "encryption change handle=%u status=%d find=%d encrypted=%d bonded=%d ready=%d",
                 (unsigned)event->enc_change.conn_handle, event->enc_change.status, find_result,
                 encrypted, find_result == 0 ? descriptor.sec_state.bonded : 0, ready);
        if (emit_ready) (void)emit(CAT_BLE_EVENT_READY, NULL, 0);
        if (!encrypted && event->enc_change.status != 0)
            (void)ble_gap_terminate(event->enc_change.conn_handle, BLE_ERR_REM_USER_CONN_TERM);
        return 0;
    }
    case BLE_GAP_EVENT_SUBSCRIBE:
        xSemaphoreTake(s_lock, portMAX_DELAY);
        bool lost_subscription = false;
        if (event->subscribe.attr_handle == s_control_handle) {
            lost_subscription = s_control_subscribed && !event->subscribe.cur_notify;
            s_control_subscribed = event->subscribe.cur_notify;
        } else if (event->subscribe.attr_handle == s_audio_handle) {
            lost_subscription = s_audio_subscribed && !event->subscribe.cur_notify;
            s_audio_subscribed = event->subscribe.cur_notify;
        }
        const bool ready = link_ready_locked();
        const bool emit_ready = ready && !s_ready_emitted;
        s_ready_emitted = ready;
        const bool control = s_control_subscribed;
        const bool audio = s_audio_subscribed;
        const bool encrypted = s_encrypted;
        const uint16_t mtu = s_connection == BLE_HS_CONN_HANDLE_NONE ? 0 : ble_att_mtu(s_connection);
        xSemaphoreGive(s_lock);
        ESP_LOGI(TAG,
                 "subscribe handle=%u attr=%u reason=%u notify=%u control=%d audio=%d encrypted=%d mtu=%u ready=%d",
                 (unsigned)event->subscribe.conn_handle, (unsigned)event->subscribe.attr_handle,
                 (unsigned)event->subscribe.reason, (unsigned)event->subscribe.cur_notify,
                 control, audio, encrypted, (unsigned)mtu, ready);
        if (emit_ready) (void)emit(CAT_BLE_EVENT_READY, NULL, 0);
        if (lost_subscription && encrypted)
            (void)ble_gap_terminate(event->subscribe.conn_handle, BLE_ERR_REM_USER_CONN_TERM);
        return 0;
    case BLE_GAP_EVENT_MTU: {
        xSemaphoreTake(s_lock, portMAX_DELAY);
        const bool ready = link_ready_locked();
        const bool emit_ready = ready && !s_ready_emitted;
        s_ready_emitted = ready;
        xSemaphoreGive(s_lock);
        ESP_LOGI(TAG, "mtu handle=%u channel=%u value=%u ready=%d",
                 (unsigned)event->mtu.conn_handle, (unsigned)event->mtu.channel_id,
                 (unsigned)event->mtu.value, ready);
        if (emit_ready) (void)emit(CAT_BLE_EVENT_READY, NULL, 0);
        return 0;
    }
    case BLE_GAP_EVENT_REPEAT_PAIRING: {
        struct ble_gap_conn_desc descriptor;
        const int find_result = ble_gap_conn_find(event->repeat_pairing.conn_handle, &descriptor);
        const int delete_result = find_result == 0
                                      ? ble_store_util_delete_peer(&descriptor.peer_id_addr)
                                      : find_result;
        ESP_LOGI(TAG, "repeat pairing handle=%u find=%d delete=%d",
                 (unsigned)event->repeat_pairing.conn_handle, find_result, delete_result);
        return BLE_GAP_REPEAT_PAIRING_RETRY;
    }
    case BLE_GAP_EVENT_ADV_COMPLETE: {
        xSemaphoreTake(s_lock, portMAX_DELAY);
        const bool disconnected = s_connection == BLE_HS_CONN_HANDLE_NONE;
        xSemaphoreGive(s_lock);
        if (disconnected) {
            const int result = advertise();
            if (result != 0) ESP_LOGE(TAG, "advertising completion recovery failed: %d", result);
        }
        return 0;
    }
    default:
        return 0;
    }
}

static int advertise(void)
{
    struct ble_hs_adv_fields fields = {0};
    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    fields.uuids128 = (ble_uuid128_t *)&SERVICE_UUID;
    fields.num_uuids128 = 1;
    fields.uuids128_is_complete = 1;
    int result = ble_gap_adv_set_fields(&fields);
    if (result != 0) return result;

    struct ble_hs_adv_fields response = {0};
    response.name = (uint8_t *)CAT_BLE_NAME;
    response.name_len = strlen(CAT_BLE_NAME);
    response.name_is_complete = 1;
    result = ble_gap_adv_rsp_set_fields(&response);
    if (result != 0) return result;

    struct ble_gap_adv_params parameters = {0};
    parameters.conn_mode = BLE_GAP_CONN_MODE_UND;
    parameters.disc_mode = BLE_GAP_DISC_MODE_GEN;
    return ble_gap_adv_start(s_address_type, NULL, BLE_HS_FOREVER, &parameters, gap_event, NULL);
}

static void on_sync(void)
{
    int result = ble_hs_util_ensure_addr(0);
    if (result == 0) result = ble_hs_id_infer_auto(0, &s_address_type);
    if (result == 0) result = advertise();
    if (result != 0) ESP_LOGE(TAG, "NimBLE sync/advertising failed: %d", result);
}

static void on_reset(int reason)
{
    ESP_LOGE(TAG, "NimBLE host reset: %d", reason);
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const bool was_connected = s_connection != BLE_HS_CONN_HANDLE_NONE;
    s_connection = BLE_HS_CONN_HANDLE_NONE;
    s_encrypted = false;
    s_control_subscribed = false;
    s_audio_subscribed = false;
    s_ready_emitted = false;
    xSemaphoreGive(s_lock);
    if (was_connected) (void)emit(CAT_BLE_EVENT_DISCONNECTED, NULL, 0);
}

static void host_task(void *argument)
{
    (void)argument;
    nimble_port_run();
    nimble_port_freertos_deinit();
}

/* Provided by ESP-IDF's NimBLE port. */
void ble_store_config_init(void);

esp_err_t cat_ble_init(cat_ble_event_cb_t callback, void *context)
{
    if (callback == NULL) return ESP_ERR_INVALID_ARG;
    s_lock = xSemaphoreCreateMutex();
    if (s_lock == NULL) return ESP_ERR_NO_MEM;
    s_callback = callback;
    s_context = context;
    int result = nimble_port_init();
    if (result != ESP_OK) return result;
    ble_svc_gap_init();
    ble_svc_gatt_init();
    ble_svc_gap_device_name_set(CAT_BLE_NAME);
    result = ble_gatts_count_cfg(SERVICES);
    if (result == 0) result = ble_gatts_add_svcs(SERVICES);
    if (result != 0) return ESP_FAIL;

    ble_hs_cfg.reset_cb = on_reset;
    ble_hs_cfg.sync_cb = on_sync;
    ble_hs_cfg.sm_bonding = 1;
    ble_hs_cfg.sm_sc = 1;
    ble_hs_cfg.sm_mitm = 0;
    ble_hs_cfg.sm_io_cap = BLE_HS_IO_NO_INPUT_OUTPUT;
    ble_hs_cfg.sm_our_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_hs_cfg.sm_their_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_store_config_init();
    nimble_port_freertos_init(host_task);
    return ESP_OK;
}

static esp_err_t notify(uint16_t handle, const uint8_t *data, size_t length)
{
    struct os_mbuf *packet = ble_hs_mbuf_from_flat(data, (uint16_t)length);
    if (packet == NULL) return ESP_ERR_NO_MEM;
    const int result = ble_gatts_notify_custom(s_connection, handle, packet);
    return result == 0 ? ESP_OK : (result == BLE_HS_ENOMEM ? ESP_ERR_NO_MEM : ESP_FAIL);
}

esp_err_t cat_ble_send_control(uint8_t type, uint16_t message_id,
                               const uint8_t *payload, size_t length)
{
    if (length > UINT16_MAX || (payload == NULL && length != 0)) return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    if (!link_ready_locked()) { xSemaphoreGive(s_lock); return ESP_ERR_INVALID_STATE; }
    const size_t mtu_payload = ble_att_mtu(s_connection) - 3U;
    const size_t chunk_max = mtu_payload - CAT_CONTROL_HEADER_SIZE;
    size_t offset = 0;
    do {
        const size_t chunk = (length - offset) < chunk_max ? (length - offset) : chunk_max;
        const size_t packet_length = cat_protocol_write_fragment(
            s_tx, sizeof(s_tx), type, message_id, offset, payload, chunk, length);
        const esp_err_t send_result = packet_length == 0
                                          ? ESP_FAIL
                                          : notify(s_control_handle, s_tx, packet_length);
        if (send_result != ESP_OK) {
            ESP_LOGW(TAG, "control notify failed type=0x%02x offset=%u length=%u err=%s",
                     type, (unsigned)offset, (unsigned)packet_length,
                     esp_err_to_name(send_result));
            xSemaphoreGive(s_lock);
            return send_result;
        }
        offset += chunk;
    } while (offset < length);
    xSemaphoreGive(s_lock);
    return ESP_OK;
}

esp_err_t cat_ble_send_audio(const uint8_t *frame, size_t length)
{
    if (frame == NULL || length == 0) return ESP_ERR_INVALID_ARG;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    if (!link_ready_locked() || length > ble_att_mtu(s_connection) - 3U) {
        xSemaphoreGive(s_lock);
        return ESP_ERR_INVALID_STATE;
    }
    const esp_err_t result = notify(s_audio_handle, frame, length);
    xSemaphoreGive(s_lock);
    return result;
}

esp_err_t cat_ble_disconnect(void)
{
    if (s_lock == NULL) return ESP_ERR_INVALID_STATE;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const uint16_t connection = s_connection;
    xSemaphoreGive(s_lock);
    if (connection == BLE_HS_CONN_HANDLE_NONE) return ESP_ERR_INVALID_STATE;
    const int result = ble_gap_terminate(connection, BLE_ERR_REM_USER_CONN_TERM);
    return result == 0 || result == BLE_HS_EALREADY ? ESP_OK : ESP_FAIL;
}
