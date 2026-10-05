#define _GNU_SOURCE
#include "native_tracking.h"
#include "shared.h"
#include <elf.h>
#include <math.h>
#include <sys/mman.h>

struct NativeTracking {
    pid_t pid;
    const uint8_t *pages[2];
    unsigned long inodes[2];
    uint64_t checked;
    NativeSample held;
};
#define NATIVE_MAX_AGE UINT64_C(250000000)
static const char *const names[] = {"TS_FACE_TRACKER_DEFAULT", "TS_EYE_TRACKER_DEFAULT"};

static int supported_abi(void) {
    static const uint8_t build_id[20] = {0xf2,0x0a,0x53,0x1c,0x01,0x58,0x88,0xdc,0x85,0xd3,0x5e,0x67,0x02,0x29,0x01,0x67,0x4d,0xbf,0xe7,0x1e};
    int fd = open("/system_ext/lib64/libtrackingserviceclients.so", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    Elf64_Ehdr header;
    int ok = 0;
    if (pread(fd, &header, sizeof header, 0) == sizeof header &&
        !memcmp(header.e_ident, ELFMAG, SELFMAG) && header.e_ident[EI_CLASS] == ELFCLASS64 &&
        header.e_machine == EM_AARCH64 && header.e_phentsize == sizeof(Elf64_Phdr) && header.e_phnum < 128) {
        for (unsigned i = 0; i < header.e_phnum; i++) {
            Elf64_Phdr segment;
            if (pread(fd, &segment, sizeof segment, header.e_phoff + i * sizeof segment) != sizeof segment ||
                segment.p_type != PT_NOTE || segment.p_filesz > 4096) continue;
            uint8_t notes[4096];
            if (pread(fd, notes, segment.p_filesz, segment.p_offset) != (ssize_t)segment.p_filesz) continue;
            for (size_t p = 0; p + 12 <= segment.p_filesz;) {
                Elf64_Nhdr note;
                memcpy(&note, notes + p, 12);
                if (note.n_namesz > 1024 || note.n_descsz > 1024) break;
                size_t desc = p + 12 + ((note.n_namesz + 3u) & ~3u);
                size_t end = desc + ((note.n_descsz + 3u) & ~3u);
                if (end > segment.p_filesz) break;
                if (note.n_type == NT_GNU_BUILD_ID && note.n_namesz == 4 && note.n_descsz == 20 &&
                    !memcmp(notes + p + 12, "GNU", 4) && !memcmp(notes + desc, build_id, 20)) ok = 1;
                p = end;
            }
        }
    }
    close(fd);
    return ok;
}

static void unmap_pages(NativeTracking *s) {
    for (int i = 0; i < 2; i++) {
        if (s->pages[i]) munmap((void *)s->pages[i], 4096);
        s->pages[i] = NULL;
    }
    s->pid = 0;
}

static void refresh(NativeTracking *s) {
    pid_t pid = 0;
    find_pids("trackingservice", &pid, 1);
    if (pid != s->pid) unmap_pages(s);
    s->pid = pid;
    if (!pid) return;
    char path[96], line[512];
    snprintf(path, sizeof path, "/proc/%d/maps", pid);
    FILE *maps = fopen(path, "re");
    if (!maps) { unmap_pages(s); return; }
    int found[2] = {0};
    while (fgets(line, sizeof line, maps)) {
        for (int i = 0; i < 2; i++) {
            char needle[80];
            snprintf(needle, sizeof needle, "/dev/ashmem/%s ", names[i]);
            unsigned long start, end, inode;
            if (!strstr(line, needle) || sscanf(line, "%lx-%lx %*s %*s %*s %lu", &start, &end, &inode) != 3 || end - start != 4096) continue;
            found[i] = 1;
            if (s->pages[i] && s->inodes[i] == inode) continue;
            if (s->pages[i]) munmap((void *)s->pages[i], 4096);
            s->pages[i] = NULL;
            snprintf(path, sizeof path, "/proc/%d/map_files/%lx-%lx", pid, start, end);
            int fd = open(path, O_RDONLY | O_CLOEXEC);
            if (fd < 0) continue;
            void *page = mmap(NULL, 4096, PROT_READ, MAP_SHARED, fd, 0);
            close(fd);
            if (page != MAP_FAILED) { s->pages[i] = page; s->inodes[i] = inode; }
        }
    }
    fclose(maps);
    for (int i = 0; i < 2; i++) if (!found[i] && s->pages[i]) {
        munmap((void *)s->pages[i], 4096); s->pages[i] = NULL;
    }
}

static int record(const uint8_t *page, size_t bytes, uint8_t *out, uint32_t *sequence) {
    if (!page) return 0;
    const uint32_t *writer = (const uint32_t *)(const void *)(page + 16);
    const uint32_t *published = writer + 1;
    for (int i = 0; i < 4; i++) {
        uint32_t seq = __atomic_load_n(published, __ATOMIC_ACQUIRE);
        memcpy(out, page + 24 + (seq & 1) * bytes, bytes);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        uint32_t writing = __atomic_load_n(writer, __ATOMIC_RELAXED);
        if (seq == writing) { *sequence = seq; return 1; }
        seq = writing ^ 1u;
        memcpy(out, page + 24 + (seq & 1) * bytes, bytes);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (writing == __atomic_load_n(writer, __ATOMIC_RELAXED)) { *sequence = writing - 1; return 1; }
    }
    return 0;
}

NativeTracking *native_tracking_open(void) {
    if (!supported_abi()) { fprintf(stderr, "NATIVE_TRACKING_UNSUPPORTED client_abi\n"); return NULL; }
    return calloc(1, sizeof(NativeTracking));
}
void native_tracking_read(NativeTracking *s, uint64_t now, NativeSample *out) {
    memset(out, 0, sizeof *out);
    if (!s) return;
    if (!s->checked || now - s->checked >= UINT64_C(1000000000)) { refresh(s); s->checked = now; }
    uint8_t face[664], eye[1384];
    if (record(s->pages[0], sizeof face, face, &out->face_sequence)) {
        memcpy(&out->face_time, face, 8);
        memcpy(out->expressions, face + 12, 280);
        int valid = face[8] == 1 && out->face_time <= now && now - out->face_time <= NATIVE_MAX_AGE;
        for (int i = 0; i < 70; i++) if (!isfinite(out->expressions[i]) || out->expressions[i] < 0 || out->expressions[i] > 1) valid = 0;
        if (valid) out->flags |= 1;
    }
    if (record(s->pages[1], sizeof eye, eye, &out->eye_sequence)) {
        memcpy(&out->eye_time, eye, 8);
        int fresh = eye[8] == 1 && out->eye_time <= now && now - out->eye_time <= NATIVE_MAX_AGE;
        for (int side = 0; side < 2; side++) {
            const uint8_t *pose = eye + 0x30 + side * 0x68;
            float *q = out->eye_orientation + side * 4;
            memcpy(q, pose, 16);
            float norm = 0;
            for (int i = 0; i < 4; i++) norm += q[i] * q[i];
            if (fresh && pose[0x34] == 1 && isfinite(norm) && fabsf(norm - 1) < .02f) out->flags |= 2u << side;
            else memset(q, 0, 16);
        }
    }
    if (out->flags & 1) { s->held.face_time = out->face_time; s->held.face_sequence = out->face_sequence; memcpy(s->held.expressions, out->expressions, sizeof out->expressions); }
    else if (s->held.face_time && s->held.face_time <= now && now - s->held.face_time <= NATIVE_MAX_AGE) {
        out->face_time = s->held.face_time; out->face_sequence = s->held.face_sequence;
        memcpy(out->expressions, s->held.expressions, sizeof out->expressions); out->flags |= 1;
    } else memset(out->expressions, 0, sizeof out->expressions);
}
void native_tracking_close(NativeTracking *s) { if (s) { unmap_pages(s); free(s); } }
