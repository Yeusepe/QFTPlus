#include <aaudio/AAudio.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static int fail(const char *what, aaudio_result_t result) {
    fprintf(stderr, "QFT_ERROR: %s: %s\n", what, AAudio_convertResultToText(result));
    return 1;
}

int main(int argc, char **argv) {
    int rate = argc == 3 && !strcmp(argv[1], "--rate") ? atoi(argv[2]) : 16000;
    if (rate < 8000 || rate > 48000) { fprintf(stderr, "usage: %s [--rate 8000-48000]\n", argv[0]); return 2; }
    AAudioStreamBuilder *builder;
    AAudioStream *stream;
    aaudio_result_t result = AAudio_createStreamBuilder(&builder);
    if (result != AAUDIO_OK) return fail("builder", result);
    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_INPUT);
    AAudioStreamBuilder_setSampleRate(builder, rate);
    AAudioStreamBuilder_setChannelCount(builder, 1);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setInputPreset(builder, AAUDIO_INPUT_PRESET_VOICE_RECOGNITION);
    result = AAudioStreamBuilder_openStream(builder, &stream);
    AAudioStreamBuilder_delete(builder);
    if (result != AAUDIO_OK) return fail("open", result);
    if ((result = AAudioStream_requestStart(stream)) != AAUDIO_OK) return fail("start", result);
    rate = AAudioStream_getSampleRate(stream);
    fprintf(stderr, "QFT_MIC_READY %d\n", rate);
    int frames = rate / 100;
    int16_t *block = malloc(frames * sizeof *block);
    for (;;) {
        aaudio_result_t read = AAudioStream_read(stream, block, frames, 1000000000LL);
        if (read < 0) return fail("read", read);
        for (ssize_t at = 0, size = read * (ssize_t)sizeof *block; at < size;) {
            ssize_t n = write(STDOUT_FILENO, (char *)block + at, size - at);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) { AAudioStream_close(stream); return 0; }
            at += n;
        }
    }
}
