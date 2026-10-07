#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#include "shared.h"

#define PORT 27055
#define PID_PATH "/data/local/tmp/qft-thumbrest.pid"
#define SERVICE "trackingservice"
#define PAGE_BYTES 4096
#define SEQ_OFFSET 0x1e8
#define RECORD_OFFSET 0x1f0
#define RECORD_BYTES 16
#define PACKET_BYTES 14
#define TRIGGER_SEQ_OFFSET 0x210
#define TRIGGER_RECORD_OFFSET 0x218
#define TRIGGER_RECORD_BYTES 0x24

typedef struct {
    pid_t pid;
    const uint8_t *pages[2];
    unsigned long inodes[2];
} Source;

static const char *const PAGE_NAMES[2] = {"TS_CONTROLLER_LEFT", "TS_CONTROLLER_RIGHT"};
static volatile sig_atomic_t g_stop;

static void on_signal(int number) {
    (void)number;
    g_stop = 1;
}

static int read_text(const char *path, char *buffer, size_t size) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    ssize_t count = read(fd, buffer, size - 1);
    close(fd);
    if (count < 0) return -1;
    buffer[count] = 0;
    return (int)count;
}

static int find_page(pid_t pid, const char *name, unsigned long *start, unsigned long *end, unsigned long *inode) {
    char path[64], needle[64], line[512];
    int found = 0;
    snprintf(path, sizeof path, "/proc/%d/maps", (int)pid);
    snprintf(needle, sizeof needle, "/dev/ashmem/%s ", name);
    FILE *maps = fopen(path, "re");
    if (maps == NULL) return 0;
    while (!found && fgets(line, sizeof line, maps))
        found = strstr(line, needle) != NULL && sscanf(line, "%lx-%lx %*s %*s %*s %lu", start, end, inode) == 3;
    fclose(maps);
    return found && *end - *start == PAGE_BYTES;
}

static const uint8_t *map_page(pid_t pid, const char *name, unsigned long *inode) {
    char path[96];
    unsigned long start, end;
    if (!find_page(pid, name, &start, &end, inode)) return NULL;
    snprintf(path, sizeof path, "/proc/%d/map_files/%lx-%lx", (int)pid, start, end);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return NULL;
    void *page = mmap(NULL, PAGE_BYTES, PROT_READ, MAP_SHARED, fd, 0);
    close(fd);
    return page == MAP_FAILED ? NULL : page;
}

static void close_source(Source *source) {
    for (int side = 0; side < 2; side++) {
        if (source->pages[side] != NULL) munmap((void *)source->pages[side], PAGE_BYTES);
        source->pages[side] = NULL;
    }
    source->pid = -1;
}

static int current_pages(const Source *source) {
    unsigned long start, end, inode;
    for (int side = 0; side < 2; side++)
        if (!find_page(source->pid, PAGE_NAMES[side], &start, &end, &inode) || inode != source->inodes[side]) return 0;
    return 1;
}

static int ensure_source(Source *source) {
    if (source->pid > 0 && current_pages(source)) return 1;
    close_source(source);
    if (!find_pids(SERVICE, &source->pid, 1)) return 0;
    for (int side = 0; side < 2; side++) source->pages[side] = map_page(source->pid, PAGE_NAMES[side], &source->inodes[side]);
    if (source->pages[0] != NULL && source->pages[1] != NULL) return 1;
    close_source(source);
    return 0;
}

static int read_record(const uint8_t *page, size_t seq_offset, size_t record_offset, size_t bytes, uint32_t *seq, uint8_t *record) {
    const uint32_t *counter = (const uint32_t *)(const void *)(page + seq_offset);
    for (int tries = 0; tries < 4; tries++) {
        uint32_t before = __atomic_load_n(counter, __ATOMIC_ACQUIRE);
        memcpy(record, page + record_offset + bytes * (before & 1), bytes);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (__atomic_load_n(counter, __ATOMIC_RELAXED) != before) continue;
        *seq = before;
        return 1;
    }
    return 0;
}

static int read_packet(const uint8_t *page, int kind, int side, uint32_t *seq, uint8_t packet[PACKET_BYTES]) {
    uint8_t record[TRIGGER_RECORD_BYTES];
    memset(packet, 0, PACKET_BYTES);
    packet[0] = (uint8_t)(2 * kind + side);
    if (kind == 0) {
        if (!read_record(page, SEQ_OFFSET, RECORD_OFFSET, RECORD_BYTES, seq, record)) return 0;
        packet[1] = record[12];
        memcpy(packet + 2, record, 12);
    } else {
        if (!read_record(page, TRIGGER_SEQ_OFFSET, TRIGGER_RECORD_OFFSET, TRIGGER_RECORD_BYTES, seq, record)) return 0;
        memcpy(packet + 6, record + 4, 4);
        memcpy(packet + 10, record, 4);
    }
    return 1;
}

static void stream(int client, Source *source) {
    uint32_t sent[2][2] = {{0}};
    int have[2][2] = {{0}};
    uint64_t checked = monotonic_nanoseconds();
    struct pollfd watch = {.fd = client, .events = POLLIN};
    while (!g_stop) {
        if (monotonic_nanoseconds() - checked >= UINT64_C(1000000000)) {
            checked = monotonic_nanoseconds();
            if (!ensure_source(source)) memset(have, 0, sizeof have);
        }
        uint8_t packets[4 * PACKET_BYTES];
        size_t used = 0;
        for (int side = 0; side < 2 && source->pages[side] != NULL; side++) {
            for (int kind = 0; kind < 2; kind++) {
                uint32_t seq;
                if (!read_packet(source->pages[side], kind, side, &seq, packets + used) || (have[kind][side] && seq == sent[kind][side])) continue;
                used += PACKET_BYTES;
                sent[kind][side] = seq;
                have[kind][side] = 1;
            }
        }
        if (used && !send_all(client, packets, used)) return;
        int ready = poll(&watch, 1, 1);
        if (ready < 0 && errno != EINTR) return;
        if (ready > 0) {
            char byte;
            ssize_t count = recv(client, &byte, 1, MSG_DONTWAIT);
            if (count == 0 || (count < 0 && errno != EAGAIN && errno != EINTR)) return;
        }
    }
}

static int listen_socket(void) {
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    int on = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, sizeof on);
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons(PORT)};
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(fd, (struct sockaddr *)&address, sizeof address) != 0 || listen(fd, 1) != 0) {
        close(fd);
        return -1;
    }
    return fd;
}

static void serve(int server) {
    Source source = {.pid = -1};
    struct sigaction action = {.sa_handler = on_signal};
    sigaction(SIGTERM, &action, NULL);
    sigaction(SIGINT, &action, NULL);
    while (!g_stop) {
        int client = accept4(server, NULL, NULL, SOCK_CLOEXEC);
        if (client < 0) {
            if (errno == EINTR) continue;
            break;
        }
        configure_client(client);
        ensure_source(&source);
        stream(client, &source);
        close(client);
    }
    close_source(&source);
    close(server);
}

static pid_t running_pid(void) {
    char text[32], command[256];
    if (read_text(PID_PATH, text, sizeof text) < 0) return -1;
    pid_t pid = (pid_t)atoi(text);
    const char *name = pid > 0 ? program_name(pid, command, sizeof command) : NULL;
    return name != NULL && strstr(name, "qft-thumbrest") != NULL ? pid : -1;
}

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--stop") == 0) {
        pid_t pid = running_pid();
        if (pid > 0) {
            kill(pid, SIGTERM);
            for (int i = 0; i < 150 && kill(pid, 0) == 0; i++) usleep(20000);
            if (kill(pid, 0) == 0) { kill(pid, SIGKILL); for (int i = 0; i < 50 && kill(pid, 0) == 0; i++) usleep(20000); }
        }
        unlink(PID_PATH);
        puts("THUMBREST_STOPPED");
        return 0;
    }
    if (argc != 2 || strcmp(argv[1], "--daemon") != 0) {
        fprintf(stderr, "usage: %s --daemon | --stop\n", argv[0]);
        return 2;
    }
    if (running_pid() > 0) {
        puts("THUMBREST_RUNNING");
        return 0;
    }
    int server = listen_socket();
    if (server < 0) {
        perror("listen on 127.0.0.1:27055");
        return 1;
    }
    if (daemon(0, 0) != 0) {
        perror("daemon");
        return 1;
    }
    FILE *pid_file = fopen(PID_PATH, "we");
    if (pid_file != NULL) {
        fprintf(pid_file, "%d\n", (int)getpid());
        fclose(pid_file);
    }
    serve(server);
    if (running_pid() == getpid()) unlink(PID_PATH);
    return 0;
}
