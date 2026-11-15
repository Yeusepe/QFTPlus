#include <cstdint>
#include <cstring>
#include <pthread.h>
#include <time.h>
#include <signal.h>
#include <unistd.h>
#include <cerrno>
#include <string>
#include <atomic>
#include <android/log.h>
#include <fcntl.h>
#include "eye_profile.h"

static QftEyeProfile profile{};
static uintptr_t engine;
static_assert(sizeof(QftEyeProfile)==(QFT_EYE_CODE_COUNT+6)*8);
template<class T> static T& field(void* p,size_t offset) {return *reinterpret_cast<T*>(static_cast<char*>(p)+offset);}
static void retain(void* p) {reinterpret_cast<uint64_t(*)(uint64_t,void*)>(engine+profile.code[QFT_RETAIN])(1,static_cast<char*>(p)+8);}
static void release(void* p) {reinterpret_cast<void(*)(void*)>(engine+profile.code[QFT_RELEASE])(p);}
extern "C" __attribute__((visibility("default"))) int qft_eye_configure(
    uintptr_t base,size_t module_size,const QftEyeProfile* candidate,size_t length) noexcept {
    if(!base||!candidate||length!=sizeof(*candidate))return 0;
    for(unsigned i:{QFT_RETAIN,QFT_RELEASE,QFT_MODEL_PREPARE,QFT_MODEL_STOP})
        if(!candidate->code[i]||candidate->code[i]>=module_size)return 0;
    if(!candidate->backend_vtable||candidate->backend_vtable>=module_size||!candidate->model_vtable||candidate->model_vtable>=module_size)return 0;
    if(engine)return engine==base&&!std::memcmp(&profile,candidate,length);
    profile=*candidate;engine=base;return 1;
}

struct Status {uint64_t code,first,second;};
static_assert(sizeof(Status)==24);
struct Transition {uint32_t graph;bool active=true,observed=false,invalid=false;};
struct EyeGuard {
    void *self,*backend,*model,*weights;
    uint32_t *edge,original,independent;
    uint64_t capacity;
    int priority;
    pid_t owner;
    std::string status_path;
    pthread_t thread;
    std::atomic<uint64_t> deadline;
    std::atomic<bool> closed{false};
    std::atomic<int> status{0};
    Transition transition{};
};
static uint64_t monotonic_ms() {
    timespec now{};clock_gettime(CLOCK_MONOTONIC,&now);
    return uint64_t(now.tv_sec)*1000+now.tv_nsec/1000000;
}
static int lock(void* backend) {
    timespec deadline{};clock_gettime(CLOCK_REALTIME,&deadline);deadline.tv_sec+=2;
    return pthread_mutex_timedlock(&field<pthread_mutex_t>(backend,0x58),&deadline);
}
static bool identity(EyeGuard* g) {
    return field<uintptr_t>(g->backend,0)==engine+profile.backend_vtable&&field<void*>(g->backend,16)==g->model&&
        field<uintptr_t>(g->model,0)==engine+profile.model_vtable&&field<void*>(g->backend,0x88)==g->weights&&
        field<uint64_t>(g->backend,0x80)==g->capacity&&field<void*>(g->model,0x180)==g->weights&&
        (*g->edge==g->original||*g->edge==g->independent);
}
static bool success(Status s) {return !s.code&&!s.first&&!s.second;}
static thread_local Transition* transition;
extern "C" __attribute__((visibility("default"))) int qft_eye_task_options(const void* original,void* copy) noexcept {
    if(!transition||!original||!copy)return 0;
    // Return SDK errors normally so its watchdog and our rollback can finish.
    std::memcpy(copy,original,32);
    field<uint8_t>(copy,0x11)=1;
    return 1;
}
extern "C" __attribute__((visibility("default"))) void qft_eye_graph_event(int event,uint32_t graph,int result) noexcept {
    auto* t=transition;if(!t||result)return;
    t->observed=true;
    if(event==0){if(t->active)t->invalid=true;t->graph=graph;t->active=true;}
    else if(event==1){if(!t->active||t->graph!=graph)t->invalid=true;else t->active=false;}
    else if(event==2&&(!t->active||t->graph!=graph))t->invalid=true;
}
static int restore_locked(EyeGuard* g) {
    auto& current=g->transition;
    if(current.invalid||!identity(g))return -2;
    auto stop=reinterpret_cast<Status(*)(void*)>(engine+profile.code[QFT_MODEL_STOP]);
    auto prepare=reinterpret_cast<Status(*)(void*,int,int)>(engine+profile.code[QFT_MODEL_PREPARE]);
    bool clean=field<uint8_t>(g->model,0x1f0)==0&&current.observed&&!current.active;
    // A failed prepare can own a DSP graph before its prepared flag is set.
    if(field<uint8_t>(g->model,0x1f0)==1||(current.observed&&current.active&&field<uint32_t>(g->model,0x1c0)==current.graph)) {
        field<uint8_t>(g->model,0x1f0)=1;
        clean=success(stop(g->model))&&field<uint8_t>(g->model,0x1f0)==0&&(!current.observed||!current.active);
    }
    if(clean) {
        *g->edge=g->original;
        if(success(prepare(g->model,g->priority,0))&&identity(g)&&field<uint8_t>(g->model,0x1f0)==1&&!current.invalid)return 1;
    }
    return -2;
}
static int change(EyeGuard* g,uint32_t target,bool require_events=false) noexcept {
    if(lock(g->backend))return -1;
    int result=-2;
    try {
        if(identity(g)&&field<uint8_t>(g->model,0x1f0)==1) {
            if(*g->edge==target)result=0;
            else {
                auto stop=reinterpret_cast<Status(*)(void*)>(engine+profile.code[QFT_MODEL_STOP]);
                auto prepare=reinterpret_cast<Status(*)(void*,int,int)>(engine+profile.code[QFT_MODEL_PREPARE]);
                g->transition={field<uint32_t>(g->model,0x1c0)};
                auto& current=g->transition;
                transition=&current;
                try {
                    if(success(stop(g->model))&&field<uint8_t>(g->model,0x1f0)==0) {
                        *g->edge=target;
                        if(success(prepare(g->model,g->priority,0))&&identity(g)&&field<uint8_t>(g->model,0x1f0)==1&&!current.invalid&&(!require_events||current.observed))result=0;
                    }
                } catch (...) {result=-3;}
                if(result<0&&!current.invalid) {
                    try {result=restore_locked(g);} catch (...) {result=-3;}
                }
                transition=nullptr;
            }
        }
    } catch (...) {result=-3;}
    pthread_mutex_unlock(&field<pthread_mutex_t>(g->backend,0x58));
    return result;
}
static void* guard_run(void* argument) {
    auto* g=static_cast<EyeGuard*>(argument);
    while(!g->closed.load()) {
        if((kill(g->owner,0)<0&&errno==ESRCH)||monotonic_ms()>g->deadline.load()) {
            const int code=change(g,g->original);
            g->status.store(code>=0?1:-1);
            int status=open(g->status_path.c_str(),O_WRONLY|O_TRUNC|O_CLOEXEC|O_NOFOLLOW,0600);
            if(status>=0){dprintf(status,"GUARD_RESTORED %d\n",code);close(status);}
            __android_log_print(ANDROID_LOG_INFO,"QFTEyes","guard_restored code=%d owner=%d",code,g->owner);
            return nullptr;
        }
        usleep(100000);
    }
    return nullptr;
}
extern "C" __attribute__((visibility("default"))) void* qft_eye_guard_start(
    void* self,void* model,uint32_t* edge,uint32_t original,uint32_t independent,int owner,const char* status_path) noexcept {
    if(!engine||!self||!model||!edge||original==independent||owner<=1||!status_path)return nullptr;
    EyeGuard* g=nullptr;
    try {
        g=new EyeGuard;
        g->self=self;g->backend=**reinterpret_cast<void***>(static_cast<char*>(self)+0x50);
        g->model=model;g->edge=edge;g->original=original;g->independent=independent;g->owner=owner;
        g->weights=field<void*>(g->backend,0x88);g->capacity=field<uint64_t>(g->backend,0x80);
        g->priority=field<int>(model,0x210);g->status_path=status_path;
        const uintptr_t begin=field<uintptr_t>(model,0xd8),end=field<uintptr_t>(model,0xe0);
        bool found=false;
        if(end>=begin&&end-begin<=104*4096&&(end-begin)%104==0)
            for(uintptr_t p=begin;p<end;p+=104)if(field<uint32_t*>(reinterpret_cast<void*>(p),56)==edge&&field<uintptr_t>(reinterpret_cast<void*>(p),64)==reinterpret_cast<uintptr_t>(edge)+16)found=true;
        if(!found||!identity(g)||*edge!=original||field<uint8_t>(model,0x1f0)!=1){delete g;return nullptr;}
        g->deadline.store(monotonic_ms()+5000);
        retain(self);
        if(!pthread_create(&g->thread,nullptr,guard_run,g))return g;
        release(self);
    } catch (...) {}
    delete g;return nullptr;
}
extern "C" __attribute__((visibility("default"))) int qft_eye_set(void* pointer,int enabled) noexcept {
    auto* g=static_cast<EyeGuard*>(pointer);
    if(enabled&&g->status.load()!=0)return -4;
    return change(g,enabled?g->independent:g->original,true);
}
extern "C" __attribute__((visibility("default"))) int qft_eye_guard_beat(void* pointer) noexcept {
    auto* g=static_cast<EyeGuard*>(pointer);const int status=g->status.load();
    if(!status)g->deadline.store(monotonic_ms()+5000);
    return status;
}
extern "C" __attribute__((visibility("default"))) int qft_eye_guard_status(void* pointer) noexcept {return static_cast<EyeGuard*>(pointer)->status.load();}
extern "C" __attribute__((visibility("default"))) int qft_eye_guard_close(void* pointer) noexcept {
    auto* g=static_cast<EyeGuard*>(pointer);
    g->closed.store(true);pthread_join(g->thread,nullptr);
    if(change(g,g->original)<0)return -1;
    release(g->self);delete g;return 0;
}
extern "C" __attribute__((visibility("default"))) int qft_eye_replay(
    void* backend,void* execute,void* domain,uint32_t graph,void* inputs,void* outputs,void* options) noexcept {
    if(lock(backend))return -100;
    uint32_t status1=0,status2=0;
    using Execute=int(*)(void*,uint32_t,void*,int,void*,int,void*,uint32_t*,uint32_t*);
    const int result=reinterpret_cast<Execute>(execute)(domain,graph,inputs,1,outputs,5,options,&status1,&status2);
    pthread_mutex_unlock(&field<pthread_mutex_t>(backend,0x58));
    return result;
}
