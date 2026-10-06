idf_component_register(
    SRCS "main.c"
         "cat_adpcm.c"
         "cat_ble.c"
         "cat_font_16.c"
         "cat_protocol.c"
         "cat_state.c"
         "cat_ui.c"
    INCLUDE_DIRS "."
    REQUIRES bsp bt esp_timer nvs_flash
)
