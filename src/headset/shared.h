#pragma once

#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define SENSOR_WIDTH 2000
#define SENSOR_HEIGHT 400
#define SENSOR_BYTES ((size_t)SENSOR_WIDTH * SENSOR_HEIGHT)

#define SHARED_PATH "/data/local/tmp/questpro-live-v9-shared.bin"
#define SHARED_GENERATION_OFFSET ((size_t)8)
#define SHARED_SEQUENCE_OFFSET ((size_t)16)
#define SHARED_TIMESTAMP_OFFSET ((size_t)24)
#define SHARED_TORN_COUNT_OFFSET ((size_t)56)
#define SHARED_ACTIVE_UNTIL_OFFSET ((size_t)64)
#define SHARED_REQUESTED_MAX_FPS_OFFSET ((size_t)72)
#define SHARED_HEADER_BYTES ((size_t)80)
#define SHARED_BYTES (SHARED_HEADER_BYTES + SENSOR_BYTES)

static inline uint64_t monotonic_nanoseconds(void) {
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (uint64_t)value.tv_sec * UINT64_C(1000000000) + (uint64_t)value.tv_nsec;
}

static inline const char *program_name(pid_t pid, char *command, size_t size) {
    char path[32];
    snprintf(path, sizeof path, "/proc/%d/cmdline", (int)pid);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return NULL;
    ssize_t length = read(fd, command, size - 1);
    close(fd);
    if (length <= 0) return NULL;
    command[length] = '\0';
    const char *name = strrchr(command, '/');
    return name ? name + 1 : command;
}
