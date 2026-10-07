#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <poll.h>
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
#ifdef QFT_ANDROID_APP
#include <dlfcn.h>
#include "camera_capture.h"
static CameraCapture *g_capture;
static void *g_fidelity;
static unsigned g_max_fps;
static int g_leased;
static void *(*g_fidelity_create)(uint64_t);
static void (*g_fidelity_destroy)(uint64_t, void *);
static int fidelity_open(void) {
    void *library = dlopen("/system_ext/lib64/libossdk.oculus.so", RTLD_NOW);
    g_fidelity_create = library ? (void *(*)(uint64_t))dlsym(library, "createTrackingFidelityManager") : NULL;
    g_fidelity_destroy = library ? (void (*)(uint64_t, void *))dlsym(library, "destroyTrackingFidelityManager") : NULL;
    if (!g_fidelity_create || !g_fidelity_destroy || !(g_fidelity = g_fidelity_create(14))) { fprintf(stderr, "CAMERA_LEASE_UNAVAILABLE\n"); return 0; }
    void **v = *(void ***)g_fidelity;
    int status = 2;
    for (int i = 0; i < 15 && status == 2; i++) if ((status = ((int (*)(void *, int))v[3])(g_fidelity, 0)) == 2) usleep(200000);
    return g_leased = !status;
}
static void fidelity_close(void) {
    if (!g_fidelity) return;
    if (g_leased) ((int (*)(void *))(*(void ***)g_fidelity)[6])(g_fidelity);
    g_leased = 0;
    g_fidelity_destroy(14, g_fidelity); g_fidelity = NULL;
}
static int request_features(int face, int eye) {
    const int levels[2][2] = {{3, face}, {4, eye}};
    return g_fidelity && !((int (*)(void *, const void *, size_t, uint64_t))(*(void ***)g_fidelity)[0])(g_fidelity, levels, 2, 0);
}
static void requested_features(int *face, int *eye) {
    static const char *names[] = {"OFF", "LOW", "MEDIUM_LOW", "MEDIUM", "MEDIUM_HIGH", "HIGH"};
    *face = *eye = -1;
    FILE *dump = popen("dumpsys oculus.internal.ITrackingFidelityService/default", "r");
    if (!dump) return;
    char line[256]; int section = 0, requested = 0;
    while (fgets(line, sizeof line, dump)) {
        if (strstr(line, "Translator:")) { section = strstr(line, "faceEye Translator:") != NULL; requested = 0; }
        else if (strstr(line, "fidelities:") || strstr(line, "Events:")) requested = section && strstr(line, "Requested fidelities:");
        else if (requested) {
            int *target = strstr(line, "FEATURE_FACE ") ? face : strstr(line, "FEATURE_EYE ") ? eye : NULL;
            const char *level = strstr(line, "FIDELITY_LEVEL_");
            if (target && level) for (int i = 0; i < 6; i++)
                if (!strncmp(level + 15, names[i], strlen(names[i])) && strchr("\r\n", level[15 + strlen(names[i])])) *target = i;
        }
    }
    pclose(dump);
}
static char g_owned[512];
#define FACE_LEVEL 3
static void keep_features(void) {
    int face, eye; requested_features(&face, &eye);
    if (face < 0 || eye < 0 || (face >= FACE_LEVEL && eye >= 3)) return;
    if (request_features(face < FACE_LEVEL ? FACE_LEVEL : face, eye < 3 ? 3 : eye)) { int fd = open(g_owned, O_CREAT | O_WRONLY | O_CLOEXEC, 0600); if (fd >= 0) close(fd); }
    printf("NATIVE_FEATURES face=%d eye=%d\n", face, eye);
}
static void release_features(void) {
    if (access(g_owned, F_OK) || !request_features(0, 0)) return;
    unlink(g_owned); printf("NATIVE_FEATURES released\n");
}
static void camera_lease_open(const char *directory) {
    snprintf(g_owned, sizeof g_owned, "%s/features.owned", directory);
    if (!fidelity_open()) return;
    int (*allow)(void *, int, const int *, size_t) = (int (*)(void *, int, const int *, size_t))(*(void ***)g_fidelity)[4];
    const int fast[] = {17}, standard[] = {5};
    int status = 1, mode = 5;
    if (!g_max_fps || g_max_fps > 36) { status = allow(g_fidelity, 2, fast, 1); mode = 17; }
    if (status) { status = allow(g_fidelity, 2, standard, 1); mode = 5; }
    printf("CAMERA_LEASE status=%d mode=%d\n", status, mode);
    keep_features();
}
static void camera_lease_close(void) { release_features(); fidelity_close(); }
static int release_owned_features(const char *directory) {
    snprintf(g_owned, sizeof g_owned, "%s/features.owned", directory);
    if (access(g_owned, F_OK)) return 0;
    fidelity_open();
    release_features(); fidelity_close();
    return 0;
}
#endif

#include "shared.h"
#ifdef QFT_HEADSET_MODEL
#include "face_model.h"
#include "tracking_model.h"
#include "native_tracking.h"
#include <math.h>
#include <sched.h>
static FaceModel *g_model;
static TrackingModel *g_tracking;
static NativeTracking *g_native;
static const char *g_model_directory;
static int g_app_session;
static void close_model(void) {
#ifdef QFT_ANDROID_APP
    camera_lease_close();
    camera_capture_close(g_capture);
#endif
    native_tracking_close(g_native); tracking_model_close(g_tracking); face_model_close(g_model);
}
#ifdef QFT_ANDROID_APP
#define RELAY_NAME "libqft_worker.so"
#else
#define RELAY_NAME "qft-headset-model"
#endif
#else
#define RELAY_NAME "questpro-camera-relay"
#endif

#define CAPTURE_LEASE_NS UINT64_C(2000000000)
#define STREAM_PORT 27273

static uint8_t *g_shared;
#ifndef QFT_ANDROID_APP
static ino_t g_shared_inode;
#endif
static volatile sig_atomic_t g_stopped;
static int g_server = -1, g_client = -1;

static void set_capture_lease(uint8_t *shared, uint64_t active_until) {
    uint64_t *lease = (uint64_t *)(void *)(shared + SHARED_ACTIVE_UNTIL_OFFSET);
    __atomic_store_n(lease, active_until, __ATOMIC_RELEASE);
}

static void handle_termination(int signal_number) {
    (void)signal_number;
    if (g_shared) set_capture_lease(g_shared, 0);
    g_stopped = 1;
    if (g_server >= 0) shutdown(g_server, SHUT_RDWR);
    if (g_client >= 0) shutdown(g_client, SHUT_RDWR);
}

#ifndef QFT_ANDROID_APP
static int is_questpro_relay(pid_t pid) {
    return runs_program(pid, RELAY_NAME);
}

static void stop_previous_relays(void) {
    pid_t matches[32];
    size_t found = find_pids(RELAY_NAME, matches, 32), count = 0;
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
#else
static int owns_worker(pid_t pid, const char *token) {
    char path[64], own[1024], other[1024], command[4096];
    ssize_t n = readlink("/proc/self/exe", own, sizeof own - 1);
    if (n < 0) return 0;
    own[n] = 0;
    snprintf(path, sizeof path, "/proc/%d/exe", pid);
    n = readlink(path, other, sizeof other - 1);
    if (n < 0) return 0;
    other[n] = 0;
    if (strcmp(own, other)) return 0;
    snprintf(path, sizeof path, "/proc/%d/cmdline", pid);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    n = read(fd, command, sizeof command - 1); close(fd);
    if (n <= 0) return 0;
    command[n] = 0;
    for (size_t i = 0; i < (size_t)n; i += strlen(command + i) + 1)
        if (!strcmp(command + i, "--token") && i + 8 + 64 < (size_t)n && !strcmp(command + i + 8, token)) return 1;
    return 0;
}
static int stop_owned_worker(const char *token) {
    if (strlen(token) != 64 || strspn(token, "0123456789abcdef") != 64) return 1;
    pid_t pids[32]; size_t count = find_pids(RELAY_NAME, pids, 32);
    for (size_t i = 0; i < count; i++) if (pids[i] != getpid() && owns_worker(pids[i], token)) {
        kill(pids[i], SIGTERM);
        for (unsigned attempt = 0; attempt < 30 && owns_worker(pids[i], token); attempt++) usleep(100000);
        if (owns_worker(pids[i], token)) kill(pids[i], SIGKILL);
    }
    return 0;
}
#endif

static void put_u32(uint8_t *buffer, size_t offset, uint32_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

static void put_u64(uint8_t *buffer, size_t offset, uint64_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

#ifndef QFT_ANDROID_APP
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
    g_shared_inode = status.st_ino;
    void *mapping = mmap(NULL, SHARED_BYTES, PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    if (mapping == MAP_FAILED) {
        shared_memory_failure(fd, SHARED_PATH, "map camera buffer (mmap)", errno, status.st_uid);
        return NULL;
    }
    close(fd);
    return (uint8_t *)mapping;
}

#endif
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

#ifndef QFT_ANDROID_APP
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
    __atomic_thread_fence(__ATOMIC_ACQUIRE); 
    uint32_t after = __atomic_load_n(generation, __ATOMIC_RELAXED);
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

#endif
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
#ifdef QFT_HEADSET_MODEL
        } else if (!strcmp(argv[i], "--model") && i + 1 < argc) {
            g_model_directory = argv[++i];
        } else if (!strcmp(argv[i], "--app-session")) {
            g_app_session = 1;
#endif
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

#if defined(QFT_ANDROID_APP) && defined(QFT_HEADSET_MODEL)
#include <pthread.h>
typedef struct {
    pthread_mutex_t lock;
    int client, stop, failed, enabled;
    uint64_t *packet, *timestamp;
    uint32_t *face, *eye, *face_updates;
    const TrackingResult *output;
} MetaRefresh;
static void *refresh_meta(void *argument) {
    MetaRefresh *r = argument;
    uint64_t next = 0;
    for (;;) {
        usleep(1000);
        pthread_mutex_lock(&r->lock);
        if (r->stop || r->failed) { pthread_mutex_unlock(&r->lock); return NULL; }
        uint64_t now = monotonic_nanoseconds();
        NativeSample fresh;
        if (r->enabled && now >= next) {
            native_tracking_read(g_native, now, &fresh);
            if ((fresh.flags & 1) && (fresh.face_sequence != *r->face || fresh.eye_sequence != *r->eye)) {
                if (fresh.face_sequence != *r->face) ++*r->face_updates;
                *r->face = fresh.face_sequence; *r->eye = fresh.eye_sequence;
                next = now + UINT64_C(5000000);
                uint8_t header[64] = {0};
                memcpy(header, "QPLIVE3", 7);
                put_u32(header, 8, 3); put_u32(header, 12, sizeof header);
                put_u64(header, 16, ++*r->packet); put_u64(header, 24, *r->timestamp);
                put_u32(header, 32, sizeof *r->output / 4); put_u32(header, 36, 1); put_u32(header, 40, sizeof *r->output / 4);
                put_u32(header, 44, 4); put_u32(header, 48, sizeof *r->output + sizeof fresh); put_u32(header, 52, 0x1f);
                put_u64(header, 56, monotonic_nanoseconds());
                if (!send_all(r->client, header, sizeof header) || !send_all(r->client, r->output, sizeof *r->output) ||
                    !send_all(r->client, &fresh, sizeof fresh)) r->failed = 1;
            }
        }
        pthread_mutex_unlock(&r->lock);
    }
}
#define REFRESH_LOCK() pthread_mutex_lock(&refresh.lock)
#define REFRESH_UNLOCK() pthread_mutex_unlock(&refresh.lock)
#else
#define REFRESH_LOCK() ((void)0)
#define REFRESH_UNLOCK() ((void)0)
#endif

static int run(int argc, char **argv);
int main(int argc, char **argv) {
    int result = run(argc, argv);
#ifdef QFT_HEADSET_MODEL
    close_model();
#endif
    return result;
}
static int run(int argc, char **argv) {
    setvbuf(stdout, NULL, _IOLBF, 0);
#ifdef QFT_ANDROID_APP
    if (argc == 3 && !strcmp(argv[1], "--stop-token")) return stop_owned_worker(argv[2]);
    if (argc == 3 && !strcmp(argv[1], "--release-features")) return release_owned_features(argv[2]);
#endif
#ifdef QFT_HEADSET_MODEL
    cpu_set_t affinity;
    CPU_ZERO(&affinity);
    for (int i = 4; i < 8; i++) CPU_SET(i, &affinity);
    (void)sched_setaffinity(0, sizeof affinity, &affinity);
    if (argc == 3 && !strcmp(argv[1], "--check-model")) {
        alarm(45);
        FaceModel *model = face_model_open(argv[2]);
        if (!model) return 5;
        TrackingModel *tracking = tracking_model_open(argv[2]);
        uint8_t *blank = calloc(1, SENSOR_BYTES);
        float output[999];
        TrackingResult result;
        TrackingControl control = {.flags = TRACK_EXTRA | TRACK_PUPILS, .native_age = 1000};
        if (tracking) tracking_model_control(tracking, &control, 0);
        int ok = blank && tracking && face_model_run(model, blank, output) && tracking_model_run(tracking, blank, output, 0, &result);
        free(blank);
        tracking_model_close(tracking);
        face_model_close(model);
        if (ok) puts("MODEL_READY dsp_convolutions=15 personalized=headset pupils=headset protocol=2");
        return ok ? 0 : 5;
    }
#endif
#ifndef QFT_ANDROID_APP
    if (argc == 2 && !strcmp(argv[1], "--stop")) {
        stop_previous_relays();
        clear_capture_lease_file();
        unlink(SHARED_PATH);
        printf("RELAY_STOPPED capture=idle\n");
        return 0;
    }
#endif
    unsigned max_fps;
    const char *token = NULL;
    struct in_addr bind_address;
    if (!parse_arguments(argc, argv, &max_fps, &token, &bind_address)) {
        fprintf(stderr, "Usage: %s --token <64 hex characters> [--max-fps 0..120] [--bind <IPv4>] | --stop\n",
                argv[0]);
        return 1;
    }
#ifdef QFT_ANDROID_APP
    if (!g_app_session) return 1;
#endif
    int server = open_server_with_retry(bind_address);
    if (server < 0) {
        fprintf(stderr, "SERVER_OPEN_FAILED error=%s\n", strerror(errno));
        return 3;
    }
    g_server = server;
    signal(SIGTERM, handle_termination);
    signal(SIGINT, handle_termination);
    signal(SIGHUP, handle_termination);
    signal(SIGPIPE, SIG_IGN);
#ifdef QFT_HEADSET_MODEL
    if (!g_model_directory) return 1;
    pid_t camera_provider = 0;
    find_pids("vendor.oculus.hardware.sensors@1.0-service", &camera_provider, 1);
#ifdef QFT_ANDROID_APP
    g_max_fps = max_fps;
    g_capture = camera_capture_open(token, camera_provider);
    if (!g_capture || g_stopped) return 8;
    camera_lease_open(g_model_directory);
#endif
    alarm(45);
    g_model = face_model_open(g_model_directory);
    if (!g_model) return 5;
    g_tracking = tracking_model_open(g_model_directory);
    if (!g_tracking) return 6; 
    g_native = native_tracking_open();
    if (g_app_session && !g_native) return 7;
    uint64_t provider_check = 0;
    alarm(0);
    if (g_stopped) return 0;
#endif
#ifndef QFT_ANDROID_APP
    uint8_t *shared = open_shared_memory();
    if (!shared) return 2;
    g_shared = shared;
    set_capture_lease(shared, 0);
    put_u32(shared, SHARED_REQUESTED_MAX_FPS_OFFSET, max_fps);
#endif
    uint8_t *frame = malloc(SENSOR_BYTES);
    if (!frame) return 4;
    char address_text[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &bind_address, address_text, sizeof(address_text));
    printf("RELAY_LISTENING address=%s port=%d max_fps=%u\n",
           address_text, STREAM_PORT, max_fps);

    const uint64_t minimum_interval = max_fps
        ? UINT64_C(1000000000) / max_fps : 0;
    int result = 0;
    while (!g_stopped) {
#ifdef QFT_HEADSET_MODEL
        if (g_app_session) {
            struct pollfd pending = {.fd = server, .events = POLLIN};
            if (poll(&pending, 1, 10000) <= 0) break;
        }
#endif
        int client = accept_client(server);
        if (client < 0) continue;
        g_client = client;
        if (!authorize_client(client, token)) { close(client); g_client = -1; continue; }
        printf("CLIENT_CONNECTED\n");
#ifndef QFT_ANDROID_APP
        set_capture_lease(shared, monotonic_nanoseconds() + CAPTURE_LEASE_NS);
        uint32_t last_generation = 0;
#else
        uint64_t last_control = monotonic_nanoseconds();
#endif
        uint64_t next_send_at = 0;
#ifdef QFT_HEADSET_MODEL
        unsigned raw_fps = 0;
        uint64_t next_raw_at = 0;
        TrackingControl control = {0}, incoming = {0}; 
        size_t control_bytes = 0;
        uint32_t revision = 0, tracking_flags = 0;
        control.native_age = 1000;
        uint64_t packet = 0;
        uint32_t sent_face = 0, meta_face_updates = 0;
#ifdef QFT_ANDROID_APP
        uint64_t last_timestamp = 0;
        uint32_t sent_eye = 0;
        TrackingResult last_output;
        MetaRefresh refresh = {.lock = PTHREAD_MUTEX_INITIALIZER, .client = client, .packet = &packet, .timestamp = &last_timestamp,
                               .face = &sent_face, .eye = &sent_eye, .face_updates = &meta_face_updates, .output = &last_output};
        pthread_t refresher;
        int refreshing = !pthread_create(&refresher, NULL, refresh_meta, &refresh);
#endif
        tracking_model_control(g_tracking, &control, (double)monotonic_nanoseconds() / 1e9);
#endif
        while (!g_stopped) {
            uint64_t now = monotonic_nanoseconds();
#ifdef QFT_HEADSET_MODEL
            if (g_app_session && now >= provider_check) {
                provider_check = now + UINT64_C(1000000000);
#ifdef QFT_ANDROID_APP
                static unsigned feature_check;
                if (g_fidelity && ++feature_check % 5 == 0) keep_features();
#endif
                if (!camera_provider || !runs_program(camera_provider, "vendor.oculus.hardware.sensors@1.0-service")) {
                    fprintf(stderr, "CAMERA_PROVIDER_CHANGED reattach_required=1\n");
                    g_stopped = 1;
                    break;
                }
            }
            int disconnected = 0;
            for (unsigned pending = 0; pending < 64; pending++) {
            ssize_t received = recv(client, (uint8_t *)&incoming + control_bytes, sizeof incoming - control_bytes, MSG_DONTWAIT);
            if (!received) { disconnected = 1; break; }
            if (received > 0) {
                control_bytes += (size_t)received;
                if (control_bytes == sizeof incoming) {
                    if (incoming.magic != UINT32_C(0x43544651) || (incoming.version != 2 && incoming.version != 3) ||
                        (incoming.raw_fps != 0 && incoming.raw_fps != 8 && incoming.raw_fps != 24) || incoming.flags > 127 ||
                        !isfinite(incoming.native_age) || incoming.native_age < 0) { disconnected = 1; break; }
                    for (unsigned i = 0; i < 70; i++) if (!isnan(incoming.native_values[i]) &&
                        (!isfinite(incoming.native_values[i]) || incoming.native_values[i] < 0 || incoming.native_values[i] > 1)) disconnected = 1;
                    if (disconnected) break;
                    control = incoming;
                    if (control.version == 3 && !g_native) { result = 7; g_stopped = 1; break; }
                    if (revision != control.revision && !tracking_model_reload(g_tracking, g_model_directory)) { result = 6; g_stopped = 1; break; }
                    revision = control.revision; raw_fps = control.raw_fps; tracking_flags = control.flags;
                    tracking_model_control(g_tracking, &control, (double)now / 1e9);
#ifdef QFT_ANDROID_APP
                    last_control = now;
#endif
                    control_bytes = 0;
                }
            } else {
                if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) disconnected = 1;
                break;
            }
            }
            if (disconnected || g_stopped) break;
#endif
#ifdef QFT_ANDROID_APP
            if (now - last_control > UINT64_C(5000000000)) {
                fprintf(stderr, "CLIENT_HEARTBEAT_EXPIRED\n"); break;
            }
#else
            set_capture_lease(shared, now + CAPTURE_LEASE_NS);
#endif
            int ready = !(minimum_interval && next_send_at && now < next_send_at);
            uint64_t sequence = 0;
            uint64_t timestamp = 0;
#ifdef QFT_ANDROID_APP
            if (!ready || !camera_capture_read(g_capture, frame, &sequence, &timestamp)) {
#ifdef QFT_HEADSET_MODEL
                if (refresh.failed) break;
#endif
                usleep(1000);
                continue;
            }
            now = monotonic_nanoseconds();
#else
            if (!ready) {
                usleep((useconds_t)((next_send_at - now) / 1000));
                continue;
            }
            if (!copy_stable_frame(shared, &last_generation, &sequence,
                                   &timestamp, frame)) {
                wait_for_frame(shared, last_generation);
                continue;
            }
#endif
            if (timestamp > now || now - timestamp > UINT64_C(250000000)) continue;
#ifdef QFT_HEADSET_MODEL
            float features[999] = {0};
            TrackingResult output;
            NativeSample native;
            REFRESH_LOCK();
            native_tracking_read(g_native, now, &native);
            REFRESH_UNLOCK();
            if (g_native && control.version == 3) {
                control.native_sequence = native.face_time;
                control.native_age = native.flags & 1 ? (float)((double)(now - native.face_time) / 1e9) : 1000;
                memcpy(control.native_values, native.expressions, sizeof control.native_values);
                tracking_model_control(g_tracking, &control, (double)now / 1e9);
            }
            uint64_t face_start = monotonic_nanoseconds();
            if ((tracking_flags & (TRACK_EXTRA | TRACK_TONGUE)) && !face_model_run(g_model, frame, features)) { result = 5; g_stopped = 1; break; }
            uint64_t track_start = monotonic_nanoseconds();
            if (!tracking_model_run(g_tracking, frame, features, (double)now / 1e9, &output)) { result = 5; g_stopped = 1; break; }
            static uint64_t timing_at, face_ns, track_ns, read_ns, native_ns; static unsigned timing_frames, native_frames;
            uint64_t done = monotonic_nanoseconds();
            face_ns += track_start - face_start; track_ns += done - track_start; read_ns += face_start - timestamp; timing_frames++;
            if ((native.flags & 1) && now > native.face_time) { native_ns += now - native.face_time; native_frames++; }
            if (!timing_at) timing_at = done + UINT64_C(5000000000);
            if (done >= timing_at) {
                fprintf(stderr, "WORKER_TIMING model=%.1ffps meta_face=%.1fHz read=%.1fms face=%.1fms track=%.1fms meta_age=%.1fms\n",
                        timing_frames / 5.0, meta_face_updates / 5.0, read_ns / 1e6 / timing_frames, face_ns / 1e6 / timing_frames,
                        track_ns / 1e6 / timing_frames, native_frames ? native_ns / 1e6 / native_frames : -1.0);
                meta_face_updates = 0;
                uint64_t stages[5]; face_model_stages(stages);
                uint64_t track[6]; tracking_model_stages(track);
                fprintf(stderr, "TRACK_STAGES networks=%.2fms rest=%.2fms pupils_detect=%.2fms(x%llu) pupils_other=%.2fms meta_not_fresh=%llu/%u\n",
                        track[0] / 1e6 / timing_frames, track[1] / 1e6 / timing_frames, track[3] ? track[2] / 1e6 / track[3] : 0.0,
                        (unsigned long long)track[3], track[4] / 1e6 / timing_frames, (unsigned long long)track[5], timing_frames);
                fprintf(stderr, "FACE_STAGES resize=%.2fms dsp0=%.2fms dsp1=%.2fms dsp2=%.2fms tail=%.2fms\n", stages[0] / 1e6 / timing_frames,
                        stages[1] / 1e6 / timing_frames, stages[2] / 1e6 / timing_frames, stages[3] / 1e6 / timing_frames, stages[4] / 1e6 / timing_frames);
                timing_at = done + UINT64_C(5000000000); face_ns = track_ns = read_ns = native_ns = 0; timing_frames = native_frames = 0;
            }
            REFRESH_LOCK();
            native_tracking_read(g_native, monotonic_nanoseconds(), &native);
            if (native.face_sequence != sent_face) meta_face_updates++;
            sent_face = native.face_sequence;
#ifdef QFT_ANDROID_APP
            sent_eye = native.eye_sequence; last_output = output; last_timestamp = timestamp;
            refresh.enabled = control.version == 3 && !raw_fps;
#endif
            uint8_t header[64] = {0};
            memcpy(header, "QPLIVE3", 7);
            put_u32(header, 8, 3); put_u32(header, 12, sizeof header);
            put_u64(header, 16, control.version == 3 ? ++packet : sequence); put_u64(header, 24, timestamp);
            put_u32(header, 32, sizeof output / 4); put_u32(header, 36, 1); put_u32(header, 40, sizeof output / 4);
            put_u32(header, 44, control.version == 3 ? 4 : 3);
            put_u32(header, 48, sizeof output + (control.version == 3 ? sizeof native : 0));
            put_u32(header, 52, 0x1f);
            put_u64(header, 56, monotonic_nanoseconds());
            int sent = send_all(client, header, sizeof header) && send_all(client, &output, sizeof output) &&
                       (control.version != 3 || send_all(client, &native, sizeof native));
            if (sent && raw_fps && now >= next_raw_at) {
                if (control.version == 3 && raw_fps == 24) {
                    float thumbnails[512];
                    tracking_model_thumbnails(frame, thumbnails);
                    put_u32(header, 44, 5); put_u32(header, 48, sizeof features + sizeof thumbnails);
                    sent = send_all(client, header, sizeof header) && send_all(client, features, sizeof features) &&
                           send_all(client, thumbnails, sizeof thumbnails);
                } else sent = send_frame(client, sequence, timestamp, frame);
                next_raw_at = now + UINT64_C(1000000000) / raw_fps;
            }
            REFRESH_UNLOCK();
#else
            int sent = send_frame(client, sequence, timestamp, frame);
#endif
            if (!sent) {
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
#ifndef QFT_ANDROID_APP
        set_capture_lease(shared, 0);
#elif defined(QFT_HEADSET_MODEL)
        REFRESH_LOCK(); refresh.stop = 1; REFRESH_UNLOCK();
        if (refreshing) pthread_join(refresher, NULL);
#endif
        close(client);
        g_client = -1;
        printf("CLIENT_DISCONNECTED\n");
#ifdef QFT_HEADSET_MODEL
        if (g_app_session) break;
#endif
    }
    close(server);
    g_server = -1;
    free(frame);
#ifndef QFT_ANDROID_APP
    set_capture_lease(shared, 0);
#ifdef QFT_HEADSET_MODEL
    if (g_app_session) {
        struct stat current;
        if (!lstat(SHARED_PATH, &current)) {
            if (S_ISREG(current.st_mode) && current.st_ino == g_shared_inode) {
                memset(shared + SHARED_HEADER_BYTES, 0, SENSOR_BYTES);
                unlink(SHARED_PATH);
            }
        }
    }
#endif
    g_shared = NULL;
    munmap(shared, SHARED_BYTES);
#endif
    return result;
}
