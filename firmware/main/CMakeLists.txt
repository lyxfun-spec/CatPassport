idf_component_register(
    SRCS "main.c"
         "cat_adpcm.c"
         "cat_ble.c"
         "cat_clock.c"
         "cat_font_16.c"
         "cat_markdown.c"
         "cat_protocol.c"
         "cat_scroll.c"
         "cat_screen.c"
         "cat_sprite_assets.c"
         "cat_state.c"
         "cat_ui.c"
    INCLUDE_DIRS "."
    REQUIRES bsp bt esp_timer nvs_flash
)
