#pragma once
#include <arm_neon.h>
#include <stdint.h>
static uint16x8_t resize_row(const uint8_t *p) {
    const uint8x16x2_t table = {{vld1q_u8(p), vld1q_u8(p + 9)}};
    const uint8_t i0[8] = {0, 3, 6, 9, 12, 15, 25, 28}, i1[8] = {1, 4, 7, 10, 13, 23, 26, 29};
    const uint8_t i2[8] = {2, 5, 8, 11, 14, 24, 27, 30}, i3[8] = {3, 6, 9, 12, 15, 25, 28, 31};
    const uint8_t first[8] = {8, 7, 6, 5, 4, 3, 2, 1}, last[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    uint16x8_t sum = vmull_u8(vqtbl2_u8(table, vld1_u8(i0)), vld1_u8(first));
    sum = vmlal_u8(sum, vqtbl2_u8(table, vld1_u8(i1)), vdup_n_u8(8));
    sum = vmlal_u8(sum, vqtbl2_u8(table, vld1_u8(i2)), vdup_n_u8(8));
    return vmlal_u8(sum, vqtbl2_u8(table, vld1_u8(i3)), vld1_u8(last));
}
static uint16x4_t resize_round(uint32x4_t sum) {
    return vmovn_u32(vreinterpretq_u32_s32(
        vqdmulhq_n_s32(vreinterpretq_s32_u32(vaddq_u32(sum, vdupq_n_u32(312))), 3435974)));
}
static void resize_neon(const uint8_t *strip, uint8_t *out, const uint8_t *lookup) {
    for (unsigned camera = 0; camera < 5; camera++)
        for (unsigned y = 0; y < 128; y++) {
            unsigned sy = y * 25 / 8, top = 8 - (y * 25 % 8), bottom = 9 - top;
            for (unsigned bx = 0; bx < 16; bx++) {
                const uint8_t *p = strip + sy * 2000 + camera * 400 + bx * 25;
                uint16x8_t a = resize_row(p), b = resize_row(p + 2000), c = resize_row(p + 4000),
                           d = resize_row(p + 6000);
                uint32x4_t lo = vmull_n_u16(vget_low_u16(a), (uint16_t)top),
                           hi = vmull_n_u16(vget_high_u16(a), (uint16_t)top);
                lo = vmlal_n_u16(lo, vget_low_u16(b), 8);
                hi = vmlal_n_u16(hi, vget_high_u16(b), 8);
                lo = vmlal_n_u16(lo, vget_low_u16(c), 8);
                hi = vmlal_n_u16(hi, vget_high_u16(c), 8);
                lo = vmlal_n_u16(lo, vget_low_u16(d), (uint16_t)bottom);
                hi = vmlal_n_u16(hi, vget_high_u16(d), (uint16_t)bottom);
                uint8_t pixels[8];
                vst1_u8(pixels, vmovn_u16(vcombine_u16(resize_round(lo), resize_round(hi))));
                for (unsigned x = 0; x < 8; x++)
                    out[camera * 16384 + y * 128 + bx * 8 + x] = lookup[pixels[x]];
            }
        }
}
