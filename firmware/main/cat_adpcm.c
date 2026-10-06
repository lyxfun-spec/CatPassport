#include "cat_adpcm.h"

#include <stdbool.h>

static const int s_step_table[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
    598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707,
    1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
    5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
};

static const int s_index_table[16] = {
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8,
};

static int clamp_int(int value, int low, int high)
{
    if (value < low) return low;
    if (value > high) return high;
    return value;
}

static uint8_t encode_sample(int sample, int *predictor, int *index)
{
    const int step = s_step_table[*index];
    int difference = sample - *predictor;
    uint8_t code = 0;
    if (difference < 0) {
        code = 8;
        difference = -difference;
    }

    int delta = step >> 3;
    if (difference >= step) { code |= 4; difference -= step; delta += step; }
    if (difference >= (step >> 1)) { code |= 2; difference -= step >> 1; delta += step >> 1; }
    if (difference >= (step >> 2)) { code |= 1; delta += step >> 2; }
    *predictor += (code & 8) ? -delta : delta;
    *predictor = clamp_int(*predictor, -32768, 32767);
    *index = clamp_int(*index + s_index_table[code], 0, 88);
    return code;
}

static int16_t decode_sample(uint8_t code, int *predictor, int *index)
{
    const int step = s_step_table[*index];
    int delta = step >> 3;
    if (code & 4) delta += step;
    if (code & 2) delta += step >> 1;
    if (code & 1) delta += step >> 2;
    *predictor += (code & 8) ? -delta : delta;
    *predictor = clamp_int(*predictor, -32768, 32767);
    *index = clamp_int(*index + s_index_table[code & 0x0f], 0, 88);
    return (int16_t)*predictor;
}

size_t cat_adpcm_encode(const int16_t *pcm, size_t sample_count,
                        cat_adpcm_state_t *state, int16_t *initial_predictor,
                        uint8_t *initial_index, uint8_t *output, size_t output_size)
{
    if (pcm == NULL || state == NULL || initial_predictor == NULL ||
        initial_index == NULL || output == NULL || sample_count == 0) return 0;
    const size_t required = sample_count > 1 ? (sample_count - 1 + 1) / 2 : 0;
    if (output_size < required) return 0;

    int predictor = pcm[0];
    int index = clamp_int(state->step_index, 0, 88);
    *initial_predictor = pcm[0];
    *initial_index = (uint8_t)index;

    for (size_t sample = 1; sample < sample_count; ++sample) {
        const uint8_t code = encode_sample(pcm[sample], &predictor, &index);
        const size_t byte = (sample - 1) / 2;
        if ((sample & 1U) != 0) output[byte] = code;
        else output[byte] |= (uint8_t)(code << 4);
    }
    state->step_index = index;
    return required;
}

size_t cat_adpcm_decode(int16_t initial_predictor, uint8_t initial_index,
                        const uint8_t *input, size_t input_size,
                        int16_t *pcm, size_t sample_count)
{
    if (pcm == NULL || sample_count == 0 || initial_index > 88 ||
        (sample_count > 1 && input == NULL)) return 0;
    const size_t required = sample_count > 1 ? (sample_count - 1 + 1) / 2 : 0;
    if (input_size != required) return 0;

    int predictor = initial_predictor;
    int index = initial_index;
    pcm[0] = initial_predictor;
    for (size_t sample = 1; sample < sample_count; ++sample) {
        const uint8_t packed = input[(sample - 1) / 2];
        const uint8_t code = (sample & 1U) != 0 ? packed & 0x0f : packed >> 4;
        pcm[sample] = decode_sample(code, &predictor, &index);
    }
    return sample_count;
}

