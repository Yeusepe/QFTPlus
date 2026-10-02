#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#include "shared.h"

#define CAPTURE_LEASE_NS UINT64_C(2000000000)
#define STREAM_PORT 27273

static uint8_t *g_shared;

static void set_capture_lease(uint8_t *shared, uint64_t active_until) {
    uint64_t *lease = (uint64_t *)(void *)(shared + SHARED_ACTIVE_UNTIL_OFFSET);
    __atomic_store_n(lease, active_until, __ATOMIC_RELEASE);
}

static void handle_termination(int signal_number) {
    (void)signal_number;
    if (g_shared) set_capture_lease(g_shared, 0);
    _exit(0);
}

static int is_questpro_relay(pid_t pid) {
    return runs_program(pid, "questpro-camera-relay");
}

static void stop_previous_relays(void) {
    pid_t matches[32];
    size_t found = find_pids("questpro-camera-relay", matches, 32), count = 0;
    for (size_t i = 0; i < found; ++i) {
        pid_t pid = matches[i];
        if (pid == getpid()) continue;
        if (kill(pid, SIGTERM) == 0) {
            matches[count++] = pid;
            printf("STALE_RELAY_STOP_REQUESTED pid=%d\n", pid);
        } else {
            fprintf(stderr, "STALE_RELAY_STOP_FAILED pid=%d error=%s\n",
                    pid, strerror(errno));
        }
    }

    for (unsigned attempt = 0; attempt < 20 && count; ++attempt) {
        size_t alive = 0;
        for (size_t i = 0; i < count; ++i)
            if (is_questpro_relay(matches[i])) matches[alive++] = matches[i];
        count = alive;
        if (count) usleep(50000);
    }
    for (size_t i = 0; i < count; ++i)
        if (is_questpro_relay(matches[i])) (void)kill(matches[i], SIGKILL);
    if (count) usleep(100000);
}

static void clear_capture_lease_file(void) {
    int fd = open(SHARED_PATH, O_RDWR | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) return;
    void *mapping = mmap(NULL, SHARED_HEADER_BYTES, PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    close(fd);
    if (mapping == MAP_FAILED) return;
    set_capture_lease((uint8_t *)mapping, 0);
    munmap(mapping, SHARED_HEADER_BYTES);
}

static void put_u32(uint8_t *buffer, size_t offset, uint32_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

static void put_u64(uint8_t *buffer, size_t offset, uint64_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

static uid_t provider_uid(void) {
    pid_t pid;
    char path[32];
    struct stat status;
    if (!find_pids("vendor.oculus.hardware.sensors@1.0-service", &pid, 1)) return (uid_t)-1;
    snprintf(path, sizeof(path), "/proc/%d", (int)pid);
    return stat(path, &status) == 0 ? status.st_uid : (uid_t)-1;
}

static int shared_memory_failure(int fd, const char *path, const char *operation,
                                 int error, uid_t owner) {
    fprintf(stderr, "SHARED_MEMORY_FAILED operation=\"%s\" path=\"%s\" errno=%d error=\"%s\""
            " relay_uid=%lu relay_euid=%lu",
            operation, path, error, strerror(error),
            (unsigned long)getuid(), (unsigned long)geteuid());
    if (owner != (uid_t)-1)
        fprintf(stderr, " provider_uid=%lu", (unsigned long)owner);
    struct stat status;
    if (fd >= 0 && fstat(fd, &status) == 0)
        fprintf(stderr, " file_uid=%lu file_gid=%lu file_mode=%#lo file_links=%lu file_bytes=%lld",
                (unsigned long)status.st_uid, (unsigned long)status.st_gid,
                (unsigned long)status.st_mode, (unsigned long)status.st_nlink,
                (long long)status.st_size);
    fputc('\n', stderr);
    if (fd >= 0) close(fd);
    errno = error;
    return -1;
}

static int private_shared_file(const char *path) {
    uid_t owner = provider_uid();
    if (owner == (uid_t)-1)
        return shared_memory_failure(-1, path, "find camera provider UID", ESRCH, owner);
    int fd = open(path, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0)
        return shared_memory_failure(-1, path, "open shared file", errno, owner);
    struct stat status;
    if (fstat(fd, &status) != 0)
        return shared_memory_failure(fd, path, "inspect shared file (fstat)", errno, owner);
    if (!S_ISREG(status.st_mode) || status.st_nlink != 1 ||
        (status.st_uid != getuid() && status.st_uid != owner)) {
        printf("SHARED_MEMORY_STALE_REPLACED path=\"%s\" file_uid=%lu file_mode=%#lo file_links=%lu\n",
               path, (unsigned long)status.st_uid, (unsigned long)status.st_mode,
               (unsigned long)status.st_nlink);
        close(fd);
        if (unlink(path) != 0)
            return shared_memory_failure(-1, path, "remove stale shared file (unlink)", errno, owner);
        fd = open(path, O_CREAT | O_EXCL | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
        if (fd < 0)
            return shared_memory_failure(-1, path, "recreate shared file", errno, owner);
    }
    if (fchown(fd, getuid(), (gid_t)-1) != 0)
        return shared_memory_failure(fd, path, "change owner to relay (fchown)", errno, owner);
    if (fchmod(fd, 0600) != 0)
        return shared_memory_failure(fd, path, "set private permissions 0600 (fchmod)", errno, owner);
    if (fchown(fd, owner, (gid_t)-1) != 0)
        return shared_memory_failure(fd, path, "change owner to camera provider (fchown)", errno, owner);
    return fd;
}

static uint8_t *open_shared_memory(void) {
    int fd = private_shared_file(SHARED_PATH);
    if (fd < 0) return NULL;
    struct stat status;
    if (fstat(fd, &status) != 0) {
        shared_memory_failure(fd, SHARED_PATH, "inspect shared file size (fstat)", errno, (uid_t)-1);
        return NULL;
    }
    if ((size_t)status.st_size != SHARED_BYTES && ftruncate(fd, (off_t)SHARED_BYTES) != 0) {
        shared_memory_failure(fd, SHARED_PATH, "resize camera buffer (ftruncate)", errno, status.st_uid);
        return NULL;
    }
    void *mapping = mmap(NULL, SHARED_BYTES, PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    if (mapping == MAP_FAILED) {
        shared_memory_failure(fd, SHARED_PATH, "map camera buffer (mmap)", errno, status.st_uid);
        return NULL;
    }
    close(fd);
    return (uint8_t *)mapping;
}

static int open_server(struct in_addr bind_address) {
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    int enabled = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons(STREAM_PORT);
    address.sin_addr = bind_address;
    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0 ||
        listen(fd, 1) != 0) {
        int saved_errno = errno;
        close(fd);
        errno = saved_errno;
        return -1;
    }
    return fd;
}

static int open_server_with_retry(struct in_addr bind_address) {
    for (unsigned attempt = 0; attempt < 40; ++attempt) {
        int fd = open_server(bind_address);
        if (fd >= 0) return fd;
        if (errno != EADDRINUSE) return -1;
        usleep(50000);
    }
    return -1;
}

static int accept_client(int server) {
    int client;
    do {
        client = accept4(server, NULL, NULL, SOCK_CLOEXEC);
    } while (client < 0 && errno == EINTR);
    if (client >= 0) configure_client(client);
    return client;
}

static int copy_stable_frame(const uint8_t *shared, uint32_t *last_generation,
                             uint64_t *sequence, uint64_t *timestamp,
                             uint8_t *frame) {
    const uint32_t *generation =
        (const uint32_t *)(const void *)(shared + SHARED_GENERATION_OFFSET);
    uint32_t before = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    if ((before & 1u) || before == *last_generation) return 0;
    memcpy(sequence, shared + SHARED_SEQUENCE_OFFSET, sizeof(*sequence));
    memcpy(timestamp, shared + SHARED_TIMESTAMP_OFFSET, sizeof(*timestamp));
    memcpy(frame, shared + SHARED_HEADER_BYTES, SENSOR_BYTES);
    uint32_t after = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    if (before != after || (after & 1u)) return 0;
    *last_generation = after;
    return 1;
}

static void wait_for_frame(const uint8_t *shared, uint32_t last_generation) {
    static const struct timespec timeout = {.tv_sec = 0, .tv_nsec = 5000000};
    uint32_t seen = __atomic_load_n(
        (const uint32_t *)(const void *)(shared + SHARED_GENERATION_OFFSET), __ATOMIC_ACQUIRE);
    if (seen == last_generation || (seen & 1u))
        generation_futex(shared, FUTEX_WAIT, seen, &timeout);
}

static int send_frame(int client, uint64_t sequence, uint64_t timestamp,
                      const uint8_t *frame) {
    uint8_t header[64] = {0};
    memcpy(header, "QPLIVE3", 7);
    put_u32(header, 8, 3);
    put_u32(header, 12, sizeof(header));
    put_u64(header, 16, sequence);
    put_u64(header, 24, timestamp);
    put_u32(header, 32, SENSOR_WIDTH);
    put_u32(header, 36, SENSOR_HEIGHT);
    put_u32(header, 40, SENSOR_WIDTH);
    put_u32(header, 44, 1);
    put_u32(header, 48, SENSOR_BYTES);
    put_u32(header, 52, 0x1fu);
    return send_all(client, header, sizeof(header)) &&
           send_all(client, frame, SENSOR_BYTES);
}

static int parse_arguments(int argc, char **argv, unsigned *max_fps,
                           const char **token, struct in_addr *bind_address) {
    *max_fps = 30;
    bind_address->s_addr = htonl(INADDR_LOOPBACK);
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--bind") && i + 1 < argc) {
            if (inet_pton(AF_INET, argv[++i], bind_address) != 1) return 0;
        } else if (!strcmp(argv[i], "--max-fps") && i + 1 < argc) {
            char *end = NULL;
            unsigned long value = strtoul(argv[++i], &end, 10);
            if (!end || *end || value > 120) return 0;
            *max_fps = (unsigned)value;
        } else if (!strcmp(argv[i], "--token") && i + 1 < argc) {
            *token = argv[++i];
            if (strlen(*token) != 64 || strspn(*token,"0123456789abcdef") != 64) return 0;
        } else {
            return 0;
        }
    }
    return *token != NULL;
}

static int authorize_client(int client, const char *token) {
    char supplied[64];
    struct timeval timeout = {.tv_sec = 3, .tv_usec = 0};
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    if (recv(client, supplied, sizeof(supplied), MSG_WAITALL) != (ssize_t)sizeof(supplied))
        return 0;
    unsigned difference = 0;
    for (size_t i=0; i<sizeof(supplied); ++i) difference |= (unsigned char)supplied[i] ^ (unsigned char)token[i];
    return difference == 0;
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (argc == 2 && !strcmp(argv[1], "--stop")) {
        stop_previous_relays();
        clear_capture_lease_file();
        unlink(SHARED_PATH);
        printf("RELAY_STOPPED capture=idle\n");
        return 0;
    }
    unsigned max_fps;
    const char *token = NULL;
    struct in_addr bind_address;
    if (!parse_arguments(argc, argv, &max_fps, &token, &bind_address)) {
        fprintf(stderr, "Usage: %s --token <64 hex characters> [--max-fps 0..120] [--bind <IPv4>] | --stop\n",
                argv[0]);
        return 1;
    }
    stop_previous_relays();
    uint8_t *shared = open_shared_memory();
    if (!shared) return 2;
    g_shared = shared;
    set_capture_lease(shared, 0);
    put_u32(shared, SHARED_REQUESTED_MAX_FPS_OFFSET, max_fps);
    signal(SIGTERM, handle_termination);
    signal(SIGINT, handle_termination);
    int server = open_server_with_retry(bind_address);
    if (server < 0) {
        fprintf(stderr, "SERVER_OPEN_FAILED error=%s\n", strerror(errno));
        return 3;
    }
    uint8_t *frame = malloc(SENSOR_BYTES);
    if (!frame) return 4;
    char address_text[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &bind_address, address_text, sizeof(address_text));
    printf("RELAY_LISTENING address=%s port=%d max_fps=%u\n",
           address_text, STREAM_PORT, max_fps);

    const uint64_t minimum_interval = max_fps
        ? UINT64_C(1000000000) / max_fps : 0;
    for (;;) {
        int client = accept_client(server);
        if (client < 0) continue;
        if (!authorize_client(client, token)) { close(client); continue; }
        printf("CLIENT_CONNECTED\n");
        set_capture_lease(shared, monotonic_nanoseconds() + CAPTURE_LEASE_NS);
        uint32_t last_generation = 0;
        uint64_t next_send_at = 0;
        while (1) {
            uint64_t now = monotonic_nanoseconds();
            set_capture_lease(shared, now + CAPTURE_LEASE_NS);
            if (minimum_interval && next_send_at && now < next_send_at) {
                usleep((useconds_t)((next_send_at - now) / 1000));
                continue;
            }
            uint64_t sequence = 0;
            uint64_t timestamp = 0;
            if (!copy_stable_frame(shared, &last_generation, &sequence,
                                   &timestamp, frame)) {
                wait_for_frame(shared, last_generation);
                continue;
            }
            if (!send_frame(client, sequence, timestamp, frame)) {
                fprintf(stderr, "CLIENT_SEND_FAILED error=%s\n", strerror(errno));
                break;
            }
            if (minimum_interval) {
                if (!next_send_at) next_send_at = now;
                do {
                    next_send_at += minimum_interval;
                } while (next_send_at <= now);
            }
        }
        set_capture_lease(shared, 0);
        close(client);
        printf("CLIENT_DISCONNECTED\n");
    }
}
