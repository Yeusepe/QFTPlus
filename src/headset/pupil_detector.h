#pragma once
#ifdef __cplusplus
extern "C" {
#endif
int pupil_detect(const unsigned char *strip, int eye, const double *range, float out[7]);
#ifdef __cplusplus
}
#endif
