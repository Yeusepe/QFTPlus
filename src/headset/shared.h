#pragma once

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/futex.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define SENSOR_WIDTH 2000
#define SENSOR_HEIGHT 400
#define SENSOR_BYTES ((size_t)SENSOR_WIDTH * SENSOR_HEIGHT)

#define SHARED_PATH "/data/local/tmp/questpro-live-v10-shared.bin"
#define SHARED_GENERATION_OFFSET ((size_t)8)
#define SHARED_SEQUENCE_OFFSET ((size_t)16)
#define SHARED_TIMESTAMP_OFFSET ((size_t)24)
#define SHARED_ACTIVE_UNTIL_OFFSET ((size_t)64)
#define SHARED_REQUESTED_MAX_FPS_OFFSET ((size_t)72)
#define SHARED_HEADER_BYTES ((size_t)80)
#define SHARED_BYTES (SHARED_HEADER_BYTES + SENSOR_BYTES)

static inline uint64_t monotonic_nanoseconds(void) {
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (uint64_t)value.tv_sec * UINT64_C(1000000000) + (uint64_t)value.tv_nsec;
}

static inline long generation_futex(const uint8_t *shared, int op, uint32_t value,
                                    const struct timespec *timeout) {
    return syscall(SYS_futex, shared + SHARED_GENERATION_OFFSET, op, value, timeout, NULL, 0);
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

static inline int runs_program(pid_t pid, const char *name) {
    char command[512];
    const char *found = program_name(pid, command, sizeof command);
    size_t length = strlen(name);
    return found && !strncmp(found, name, length) && (!found[length] || found[length] == '-');
}

static inline size_t find_pids(const char *name, pid_t *pids, size_t capacity) {
    DIR *proc = opendir("/proc");
    if (!proc) return 0;
    size_t count = 0;
    struct dirent *entry;
    while (count < capacity && (entry = readdir(proc))) {
        pid_t pid = (pid_t)atoi(entry->d_name);
        if (pid > 0 && runs_program(pid, name)) pids[count++] = pid;
    }
    closedir(proc);
    return count;
}

static inline void configure_client(int client) {
    int enabled = 1;
    struct timeval timeout = {.tv_sec = 2, .tv_usec = 0};
    setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
}

static inline int send_all(int fd, const void *data, size_t size) {
    const uint8_t *bytes = (const uint8_t *)data;
    size_t sent = 0;
    while (sent < size) {
        ssize_t result = send(fd, bytes + sent, size - sent, MSG_NOSIGNAL);
        if (result > 0) {
            sent += (size_t)result;
            continue;
        }
        if (result < 0 && errno == EINTR) continue;
        return 0;
    }
    return 1;
}
