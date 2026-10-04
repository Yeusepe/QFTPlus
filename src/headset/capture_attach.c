#define _GNU_SOURCE
#include "frida-core.h"
#include "shared.h"
#include <sys/stat.h>

int main(int argc, char **argv) {
    if ((argc != 2 && argc != 3) || geteuid() != 0) { fprintf(stderr, "Root access is required.\n"); return 2; }
    if (argc == 3 && (strlen(argv[2]) != 64 || strspn(argv[2], "0123456789abcdef") != 64)) return 2;
    struct stat status;
    if (lstat(argv[1], &status) || !S_ISREG(status.st_mode) || status.st_nlink != 1) return 2;
    pid_t provider;
    if (!find_pids("vendor.oculus.hardware.sensors@1.0-service", &provider, 1)) return 3;
    char path[80], line[1024];
    snprintf(path, sizeof path, "/proc/%d/maps", provider);
    FILE *maps = fopen(path, "re");
    if (!maps) return 3;
    while (fgets(line, sizeof line, maps)) if (argc == 2 && strstr(line, "libquestpro-camera-streamer-v10.so")) {
        fclose(maps); puts("CAPTURE_ATTACHED reused=1"); return 0;
    }
    fclose(maps);
    alarm(20);
    frida_init();
    FridaInjector *injector = frida_injector_new();
    GError *error = NULL;
    frida_injector_inject_library_file_sync(injector, provider, argv[1], argc == 3 ? "qft_export_main" : "qft_streamer_main", argc == 3 ? argv[2] : "", NULL, &error);
    int ok = error == NULL;
    if (error) { fprintf(stderr, "CAPTURE_ATTACH_FAILED %s\n", error->message); g_clear_error(&error); }
    frida_injector_close_sync(injector, NULL, &error);
    if (error) { fprintf(stderr, "CAPTURE_CLOSE_FAILED %s\n", error->message); g_clear_error(&error); }
    g_object_unref(injector);
    frida_deinit();
    if (ok) puts("CAPTURE_ATTACHED reused=0");
    return ok ? 0 : 4;
}
