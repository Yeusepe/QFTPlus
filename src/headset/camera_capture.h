#pragma once
#include <stdint.h>
#include <sys/types.h>

typedef struct CameraCapture CameraCapture;
CameraCapture *camera_capture_open(const char *token, pid_t provider);
int camera_capture_read(CameraCapture *capture, uint8_t *frame, uint64_t *sequence, uint64_t *timestamp);
void camera_capture_close(CameraCapture *capture);
