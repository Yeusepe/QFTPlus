#define _GNU_SOURCE
#include "face_model.h"
#include "area_resize.h"
#include <dlfcn.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "onnxruntime_c_api.h"

typedef struct { uint32_t id, index; } NNInput;
typedef struct { uint32_t rank, dims[8], capacity; } NNOutput;
typedef struct { uint32_t dims[8], rank, type; void *data; int32_t bytes; } NNTensor;
_Static_assert(sizeof(NNTensor) == 56, "Meta tensor ABI");
_Static_assert(sizeof(NNOutput) == 40, "Meta output ABI");

typedef struct {
    uint32_t id, in[4], out[4];
    uint8_t *input, *output;
} Graph;

struct FaceModel {
    void *library, *ort_library;
    uint64_t handle;
    int (*close)(uint64_t);
    int (*destroy)(uint64_t, uint32_t);
    int (*execute)(uint64_t, uint32_t, const NNTensor *, int, NNTensor *, int,
                   const int32_t *, int32_t *, int32_t *);
    Graph graphs[3];
    uint8_t resize[256], mouth[256], brows[256];
    uint8_t *mouth_output, *brow_output;
    const OrtApi *ort;
    OrtEnv *env;
    OrtSession *session;
    OrtValue *inputs[2], *outputs[2];
    float result[999];
};

static const char *input_names[] = {
    "/model/mouth/mouth.15/Mul_output_0_QuantizeLinear_Output",
    "/model/brows/brows.15/Mul_output_0_QuantizeLinear_Output"
};
static const char *output_names[] = {"mouth", "brows"};
static const int32_t options[4] = {0, 2, -1, 0};

static int ort_ok(FaceModel *m, OrtStatus *status) {
    if (!status) return 1;
    fprintf(stderr, "QFT_ERROR: Headset tail: %s\n", m->ort->GetErrorMessage(status));
    m->ort->ReleaseStatus(status);
    return 0;
}

static int read_data(FILE *file, void *data, size_t bytes) {
    return fread(data, 1, bytes, file) == bytes;
}
static int read_u32(FILE *file, uint32_t *data, size_t count) {
    return read_data(file, data, count * sizeof(*data));
}
static size_t elements(const uint32_t *dims) {
    size_t size = 1;
    for (int i = 0; i < 4; i++) {
        if (!dims[i] || dims[i] > 8192 || size > 16000000 / dims[i]) return 0;
        size *= dims[i];
    }
    return size;
}
static void *allocate(size_t bytes) {
    void *pointer = NULL;
    if (!bytes || posix_memalign(&pointer, 128, bytes)) return NULL;
    return pointer;
}

FaceModel *face_model_open(const char *directory) {
    FaceModel *m = calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->handle = UINT64_MAX;
    FILE *file = NULL;
    OrtSessionOptions *settings = NULL;
    OrtMemoryInfo *memory = NULL;
    char path[1024];
#define REQUIRE(test) do { if (!(test)) { fprintf(stderr, "QFT_ERROR: Headset model setup failed at %s:%d\n", __FILE__, __LINE__); goto fail; } } while (0)
    m->library = dlopen("/vendor/lib64/libhexagon.so", RTLD_NOW | RTLD_LOCAL);
    REQUIRE(m->library);
    int (*open_nn)(const char *, uint64_t *) = dlsym(m->library, "hexagon_open");
    int (*start)(uint64_t) = dlsym(m->library, "hexagon_nnStart");
    int (*init)(uint64_t, uint32_t *) = dlsym(m->library, "hexagon_nnInit");
    int (*constant)(uint64_t, uint32_t, uint32_t, const NNTensor *) = dlsym(m->library, "hexagon_nnAddConstNode");
    int (*node)(uint64_t, uint32_t, uint32_t, const char *, uint32_t, const NNInput *, int, const NNOutput *, int) = dlsym(m->library, "hexagon_nnAddNodeByOpName");
    int (*prepare)(uint64_t, uint32_t, const int32_t *, int32_t *, int32_t *) = dlsym(m->library, "hexagon_nnPrepare");
    m->close = dlsym(m->library, "hexagon_close");
    m->destroy = dlsym(m->library, "hexagon_nnDestroy");
    m->execute = dlsym(m->library, "hexagon_nnExecute");
    REQUIRE(open_nn && start && init && constant && node && prepare && m->close && m->destroy && m->execute);
    REQUIRE(!open_nn("file:///libhexagon_skel.so?hexagon_skel_handle_invoke&_modver=1.0&_dom=cdsp", &m->handle));
    REQUIRE(!start(m->handle));
    REQUIRE(snprintf(path, sizeof path, "%s/model.bin", directory) < (int)sizeof path);
    file = fopen(path, "rb");
    REQUIRE(file);
    unsigned char header[40];
    REQUIRE(read_data(file, header, sizeof header) && !memcmp(header, "QFTHM001", 8));
    for (int c = 0; c < 64; c++) {
        uint32_t count, offset, weight;
        REQUIRE(read_u32(file, &count, 1) && count == 16);
        const unsigned y = (unsigned)c / 8, x = (unsigned)c % 8;
        const unsigned wy[4] = {8-y, 8, 8, 1+y}, wx[4] = {8-x, 8, 8, 1+x};
        for (unsigned i = 0; i < count; i++) {
            REQUIRE(read_u32(file, &offset, 1) && read_u32(file, &weight, 1));
            REQUIRE(offset == (y*25/8+i/4)*25+x*25/8+i%4 && weight == wy[i/4]*wx[i%4]);
        }
    }
    REQUIRE(read_data(file, m->resize, 256) && read_data(file, m->mouth, 256) && read_data(file, m->brows, 256));
    for (int g = 0; g < 3; g++) {
        Graph *graph = &m->graphs[g];
        uint32_t counts[2], previous = 1;
        REQUIRE(read_u32(file, graph->in, 4) && read_u32(file, graph->out, 4) && read_u32(file, counts, 2));
        REQUIRE(elements(graph->in) && elements(graph->out) && counts[0] <= 1024 && counts[1] <= 128);
        REQUIRE(!init(m->handle, &graph->id));
        for (uint32_t c = 0; c < counts[0]; c++) {
            uint32_t definition[12];
            REQUIRE(read_u32(file, definition, 12));
            REQUIRE(definition[0] > 1 && definition[1] > 0 && definition[1] <= 8 && definition[11] <= 8000000);
            NNTensor tensor = {.rank = definition[1], .type = definition[10], .bytes = (int32_t)definition[11]};
            memcpy(tensor.dims, definition + 2, sizeof tensor.dims);
            if (tensor.bytes) {
                tensor.data = allocate((size_t)tensor.bytes);
                if (!tensor.data || !read_data(file, tensor.data, (size_t)tensor.bytes)) { free(tensor.data); goto fail; }
            }
            int rc = constant(m->handle, graph->id, definition[0], &tensor);
            free(tensor.data);
            REQUIRE(!rc);
        }
        NNOutput input = {.rank = 4, .capacity = (uint32_t)elements(graph->in)};
        memcpy(input.dims, graph->in, sizeof graph->in);
        REQUIRE(!node(m->handle, graph->id, 1, "INPUT", 0, NULL, 0, &input, 1));
        for (uint32_t n = 0; n < counts[1]; n++) {
            uint32_t ident, length, count, dimensions[4];
            char name[80] = {0};
            NNInput inputs[16] = {0};
            REQUIRE(read_u32(file, &ident, 1) && read_u32(file, &length, 1) && length && length < sizeof name);
            REQUIRE(read_data(file, name, length) && read_u32(file, &count, 1) && count <= 16);
            REQUIRE(!strcmp(name, "QuantizedConv2dPerChannel_8x8to8") || !strcmp(name, "Activation_8to8") || !strcmp(name, "Caffe2_QuantizedAdd_8p8to8"));
            for (uint32_t i = 0; i < count; i++) REQUIRE(read_u32(file, &inputs[i].id, 1));
            REQUIRE(read_u32(file, dimensions, 4) && elements(dimensions));
            NNOutput outputs[3] = {{.rank = 4, .capacity = (uint32_t)elements(dimensions)},
                {.rank = 4, .dims = {1,1,1,1}, .capacity = 4}, {.rank = 4, .dims = {1,1,1,1}, .capacity = 4}};
            memcpy(outputs[0].dims, dimensions, sizeof dimensions);
            REQUIRE(!node(m->handle, graph->id, ident, name, 0, inputs, (int)count, outputs, 3));
            previous = ident;
        }
        NNInput output = {previous, 0};
        REQUIRE(!node(m->handle, graph->id, previous + 1, "OUTPUT", 0, &output, 1, NULL, 0));
        int32_t status = -1, failed = -1;
        int rc = prepare(m->handle, graph->id, options, &status, &failed);
        fprintf(stderr, "DSP_PREPARE partition=%d rpc=%d status=%d node=%d\n", g, rc, status, failed);
        REQUIRE(!rc && !status);
        graph->input = allocate(elements(graph->in));
        graph->output = allocate(elements(graph->out));
        REQUIRE(graph->input && graph->output);
    }
    REQUIRE(fgetc(file) == EOF);
    fclose(file); file = NULL;
    const uint32_t expected_in[3][4] = {{5,128,128,1}, {2,16,16,96}, {3,16,16,96}};
    const uint32_t expected_out[3][4] = {{5,16,16,96}, {2,8,8,160}, {3,8,8,160}};
    for (int i = 0; i < 3; i++) REQUIRE(!memcmp(m->graphs[i].in, expected_in[i], 16) && !memcmp(m->graphs[i].out, expected_out[i], 16));
    REQUIRE(snprintf(path, sizeof path, "%s/libonnxruntime.so", directory) < (int)sizeof path);
    m->ort_library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    REQUIRE(m->ort_library);
    const OrtApiBase *(*api_base)(void) = dlsym(m->ort_library, "OrtGetApiBase");
    REQUIRE(api_base);
    m->ort = api_base()->GetApi(ORT_API_VERSION);
    REQUIRE(m->ort);
    REQUIRE(ort_ok(m, m->ort->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "qft-headset", &m->env)));
    REQUIRE(ort_ok(m, m->ort->CreateSessionOptions(&settings)));
    REQUIRE(ort_ok(m, m->ort->SetIntraOpNumThreads(settings, 1)));
    REQUIRE(ort_ok(m, m->ort->SetInterOpNumThreads(settings, 1)));
    REQUIRE(ort_ok(m, m->ort->AddSessionConfigEntry(settings, "session.intra_op.allow_spinning", "0")));
    REQUIRE(ort_ok(m, m->ort->SetSessionGraphOptimizationLevel(settings, ORT_ENABLE_ALL)));
    REQUIRE(snprintf(path, sizeof path, "%s/tail.onnx", directory) < (int)sizeof path);
    REQUIRE(ort_ok(m, m->ort->CreateSession(m->env, path, settings, &m->session)));
    m->ort->ReleaseSessionOptions(settings); settings = NULL;
    REQUIRE(ort_ok(m, m->ort->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &memory)));
    m->mouth_output = allocate(2 * 160 * 64);
    m->brow_output = allocate(3 * 160 * 64);
    REQUIRE(m->mouth_output && m->brow_output);
    void *buffers[] = {m->mouth_output, m->brow_output};
    int64_t shapes[2][4] = {{2,160,8,8}, {3,160,8,8}};
    size_t sizes[] = {2 * 160 * 64, 3 * 160 * 64};
    for (int i = 0; i < 2; i++) REQUIRE(ort_ok(m, m->ort->CreateTensorWithDataAsOrtValue(memory, buffers[i], sizes[i], shapes[i], 4,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8, &m->inputs[i])));
    int64_t lengths[] = {512, 480};
    for (int i = 0; i < 2; i++) {
        int64_t shape[2] = {1, lengths[i]};
        REQUIRE(ort_ok(m, m->ort->CreateTensorWithDataAsOrtValue(memory, m->result + (i ? 519 : 0), (size_t)lengths[i] * sizeof(float), shape, 2,
            ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &m->outputs[i])));
    }
    m->ort->ReleaseMemoryInfo(memory);
    return m;
fail:
    if (file) fclose(file);
    if (settings && m->ort) m->ort->ReleaseSessionOptions(settings);
    if (memory && m->ort) m->ort->ReleaseMemoryInfo(memory);
    face_model_close(m);
    return NULL;
#undef REQUIRE
}

static int execute_graph(FaceModel *m, Graph *g) {
    NNTensor input = {.rank = 4, .type = 1, .data = g->input, .bytes = (int32_t)elements(g->in)};
    NNTensor output = {.data = g->output, .bytes = (int32_t)elements(g->out)};
    memcpy(input.dims, g->in, sizeof g->in);
    int32_t status = -1, failed = -1;
    int rc = m->execute(m->handle, g->id, &input, 1, &output, 1, options, &status, &failed);
    if (rc || status || output.rank != 4 || memcmp(output.dims, g->out, sizeof g->out)) {
        fprintf(stderr, "QFT_ERROR: DSP execution rpc=%d status=%d node=%d\n", rc, status, failed);
        return 0;
    }
    return 1;
}

static uint64_t stage_ns[5];
static uint64_t clock_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000u + (uint64_t)t.tv_nsec; }
void face_model_stages(uint64_t out[5]) { memcpy(out, stage_ns, sizeof stage_ns); memset(stage_ns, 0, sizeof stage_ns); }
int face_model_run(FaceModel *m, const uint8_t *strip, float output[999]) {
    uint64_t t = clock_ns(), u;
    resize_neon(strip, m->graphs[0].input, m->resize);
    u = clock_ns(); stage_ns[0] += u - t; t = u;
    if (!execute_graph(m, &m->graphs[0])) return 0;
    u = clock_ns(); stage_ns[1] += u - t; t = u;
    const unsigned plane = 16 * 16 * 96;
    for (unsigned i = 0; i < 2 * plane; i++) {
        uint8_t value = m->mouth[m->graphs[0].output[2 * plane + i]];
        m->graphs[1].input[i] = value;
    }
    const unsigned cameras[] = {0, 1, 4};
    for (unsigned b = 0; b < 3; b++) for (unsigned i = 0; i < plane; i++)
        m->graphs[2].input[b * plane + i] = m->brows[m->graphs[0].output[cameras[b] * plane + i]];
    for (int g = 1; g < 3; g++) {
        if (!execute_graph(m, &m->graphs[g])) return 0;
        u = clock_ns(); stage_ns[1 + g] += u - t; t = u;
        uint8_t *to = g == 1 ? m->mouth_output : m->brow_output;
        for (unsigned b = 0; b < (g == 1 ? 2u : 3u); b++) for (unsigned p = 0; p < 64; p++) for (unsigned c = 0; c < 160; c++)
            to[(b * 160 + c) * 64 + p] = m->graphs[g].output[(b * 64 + p) * 160 + c];
    }
    if (!ort_ok(m, m->ort->Run(m->session, NULL, input_names, (const OrtValue *const *)m->inputs, 2, output_names, 2, m->outputs))) return 0;
    stage_ns[4] += clock_ns() - t;
    for (int i = 0; i < 999; i++) if (!isfinite(m->result[i])) return 0;
    memcpy(output, m->result, sizeof m->result);
    return 1;
}

void face_model_close(FaceModel *m) {
    if (!m) return;
    if (m->ort) {
        for (int i = 0; i < 2; i++) {
            if (m->inputs[i]) m->ort->ReleaseValue(m->inputs[i]);
            if (m->outputs[i]) m->ort->ReleaseValue(m->outputs[i]);
        }
        if (m->session) m->ort->ReleaseSession(m->session);
        if (m->env) m->ort->ReleaseEnv(m->env);
    }
    for (int i = 0; i < 3; i++) {
        if (m->graphs[i].id && m->destroy) m->destroy(m->handle, m->graphs[i].id);
        free(m->graphs[i].input); free(m->graphs[i].output);
    }
    if (m->handle != UINT64_MAX && m->close) m->close(m->handle);
    free(m->mouth_output); free(m->brow_output);
    if (m->ort_library) dlclose(m->ort_library);
    if (m->library) dlclose(m->library);
    free(m);
}
