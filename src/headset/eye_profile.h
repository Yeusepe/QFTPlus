#pragma once
#include <stdint.h>
enum QftEyeCode {
    QFT_EXECUTE,
    QFT_RETAIN,
    QFT_RELEASE,
    QFT_ASSERT,
    QFT_DETECTOR,
    QFT_FILTER_PROCESS,
    QFT_FILTER_DESTROY,
    QFT_FILTER_APPLY,
    QFT_MODEL_PREPARE,
    QFT_MODEL_STOP,
    QFT_TASK_DISPATCH,
    QFT_TASK_CONSTRUCT,
    QFT_TASK_RUN,
    QFT_TASK_ERROR,
    QFT_EYE_CODE_COUNT
};
struct QftEyeProfile {
    uint64_t code[QFT_EYE_CODE_COUNT];
    uint64_t backend_vtable, filter_vtable, trace[2], axis, model_vtable;
    uint64_t lengths[QFT_EYE_CODE_COUNT], task_options_size;
};
