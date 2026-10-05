#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct NativeTracking NativeTracking;
typedef struct {
    uint64_t face_time, eye_time;
    uint32_t face_sequence, eye_sequence, flags, reserved;
    float expressions[70], eye_orientation[8];
} NativeSample;
NativeTracking *native_tracking_open(void);
void native_tracking_read(NativeTracking *source, uint64_t now, NativeSample *sample);
void native_tracking_close(NativeTracking *source);
#ifdef __cplusplus
}
#endif
