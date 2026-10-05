#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct TrackingModel TrackingModel;
typedef struct {
    uint32_t magic, version, raw_fps, revision;
    uint64_t native_sequence;
    float native_age, native_values[70];
    uint32_t flags;
} TrackingControl;
typedef struct {
    uint32_t version, flags, revision, status, on;
    float expressions[12], shares[4], tongue[3], pupils[2], geometry[14], raw[13], neutral[13];
} TrackingResult;
enum {
    TRACK_EXTRA = 1,
    TRACK_TONGUE = 2,
    TRACK_PUPILS = 4,
    TRACK_CHEEKS = 8,
    TRACK_BROWS = 16,
    TRACK_PUPIL_PREVIEW = 32,
    TRACK_PUPIL_CALIBRATE = 64
};
enum {
    TRACK_NATIVE_FRESH = 1,
    TRACK_SPEAKING = 2,
    TRACK_TONGUE_VISIBLE = 4,
    TRACK_PUPIL_VALID = 8,
    TRACK_PUPIL_UPDATED = 16
};
TrackingModel *tracking_model_open(const char *directory);
int tracking_model_reload(TrackingModel *model, const char *directory);
void tracking_model_control(TrackingModel *model, const TrackingControl *control, double now);
void tracking_model_stages(uint64_t out[6]);
void tracking_model_thumbnails(const uint8_t *strip, float out[512]);
int tracking_model_run(TrackingModel *model, const uint8_t *strip, const float features[999], double now,
                       TrackingResult *result);
void tracking_model_close(TrackingModel *model);
#ifdef __cplusplus
}
#endif
