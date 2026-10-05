#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

typedef struct FaceModel FaceModel;
FaceModel *face_model_open(const char *directory);
void face_model_stages(uint64_t out[5]);
int face_model_run(FaceModel *model, const uint8_t *strip, float output[999]);
void face_model_close(FaceModel *model);
#ifdef __cplusplus
}
#endif
