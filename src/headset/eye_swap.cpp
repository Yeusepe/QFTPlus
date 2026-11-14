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

struct Value { void* payload; uint32_t tag; uint32_t padding; };
struct NativeString { alignas(8) unsigned char bytes[24]; };
struct Pair { void* bucket; uint64_t inserted; };
// A nontrivial return forces the same AArch64 indirect return as c10::Dict.
struct Handle { void* payload; ~Handle() {} };
struct Report {
    uint64_t before, after;
    int lock_result, attempts, result, dictionary_size;
    int stage, failed_stage;
};
static_assert(sizeof(Value) == 16 && sizeof(NativeString) == 24 && sizeof(Handle) == 8);

struct Engine {
    uintptr_t base;
    template<class F> F at(uintptr_t offset) { return reinterpret_cast<F>(base + offset); }
    void retain(void* p) { at<uint64_t(*)(uint64_t,void*)>(0x769750)(1, static_cast<char*>(p)+8); }
    void release(void* p) { at<void(*)(void*)>(0xc271a0)(p); }
    uint64_t& word(void* p, size_t offset) { return *reinterpret_cast<uint64_t*>(static_cast<char*>(p)+offset); }
    Value string(const char* bytes, size_t length) {
        Value v{};
        at<void(*)(Value*,const char*)>(0x12cd330)(&v, "x");
        if (v.tag != 12) { release(v.payload); throw 1; }
        auto* s = reinterpret_cast<NativeString*>(static_cast<char*>(v.payload)+16);
        std::memset(s, 0, sizeof(*s));
        try { at<void(*)(NativeString*,const char*,size_t)>(0x76d0d0)(s, bytes, length); }
        catch (...) { release(v.payload); throw; }
        const bool long_string=s->bytes[0]&1;
        const auto size=long_string?word(s,8):s->bytes[0]>>1;
        const auto* contents=long_string?reinterpret_cast<char*>(word(s,16)):reinterpret_cast<char*>(s->bytes)+1;
        if(size!=length || std::memcmp(contents,bytes,length)) { release(v.payload); throw 10; }
        return v;
    }
    void insert(void* dict, const char* key, const char* data, size_t length) {
        NativeString k{};
        const auto key_length = std::strlen(key);
        if (key_length > 22) throw 2;
        k.bytes[0] = key_length*2;
        std::memcpy(k.bytes+1, key, key_length);
        Value v = string(data,length);
        try {
            auto* map = static_cast<char*>(dict)+16;
            const auto hash = at<uint64_t(*)(void*,NativeString*)>(0x12cf910)(map,&k);
            const auto shift = *reinterpret_cast<unsigned char*>(map+16);
            if (shift > 63 || word(dict,24)>256) throw 3;
            auto index = (hash*UINT64_C(0x9e3779b97f4a7c15)) >> shift;
            auto* bucket = reinterpret_cast<char*>(word(dict,16))+index*56;
            int distance = 0;
            while (*reinterpret_cast<int8_t*>(bucket+16)>=distance) {
                if (++distance>32) throw 4;
                bucket+=56;
            }
            Value zero{nullptr,5,0};
            const auto old_size=word(dict,40);
            auto result=at<Pair(*)(void*,int,void*,NativeString*,Value*)>(0x24b4180)(map,distance,bucket,&k,&zero);
            if (!result.bucket || static_cast<uint8_t>(result.inserted)!=1 || word(dict,40)!=old_size+1) throw 5;
            auto* slot=reinterpret_cast<Value*>(static_cast<char*>(result.bucket)+40);
            if (slot->tag!=5) throw 6;
            *slot=v;
            v={};
        } catch (...) { if(v.payload)release(v.payload); throw; }
    }
    void clear(void* dict) {
        const auto mask=word(dict,24);
        const auto extra=*reinterpret_cast<int8_t*>(static_cast<char*>(dict)+33);
        if(mask>256 || extra<1 || extra>32 || word(dict,40)>3)throw 7;
        auto* buckets=reinterpret_cast<char*>(word(dict,16));
        // This reproduces the compiler's source-map cleanup, including overflow buckets.
        for(size_t i=0;i<mask+extra;i++) {
            auto* bucket=buckets+i*56;
            if(*reinterpret_cast<int8_t*>(bucket+16)<0)continue;
            auto* key=reinterpret_cast<Value*>(bucket+24);
            auto* value=reinterpret_cast<Value*>(bucket+40);
            if(key->tag!=12 || value->tag!=12)throw 8;
            release(value->payload);release(key->payload);
            *key={};*value={};bucket[16]=0xff;
        }
        auto* sentinel=reinterpret_cast<uint64_t*>(word(dict,56));
        sentinel[0]=sentinel[1]=reinterpret_cast<uint64_t>(sentinel);
        word(dict,40)=0;
    }
};

extern "C" __attribute__((visibility("default"))) int qft_eye_replace(
    uintptr_t base, void* backend, void* processed, void* spec,
    const char* json, size_t json_length, const char* bin, size_t bin_length,
    Report* report, const char* rollback) noexcept {
    Engine e{base};
    *report={};
    if(e.word(backend,0)!=base+0x2cd6838)return report->result=-10;
    if(json_length!=108564)return report->result=-16;
    if(bin_length!=266054)return report->result=-17;
    timespec deadline{};clock_gettime(CLOCK_REALTIME,&deadline);deadline.tv_sec+=2;
    auto* mutex=reinterpret_cast<pthread_mutex_t*>(static_cast<char*>(backend)+0x58);
    report->lock_result=pthread_mutex_timedlock(mutex,&deadline);
    if(report->lock_result)return report->result=-11;
    if(e.word(processed,40)!=0) {
        pthread_mutex_unlock(mutex);return report->result=-15;
    }
    report->before=e.word(backend,16);
    int result=-12;
    for(int attempt=0;attempt<2;attempt++) {
        report->attempts++;
        Value input{};
        try {
            report->stage=1;
            if(e.word(processed,40))e.clear(processed);
            report->stage=2;e.insert(processed,"backend","hexagon",7);
            report->stage=3;e.insert(processed,"json",attempt?rollback:json,json_length);
            report->stage=4;e.insert(processed,"bin",bin,bin_length);
            e.retain(processed);input={processed,15,0};
            report->stage=5;
            auto output=e.at<Handle(*)(void*,Value*,void**)>(0x24aeb30)(backend,&input,&spec);
            report->stage=6;
            if(output.payload)e.at<void(*)(void*)>(0xc9ef54)(output.payload);
            if(input.payload)e.release(input.payload);
            input={};
            if(e.word(processed,40) || !e.word(backend,16))throw 9;
            result=attempt?1:0;
            break;
        } catch (...) {
            report->failed_stage=report->stage;
            if(input.payload)e.release(input.payload);
            result=-13;
        }
    }
    try { if(e.word(processed,40))e.clear(processed); } catch (...) { result=-14; }
    report->after=e.word(backend,16);
    report->dictionary_size=e.word(processed,40);
    pthread_mutex_unlock(mutex);
    return report->result=result;
}

extern "C" __attribute__((visibility("default"))) int qft_eye_replay(
    void* backend, void* execute, void* domain, uint32_t graph,
    void* inputs, void* outputs, void* options) noexcept {
    timespec deadline{};clock_gettime(CLOCK_REALTIME,&deadline);deadline.tv_sec+=2;
    auto* mutex=reinterpret_cast<pthread_mutex_t*>(static_cast<char*>(backend)+0x58);
    if(pthread_mutex_timedlock(mutex,&deadline))return -100;
    uint32_t status1=0,status2=0;
    using Execute=int(*)(void*,uint32_t,void*,int,void*,int,void*,uint32_t*,uint32_t*);
    int result=reinterpret_cast<Execute>(execute)(domain,graph,inputs,1,outputs,5,options,&status1,&status2);
    pthread_mutex_unlock(mutex);
    return result;
}

struct EyeGuard {
    Engine engine;
    void *self, *backend, *processed, *spec;
    std::string stock, binary, status_path;
    pid_t owner;
    pthread_t thread;
    std::atomic<uint64_t> deadline;
    std::atomic<bool> closed{false};
    std::atomic<int> status{0};
};
static uint64_t monotonic_ms() {
    timespec now{};clock_gettime(CLOCK_MONOTONIC,&now);
    return uint64_t(now.tv_sec)*1000+now.tv_nsec/1000000;
}
static void* guard_run(void* argument) {
    auto* guard=static_cast<EyeGuard*>(argument);
    while(!guard->closed.load()) {
        const bool gone=kill(guard->owner,0)<0&&errno==ESRCH;
        if(gone||monotonic_ms()>guard->deadline.load()) {
            Report report{};
            const int code=qft_eye_replace(guard->engine.base,guard->backend,guard->processed,guard->spec,
                guard->stock.c_str(),guard->stock.size(),guard->binary.c_str(),guard->binary.size(),&report,guard->stock.c_str());
            guard->status.store(code>=0?1:-1);
            int status=open(guard->status_path.c_str(),O_WRONLY|O_TRUNC|O_CLOEXEC|O_NOFOLLOW);
            if(status>=0){dprintf(status,"GUARD_RESTORED %d\n",code);close(status);}
            __android_log_print(ANDROID_LOG_INFO,"QFTEyes","guard_restored code=%d owner=%d",code,guard->owner);
            return nullptr;
        }
        usleep(100000);
    }
    return nullptr;
}
extern "C" __attribute__((visibility("default"))) void* qft_eye_guard_start(
    uintptr_t base,void* self,void* processed,void* spec,const char* stock,size_t graph_length,
    const char* binary,size_t binary_length,int owner,const char* status_path) noexcept {
    if(graph_length!=108564||binary_length!=266054||owner<=1)return nullptr;
    EyeGuard* guard=nullptr;
    try {
        guard=new EyeGuard;
        guard->engine={base};guard->self=self;guard->processed=processed;guard->spec=spec;guard->owner=owner;
        guard->backend=**reinterpret_cast<void***>(static_cast<char*>(self)+0x50);
        guard->stock.assign(stock,graph_length);guard->binary.assign(binary,binary_length);
        guard->status_path=status_path;
        guard->deadline.store(monotonic_ms()+5000);
        guard->engine.retain(self);guard->engine.retain(processed);guard->engine.retain(spec);
        if(!pthread_create(&guard->thread,nullptr,guard_run,guard))return guard;
        guard->engine.release(self);guard->engine.release(processed);guard->engine.release(spec);
    } catch (...) {}
    delete guard;return nullptr;
}
extern "C" __attribute__((visibility("default"))) int qft_eye_guard_beat(void* pointer) noexcept {
    auto* guard=static_cast<EyeGuard*>(pointer);
    const int status=guard->status.load();
    if(!status)guard->deadline.store(monotonic_ms()+5000);
    return status;
}
extern "C" __attribute__((visibility("default"))) int qft_eye_guard_status(void* pointer) noexcept {
    return static_cast<EyeGuard*>(pointer)->status.load();
}
extern "C" __attribute__((visibility("default"))) void qft_eye_guard_close(void* pointer) noexcept {
    auto* guard=static_cast<EyeGuard*>(pointer);
    guard->closed.store(true);pthread_join(guard->thread,nullptr);
    guard->engine.release(guard->self);guard->engine.release(guard->processed);guard->engine.release(guard->spec);
    delete guard;
}
