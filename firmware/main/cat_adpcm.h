#pragma once

#include <stddef.h>
#include <stdint.h>

typedef struct {
    int step_index;
} cat_adpcm_state_t;

/* 最大输出为 ceil((sample_count - 1) / 2)。返回实际字节数，失败返回 0。 */
size_t cat_adpcm_encode(const int16_t *pcm, size_t sample_count,
                        cat_adpcm_state_t *state, int16_t *initial_predictor,
                        uint8_t *initial_index, uint8_t *output, size_t output_size);

/* 解码一个独立帧。返回写入采样数，参数或负载长度不匹配时返回 0。 */
size_t cat_adpcm_decode(int16_t initial_predictor, uint8_t initial_index,
                        const uint8_t *input, size_t input_size,
                        int16_t *pcm, size_t sample_count);

