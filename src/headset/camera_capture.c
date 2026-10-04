#define _GNU_SOURCE
#include "shared.h"
#include "camera_capture.h"
#include <poll.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/un.h>

#define CAMERA_MAP_BYTES ((size_t)0xc4000)
#define CAMERA_COUNT 9
#define CAMERA_MAGIC UINT32_C(0x31464351)

static int address_for(const char *token, struct sockaddr_un *address) {
    if (!token || strlen(token) != 64 || strspn(token, "0123456789abcdef") != 64) return 0;
    memset(address, 0, sizeof *address);
    address->sun_family = AF_UNIX;
    snprintf(address->sun_path + 1, sizeof address->sun_path - 1, "qft-capture-%s", token);
    return 1;
}

#ifdef QFT_CAPTURE_EXPORT
void qft_export_main(const char *token, int *unload_policy, void *state) {
    (void)state;
    *unload_policy = 0;
    struct sockaddr_un address;
    if (!address_for(token, &address)) return;
    int socket_fd = socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0);
    if (socket_fd < 0) return;
    int connected = 0;
    for (unsigned i = 0; i < 200; i++) {
        if (!connect(socket_fd, (void *)&address, sizeof address)) { connected = 1; break; }
        if (errno != ECONNREFUSED && errno != ENOENT) break;
        usleep(25000);
    }
    struct ucred peer;
    socklen_t length = sizeof peer;
    if (!connected || getsockopt(socket_fd, SOL_SOCKET, SO_PEERCRED, &peer, &length) || peer.uid != 0) {
        close(socket_fd); return;
    }
    ino_t inodes[CAMERA_COUNT];
    unsigned count = 0;
    FILE *maps = fopen("/proc/self/maps", "re");
    char line[1024];
    if (maps) {
        while (fgets(line, sizeof line, maps)) {
            unsigned long start, end, inode;
            char permissions[5];
            if (!strstr(line, "/dmabuf:dmabuf") ||
                sscanf(line, "%lx-%lx %4s %*s %*s %lu", &start, &end, permissions, &inode) != 4 ||
                permissions[0] != 'r' || end - start != CAMERA_MAP_BYTES) continue;
            if (count == CAMERA_COUNT) { count = 0; break; }
            inodes[count++] = inode;
        }
        fclose(maps);
    }
    int fds[CAMERA_COUNT];
    for (unsigned i = 0; i < CAMERA_COUNT; i++) fds[i] = -1;
    DIR *directory = count == CAMERA_COUNT ? opendir("/proc/self/fd") : NULL;
    struct dirent *entry;
    if (directory) {
        while ((entry = readdir(directory))) {
            char *end;
            long number = strtol(entry->d_name, &end, 10);
            struct stat status;
            if (*end || number < 0 || number > INT32_MAX || fstat((int)number, &status)) continue;
            for (unsigned i = 0; i < CAMERA_COUNT; i++)
                if (fds[i] < 0 && status.st_ino == inodes[i]) fds[i] = fcntl((int)number, F_DUPFD_CLOEXEC, 3);
        }
        closedir(directory);
    }
    int valid = count == CAMERA_COUNT;
    for (unsigned i = 0; i < CAMERA_COUNT; i++) if (fds[i] < 0) valid = 0;
    if (valid) {
        uint32_t payload[2] = {CAMERA_MAGIC, CAMERA_COUNT};
        union { struct cmsghdr align; char bytes[CMSG_SPACE(sizeof fds)]; } ancillary = {0};
        struct iovec buffer = {payload, sizeof payload};
        struct msghdr message = {.msg_iov = &buffer, .msg_iovlen = 1,
            .msg_control = ancillary.bytes, .msg_controllen = sizeof ancillary.bytes};
        struct cmsghdr *rights = CMSG_FIRSTHDR(&message);
        rights->cmsg_level = SOL_SOCKET; rights->cmsg_type = SCM_RIGHTS; rights->cmsg_len = CMSG_LEN(sizeof fds);
        memcpy(CMSG_DATA(rights), fds, sizeof fds);
        (void)sendmsg(socket_fd, &message, MSG_NOSIGNAL | MSG_DONTWAIT);
    }
    for (unsigned i = 0; i < CAMERA_COUNT; i++) if (fds[i] >= 0) close(fds[i]);
    close(socket_fd);
}
#else
struct CameraCapture {
    const uint8_t *maps[CAMERA_COUNT];
    uint8_t first[SENSOR_BYTES];
    uint32_t counter;
    uint64_t sequence;
};

static uint32_t counter(const uint8_t *map) {
    return __atomic_load_n((const uint32_t *)(const void *)(map + SENSOR_BYTES + 24), __ATOMIC_ACQUIRE);
}

void camera_capture_close(CameraCapture *capture) {
    if (!capture) return;
    for (unsigned i = 0; i < CAMERA_COUNT; i++) if (capture->maps[i]) munmap((void *)capture->maps[i], CAMERA_MAP_BYTES);
    memset(capture, 0, sizeof *capture);
    free(capture);
}

CameraCapture *camera_capture_open(const char *token, pid_t provider) {
    struct sockaddr_un address;
    if (provider <= 0 || !address_for(token, &address)) return NULL;
    int server = socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0), client = -1;
    CameraCapture *capture = NULL;
    if (server < 0) return NULL;
    if (bind(server, (void *)&address, sizeof address) || listen(server, 1)) goto finish;
    puts("CAMERA_HANDLES_WAITING");
    struct pollfd pending = {.fd = server, .events = POLLIN};
    if (poll(&pending, 1, 10000) <= 0) goto finish;
    client = accept4(server, NULL, NULL, SOCK_CLOEXEC);
    struct ucred peer;
    socklen_t length = sizeof peer;
    if (client < 0 || getsockopt(client, SOL_SOCKET, SO_PEERCRED, &peer, &length) || peer.pid != provider) goto finish;
    pending.fd = client;
    if (poll(&pending, 1, 5000) <= 0) goto finish;
    uint32_t payload[2] = {0};
    union { struct cmsghdr align; char bytes[CMSG_SPACE(CAMERA_COUNT * sizeof(int))]; } ancillary = {0};
    struct iovec buffer = {payload, sizeof payload};
    struct msghdr message = {.msg_iov = &buffer, .msg_iovlen = 1,
        .msg_control = ancillary.bytes, .msg_controllen = sizeof ancillary.bytes};
    ssize_t received = recvmsg(client, &message, MSG_CMSG_CLOEXEC);
    if (received < 0) goto finish;
    capture = calloc(1, sizeof *capture);
    unsigned count = 0;
    for (struct cmsghdr *rights = CMSG_FIRSTHDR(&message); rights; rights = CMSG_NXTHDR(&message, rights)) {
        if (rights->cmsg_len < CMSG_LEN(0)) break;
        if (rights->cmsg_level != SOL_SOCKET || rights->cmsg_type != SCM_RIGHTS) continue;
        const int *fds = (const int *)(const void *)CMSG_DATA(rights);
        size_t n = (rights->cmsg_len - CMSG_LEN(0)) / sizeof(int);
        for (size_t i = 0; i < n; i++) {
            if (capture && count < CAMERA_COUNT) {
                void *map = mmap(NULL, CAMERA_MAP_BYTES, PROT_READ, MAP_SHARED, fds[i], 0);
                if (map != MAP_FAILED) capture->maps[count++] = map;
            }
            close(fds[i]);
        }
    }
    if (received != sizeof payload || payload[0] != CAMERA_MAGIC || payload[1] != CAMERA_COUNT ||
        (message.msg_flags & (MSG_TRUNC | MSG_CTRUNC)) || count != CAMERA_COUNT) {
        camera_capture_close(capture); capture = NULL;
    }
    if (capture) {
        capture->counter = counter(capture->maps[0]);
        for (unsigned i = 1; i < CAMERA_COUNT; i++) {
            uint32_t value = counter(capture->maps[i]);
            if ((int32_t)(value - capture->counter) > 0) capture->counter = value;
        }
    }
finish:
    if (client >= 0) close(client);
    close(server);
    if (capture) puts("CAMERA_HANDLES_READY buffers=9 access=read-only owner=worker");
    else fprintf(stderr, "CAMERA_HANDLES_FAILED\n");
    return capture;
}

static int face_views(const uint8_t *map) {
    unsigned nonzero = 0, samples = 0;
    for (unsigned y = 23; y < SENSOR_HEIGHT; y += 47) for (unsigned x = 819; x < SENSOR_WIDTH; x += 89) {
        nonzero += map[y * SENSOR_WIDTH + x] != 0; samples++;
    }
    return nonzero * 4 > samples * 3;
}
int camera_capture_read(CameraCapture *capture, uint8_t *frame, uint64_t *sequence, uint64_t *timestamp) {
    const uint8_t *newest = NULL;
    uint32_t latest = capture->counter;
    for (unsigned i = 0; i < CAMERA_COUNT; i++) {
        uint32_t value = counter(capture->maps[i]);
        if ((int32_t)(value - latest) > 0 && face_views(capture->maps[i])) { newest = capture->maps[i]; latest = value; }
    }
    if (!newest) return 0;
    *timestamp = monotonic_nanoseconds();
    memcpy(capture->first, newest, SENSOR_BYTES);
    usleep(250);
    memcpy(frame, newest, SENSOR_BYTES);
    if (counter(newest) != latest || memcmp(frame, capture->first, SENSOR_BYTES) || !face_views(frame)) return 0;
    capture->counter = latest;
    *sequence = ++capture->sequence;
    return 1;
}
#endif
