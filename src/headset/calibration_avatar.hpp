#pragma once
#include <dlfcn.h>
#include <mutex>

namespace avatar {
enum : int32_t { Success = 0 };
struct Vec3 { float x, y, z; };
struct Quat { float x, y, z, w; };
struct Transform { Vec3 position; Quat orientation; Vec3 scale; };
struct Init {
    uint32_t version[5]; const char* clientVersion; int32_t platform, flags, loggingLevel;
    void *loggingCallback, *loggingContext, *alloc, *free, *memoryContext, *request, *fileOpen, *fileRead, *fileClose, *fileContext, *resource, *resourceContext;
    const char* fallbackAssetsFolder; uint32_t workers; int64_t maxRequests, maxSend, maxReceive;
    Vec3 defaultColor, right, up, forward; const char* clientName; uint32_t networkHz, reserved1;
    void* reserved2[5]; void* loggingView; void* reserved7; int64_t reserved8; void* reserved9[3]; const char* extraJson;
};
struct Filters { int32_t lod, manifestation, view, subMeshes, quality; uint8_t rigFromGlb, reserved[32]; int32_t subMeshVertex; };
struct Create { int32_t features; Filters filters; int32_t lod; };
struct Load { Filters filters; uint32_t network[6], textureMemory, morphLods; uint8_t reserved[4]; };
struct Resource { int32_t status, id; };
struct Request { int32_t id, entity, state, failure, type; int64_t response; };
struct Pose { uint32_t count; Transform *local, *object; int32_t* parents; int32_t* nodes; };
struct EntityState { Transform root; uint32_t primitives; int32_t hierarchy, allVersion, visibleVersion; void *all, *visible; uint32_t allCount, visibleCount; };
struct PrimitiveState { int32_t id, primitive, node; Transform local, world; Pose pose; uint32_t morphCount; Transform skinOrigin; };
struct Primitive { int32_t id, vertices, morphs, compact; uint32_t indexCount; uint16_t minIndex, maxIndex; int32_t alpha; uint32_t textures, joints, skeleton, cost; };
struct Image { int32_t id; uint32_t format, width, height, mips, size; };
struct MaterialTexture { int32_t type; float factor[4]; int32_t image; };
struct JointInfo { int32_t joint; float inverseBind[16]; };
struct ExtensionEntry { int32_t type; uint32_t nameSize, dataSize; };
struct FacePose { float weights[72], confidence[72]; };
struct FaceProvider { void* context; bool (*callback)(FacePose*, void*); };

enum Expression { CheekPuffL = 2, CheekPuffR = 3, CheekRaiserL = 4, CheekSuckL = 6, CheekSuckR = 7, EyesClosedL = 12, EyesClosedR = 13, EyesLookDownL = 14, EyesLookDownR = 15,
    EyesLookLeftL = 16, EyesLookLeftR = 17, EyesLookRightL = 18, EyesLookRightR = 19, EyesLookUpL = 20, EyesLookUpR = 21, InnerBrowRaiserL = 22, JawDrop = 24,
    LipFunnelerLB = 34, LipFunnelerLT = 35, LipFunnelerRB = 36, LipFunnelerRT = 37, LipPressorL = 38, LipPuckerL = 40, LipPuckerR = 41, UpperLidRaiserL = 59, Count = 72 };

struct Api {
    void* library = nullptr;
#define QFT_AVATAR_API(X) \
    X(int32_t, ovrAvatar2_Initialize, const Init*) X(int32_t, ovrAvatar2_Shutdown) X(int32_t, ovrAvatar2_Update, float) \
    X(int32_t, ovrAvatar2Entity_Create, const Create*, int32_t*) X(int32_t, ovrAvatar2Entity_Destroy, int32_t) \
    X(Load, ovrAvatar2Entity_DefaultLoadSettings) \
    X(int32_t, ovrAvatar2Entity_LoadMemory, int32_t, const void*, uint32_t, const char*, Load, int32_t*) \
    X(int32_t, ovrAvatar2Entity_GetNodeName, int32_t, int32_t, char*, uint32_t, uint32_t*) \
    X(int32_t, ovrAvatar2Asset_GetLoadRequestInfo, int32_t, Request*) X(int32_t, ovrAvatar2Asset_ResourceReadyToRender, int32_t) \
    X(int32_t, ovrAvatar2Asset_GetPrimitiveCount, int32_t, uint32_t*) X(int32_t, ovrAvatar2Asset_GetPrimitiveByIndex, int32_t, uint32_t, Primitive*) \
    X(int32_t, ovrAvatar2Asset_GetImageCount, int32_t, uint32_t*) X(int32_t, ovrAvatar2Asset_GetImageByIndex, int32_t, uint32_t, Image*) \
    X(int32_t, ovrAvatar2Asset_GetImageDataByIndex, int32_t, uint32_t, void*, uint32_t) \
    X(int32_t, ovrAvatar2Primitive_GetIndexData, int32_t, uint16_t*, uint32_t) \
    X(int32_t, ovrAvatar2Primitive_GetMaterialTextureByIndex, int32_t, uint32_t, MaterialTexture*) \
    X(int32_t, ovrAvatar2Primitive_GetJointInfo, int32_t, JointInfo*, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetVertexCount, int32_t, uint32_t*) \
    X(int32_t, ovrAvatar2VertexBuffer_GetPositions, int32_t, void*, uint32_t, uint32_t) X(int32_t, ovrAvatar2VertexBuffer_GetNormals, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetTangents, int32_t, void*, uint32_t, uint32_t) X(int32_t, ovrAvatar2VertexBuffer_GetColors, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetColorsORMT, int32_t, void*, uint32_t, uint32_t) X(int32_t, ovrAvatar2VertexBuffer_GetTexCoord0, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetJointIndices, int32_t, void*, uint32_t, uint32_t) X(int32_t, ovrAvatar2VertexBuffer_GetJointWeights, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetTexCoord2, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetMaterialTypesFloat, int32_t, int32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2Primitive_GetNumMaterialExtensions, int32_t, uint32_t*) \
    X(int32_t, ovrAvatar2Primitive_GetMaterialExtensionName, int32_t, uint32_t, char*, uint32_t*) \
    X(int32_t, ovrAvatar2Primitive_GetNumEntriesInMaterialExtensionByIndex, int32_t, uint32_t, uint32_t*) \
    X(int32_t, ovrAvatar2Primitive_MaterialExtensionEntryMetaDataByIndex, int32_t, uint32_t, uint32_t, ExtensionEntry*) \
    X(int32_t, ovrAvatar2Primitive_MaterialExtensionEntryDataByIndex, int32_t, uint32_t, uint32_t, char*, uint32_t, void*, uint32_t) \
    X(int32_t, ovrAvatar2VertexBuffer_GetMorphTargetCount, int32_t, uint32_t*) \
    X(int32_t, ovrAvatar2MorphTarget_GetVertexPositions, int32_t, uint32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2MorphTarget_GetVertexNormals, int32_t, uint32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2MorphTarget_GetVertexTangents, int32_t, uint32_t, void*, uint32_t, uint32_t) \
    X(int32_t, ovrAvatar2Input_SetFacePoseProvider, int32_t, const FaceProvider*) \
    X(int32_t, ovrAvatar2Behavior_SetBehaviorSystemEnabled, int32_t, bool) \
    X(int32_t, ovrAvatar2Render_QueryRenderState, int32_t, EntityState*) \
    X(int32_t, ovrAvatar2Render_GetPrimitiveRenderStateByIndex, int32_t, uint32_t, PrimitiveState*) \
    X(int32_t, ovrAvatar2Render_GetSkinTransforms, int32_t, int32_t, void*, uint32_t, bool) \
    X(int32_t, ovrAvatar2Render_GetMorphTargetWeights, int32_t, int32_t, void*, uint32_t)
#define QFT_AVATAR_FIELD(R, name, ...) R (*name)(__VA_ARGS__) = nullptr;
    QFT_AVATAR_API(QFT_AVATAR_FIELD)
    void open(const char* path) {
        library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
        require(library, "Meta Avatars SDK is missing");
#define QFT_AVATAR_LOAD(R, name, ...) name = reinterpret_cast<decltype(name)>(dlsym(library, #name)); require(name, "Meta Avatars SDK: " #name);
        QFT_AVATAR_API(QFT_AVATAR_LOAD)
    }
};
inline void ok(int32_t result, const char* what) { if (result != Success) throw std::runtime_error(std::string(what) + " (" + std::to_string(result) + ")"); }

struct Mat { float m[16]; };
inline Mat multiply(const Mat& a, const Mat& b) { Mat r{}; for (int c = 0; c < 4; c++) for (int i = 0; i < 4; i++) for (int k = 0; k < 4; k++) r.m[c*4+i] += a.m[k*4+i] * b.m[c*4+k]; return r; }
inline Mat identity() { Mat r{}; r.m[0] = r.m[5] = r.m[10] = r.m[15] = 1; return r; }
inline Mat translation(Vec3 t) { Mat r = identity(); r.m[12] = t.x; r.m[13] = t.y; r.m[14] = t.z; return r; }
inline Mat rotation(Vec3 a, float degrees) {
    float n = std::sqrt(a.x*a.x + a.y*a.y + a.z*a.z); a = {a.x/n, a.y/n, a.z/n};
    float r = degrees * 3.14159265f / 180, c = std::cos(r), s = std::sin(r), t = 1 - c; Mat m = identity();
    m.m[0] = c + a.x*a.x*t; m.m[4] = a.x*a.y*t - a.z*s; m.m[8] = a.x*a.z*t + a.y*s;
    m.m[1] = a.y*a.x*t + a.z*s; m.m[5] = c + a.y*a.y*t; m.m[9] = a.y*a.z*t - a.x*s;
    m.m[2] = a.z*a.x*t - a.y*s; m.m[6] = a.z*a.y*t + a.x*s; m.m[10] = c + a.z*a.z*t; return m;
}
inline Vec3 apply(const Mat& m, Vec3 p) { return {m.m[0]*p.x + m.m[4]*p.y + m.m[8]*p.z + m.m[12], m.m[1]*p.x + m.m[5]*p.y + m.m[9]*p.z + m.m[13], m.m[2]*p.x + m.m[6]*p.y + m.m[10]*p.z + m.m[14]}; }
inline Mat about(Vec3 p, const Mat& m) { return multiply(translation(p), multiply(m, translation({-p.x, -p.y, -p.z}))); }
inline Mat inverseRigid(const Mat& a) {
    float s = a.m[0]*a.m[0] + a.m[1]*a.m[1] + a.m[2]*a.m[2]; Mat r = identity();
    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r.m[j*4+i] = a.m[i*4+j] / s;
    Vec3 t = apply(r, {a.m[12], a.m[13], a.m[14]}); r.m[12] = -t.x; r.m[13] = -t.y; r.m[14] = -t.z; return r;
}
inline Vec3 cross(Vec3 a, Vec3 b) { return {a.y*b.z - a.z*b.y, a.z*b.x - a.x*b.z, a.x*b.y - a.y*b.x}; }
inline Vec3 normalize(Vec3 v) { float n = std::sqrt(v.x*v.x + v.y*v.y + v.z*v.z); return {v.x/n, v.y/n, v.z/n}; }

struct Tongue { float out = 0, side = 0, up = 0; };
constexpr float tongueExtend = .05f, tongueRest = -4, tongueWidth = .8f, tongueLift = 15, tongueRise = .005f, baseSide = 7, tipSide = 22, baseUp = 0, tipUp = 22;

class Guide {
public:
    FacePose pose{};
    Tongue tongue;
    bool idle = true;

    ~Guide() {
        if (!api.library) return;
        if (entity) api.ovrAvatar2Entity_Destroy(entity);
        if (initialized) api.ovrAvatar2_Shutdown();
    }
    void start(const std::string& libraryPath, const std::string& assetsFolder, std::vector<uint8_t> preset) {
        api.open(libraryPath.c_str());
        Init init{}; uint32_t version[5] = {40, 0, 0, 24, 4}; std::memcpy(init.version, version, sizeof version);
        init.clientVersion = "QFT+ calibration"; init.platform = 4; init.flags = 2; init.loggingLevel = 6;
        init.resource = reinterpret_cast<void*>(&Guide::onResource); init.resourceContext = this;
        init.fallbackAssetsFolder = assetsFolder.c_str(); init.workers = 1; init.maxRequests = init.maxSend = init.maxReceive = -1;
        init.right = {1, 0, 0}; init.up = {0, 1, 0}; init.forward = {0, 0, -1}; init.clientName = "qftplus.headset"; init.networkHz = 10;
        ok(api.ovrAvatar2_Initialize(&init), "Start Meta Avatars SDK"); initialized = true;
        Create create{2 | 4 | 16 | 64 | (1 << 12) | (1 << 13), {1, 2, 2, -1, 0, 1, {}, 0}, 1};
        ok(api.ovrAvatar2Entity_Create(&create, &entity), "Create avatar");
        FaceProvider provider{this, &Guide::onFace};
        ok(api.ovrAvatar2Input_SetFacePoseProvider(entity, &provider), "Connect avatar face");
        Load load = api.ovrAvatar2Entity_DefaultLoadSettings(); load.filters = create.filters;
        presetData = std::move(preset);
        ok(api.ovrAvatar2Entity_LoadMemory(entity, presetData.data(), static_cast<uint32_t>(presetData.size()), "preset.glb", load, &request), "Load avatar");
    }
    bool ready() const { return state == 7; }
    bool failed() const { return state == 8 || state == 9; }

    void update(float dt) {
        animate(dt);
        if (!api.library) return;
        api.ovrAvatar2_Update(dt);
        if (!ready()) {
            Request info{}; api.ovrAvatar2Asset_GetLoadRequestInfo(request, &info); state = info.state;
            std::vector<int32_t> fresh; { std::lock_guard<std::mutex> lock(mutex); fresh.swap(pending); }
            for (int32_t id : fresh) { upload(id); api.ovrAvatar2Asset_ResourceReadyToRender(id); }
            if (ready()) api.ovrAvatar2Behavior_SetBehaviorSystemEnabled(entity, true);
            return;
        }
        EntityState es{}; if (api.ovrAvatar2Render_QueryRenderState(entity, &es) != Success) return;
        instances.clear();
        for (uint32_t k = 0; k < es.primitives; k++) {
            PrimitiveState ps{}; if (api.ovrAvatar2Render_GetPrimitiveRenderStateByIndex(entity, k, &ps) != Success) continue;
            auto found = meshes.find(ps.primitive); if (found == meshes.end()) continue;
            Mesh& m = found->second;
            weights.resize(ps.morphCount);
            api.ovrAvatar2Render_GetMorphTargetWeights(entity, ps.id, weights.data(), static_cast<uint32_t>(weights.size() * 4));
            if (pose.weights[CheekPuffL] > .9f && pose.weights[CheekPuffR] > .9f && !blinking) {
                m.puff.clear(); for (uint32_t t = 0; t < weights.size(); t++) if (weights[t] > .5f) m.puff.push_back(t);
            }
            float suck = std::min(pose.weights[CheekSuckL], pose.weights[CheekSuckR]);
            for (uint32_t t : m.puff) if (t < weights.size()) weights[t] -= .8f * suck;
            skin.resize(m.inverseBind.size());
            api.ovrAvatar2Render_GetSkinTransforms(entity, ps.id, skin.data(), static_cast<uint32_t>(skin.size() * sizeof(Mat)), false);
            if (m.arm.empty()) resolveJoints(m, ps.pose);
            poseArms(m);
            if (idle) poseHead(m);
            if (m.tongueBase >= 0) poseTongue(m);
            if (m.head >= 0) head = apply(multiply(skin[m.head], inverseRigid(m.inverseBind[m.head])), {0, 0, 0});
            morph(m);
            glBindBuffer(GL_UNIFORM_BUFFER, m.joints); glBufferSubData(GL_UNIFORM_BUFFER, 0, static_cast<GLsizeiptr>(skin.size() * sizeof(Mat)), skin.data());
            instances.push_back(&m);
        }
    }
    Vec3 head{0, 1.6f, 0};

    void draw(GLuint program) {
        for (Mesh* m : instances) {
            glBindVertexArray(m->vao);
            glBindBufferBase(GL_UNIFORM_BUFFER, 0, m->joints);
            for (int t = 0; t < 3; t++) { glActiveTexture(GL_TEXTURE0 + t); glBindTexture(GL_TEXTURE_2D, m->textures[t]); }
            glUniform1i(glGetUniformLocation(program, "u_BaseColor"), 0); glUniform1i(glGetUniformLocation(program, "u_Normal"), 1); glUniform1i(glGetUniformLocation(program, "u_ORM"), 2);
            glUniform4fv(glGetUniformLocation(program, "u_Hair"), 2, m->hair); glUniform4fv(glGetUniformLocation(program, "u_FacialHair"), 2, m->facialHair);
            glDrawElements(GL_TRIANGLES, m->indexCount, GL_UNSIGNED_SHORT, nullptr);
        }
        glBindVertexArray(0); glActiveTexture(GL_TEXTURE0);
    }
    void release() {
        for (auto& [id, m] : meshes) { glDeleteVertexArrays(1, &m.vao); GLuint b[] = {m.dynamic, m.fixed, m.jointIndices, m.indices, m.joints}; glDeleteBuffers(5, b); }
        glDeleteTextures(static_cast<GLsizei>(textures.size()), textures.data());
        meshes.clear(); images.clear(); textures.clear(); instances.clear();
    }

private:
    struct Delta { uint32_t vertex; float position[3], normal[3]; };
    struct Mesh {
        GLuint vao = 0, dynamic = 0, fixed = 0, jointIndices = 0, indices = 0, joints = 0, textures[3] = {};
        uint32_t indexCount = 0;
        std::vector<float> position, normal, morphed;
        std::vector<std::vector<Delta>> morphs;
        std::vector<Mat> inverseBind;
        std::vector<int32_t> poseJoint;
        std::vector<int8_t> arm;
        std::vector<uint8_t> headChain;
        std::vector<uint32_t> puff;
        int tongueBase = -1, tongueTip = -1, head = -1, shoulder[2] = {-1, -1};
        float hair[8] = {.5f, .4f, .2f, .2f, .2f, 1, .2f, .2f}, facialHair[8] = {.5f, .4f, .2f, .2f, .2f, 1, .2f, .2f};
    };
    Api api;
    bool initialized = false;
    int32_t entity = 0, request = 0, state = 0;
    std::vector<uint8_t> presetData;
    std::mutex mutex;
    std::vector<int32_t> pending;
    std::map<int32_t, Mesh> meshes;
    std::map<int32_t, std::pair<Image, std::vector<uint8_t>>> images;
    std::vector<GLuint> textures;
    std::vector<Mesh*> instances;
    std::vector<float> weights;
    std::vector<Mat> skin;
    float clock = 0, nextBlink = 1.5f, nextGlance = 2, turnAt = 0, glance[2] = {}, eye[2] = {}, eyeSpeed[2] = {},
        turn[2] = {}, turnSpeed[2] = {}, turnGoal[2] = {};
    bool blinking = false, secondBlink = false;
    uint32_t seed = 0x9e3779b9u;
    float random() { seed = seed * 1664525u + 1013904223u; return float(seed >> 8) / float(1u << 24); }
    static float ease(float x) { x = std::min(1.f, std::max(0.f, x)); return x * x * x * (x * (x * 6 - 15) + 10); }
    float breath() const {
        if (!idle) return 0;
        float p = std::fmod(clock, 4.5f) / 4.5f;
        return p < .4f ? 2 * ease(p / .4f) - 1 : 1 - 2 * ease((p - .4f) / .6f);
    }
    static void spring(float& x, float& speed, float target, float frequency, float damping, float dt) {
        float w = 6.2831853f * frequency;
        speed += (w * w * (target - x) - 2 * damping * w * speed) * dt; x += speed * dt;
    }

    void animate(float dt) {
        clock += dt;
        auto& w = pose.weights;
        if (clock >= nextBlink + .26f) {
            secondBlink = !secondBlink && random() < .15f;
            nextBlink = clock + (secondBlink ? .1f : 2.5f + 3.5f * random());
        }
        float b = clock - nextBlink, blink = b < 0 || b >= .26f ? 0 : b < .07f ? (b / .07f) * (b / .07f) : b < .11f ? 1 : 1 - ease((b - .11f) / .15f);
        blinking = blink > 0;
        w[EyesClosedL] = std::max(w[EyesClosedL], blink); w[EyesClosedR] = std::max(w[EyesClosedR], blink);
        if (!idle) return;
        if (clock >= nextGlance) {
            float x = (random() * 2 - 1) * 5, y = (random() * 2 - 1) * 2;
            if (std::abs(x - glance[0]) > 5 && clock > nextBlink + .26f && random() < .4f) nextBlink = clock;
            glance[0] = x; glance[1] = y; turnAt = clock + .12f; nextGlance = clock + 1.2f + 2.8f * random();
        }
        if (clock >= turnAt) { turnGoal[0] = glance[0] * .45f; turnGoal[1] = glance[1] * .35f; }
        for (float left = dt; left > 0; left -= 1 / 240.f)
            for (int i = 0; i < 2; i++) {
                float h = std::min(left, 1 / 240.f);
                spring(eye[i], eyeSpeed[i], glance[i] - turn[i], 12, .65f, h);
                spring(turn[i], turnSpeed[i], turnGoal[i], 1.4f, .8f, h);
            }
        float x = eye[0] / 30, y = eye[1] / 30;
        w[EyesLookRightL] = w[EyesLookRightR] = std::max(0.f, x); w[EyesLookLeftL] = w[EyesLookLeftR] = std::max(0.f, -x);
        w[EyesLookUpL] = w[EyesLookUpR] = std::max(0.f, y); w[EyesLookDownL] = w[EyesLookDownR] = std::max(0.f, -y);
    }

    static void onResource(const Resource* r, void* context) {
        auto* self = static_cast<Guide*>(context);
        if (r->status == 1) { std::lock_guard<std::mutex> lock(self->mutex); self->pending.push_back(r->id); }
    }
    static bool onFace(FacePose* out, void* context) { *out = static_cast<Guide*>(context)->pose; return true; }

    std::string nodeName(int32_t node) {
        char name[128] = {}; uint32_t size = 0;
        return api.ovrAvatar2Entity_GetNodeName(entity, node, name, sizeof name, &size) == Success ? name : "";
    }
    void upload(int32_t resource) {
        uint32_t count = 0; api.ovrAvatar2Asset_GetImageCount(resource, &count);
        for (uint32_t i = 0; i < count; i++) {
            Image image{}; ok(api.ovrAvatar2Asset_GetImageByIndex(resource, i, &image), "Read avatar texture");
            std::vector<uint8_t> data(image.size); ok(api.ovrAvatar2Asset_GetImageDataByIndex(resource, i, data.data(), image.size), "Read avatar texture data");
            images[image.id] = {image, std::move(data)};
        }
        api.ovrAvatar2Asset_GetPrimitiveCount(resource, &count);
        for (uint32_t i = 0; i < count; i++) {
            Primitive p{}; ok(api.ovrAvatar2Asset_GetPrimitiveByIndex(resource, i, &p), "Read avatar mesh");
            meshes[p.id] = mesh(p);
        }
    }
    GLuint texture(const Image& image, const std::vector<uint8_t>& data, bool srgb) {
        int block = image.format == 0xdc2b8f4cu ? 4 : image.format == 0xbd4fed74u ? 6 : image.format == 0xa75606e7u ? 8 : 0;
        GLuint t; glGenTextures(1, &t); glBindTexture(GL_TEXTURE_2D, t);
        if (block) {
            GLenum format = (block == 4 ? GL_COMPRESSED_RGBA_ASTC_4x4_KHR : block == 6 ? GL_COMPRESSED_RGBA_ASTC_6x6_KHR : GL_COMPRESSED_RGBA_ASTC_8x8_KHR) + (srgb ? 0x20 : 0);
            size_t offset = 0; uint32_t w = image.width, h = image.height;
            for (uint32_t level = 0; level < image.mips && offset < data.size(); level++) {
                size_t bytes = size_t((w + block - 1) / block) * ((h + block - 1) / block) * 16;
                if (offset + bytes > data.size()) break;
                glCompressedTexImage2D(GL_TEXTURE_2D, level, format, w, h, 0, static_cast<GLsizei>(bytes), data.data() + offset);
                offset += bytes; w = std::max(1u, w / 2); h = std::max(1u, h / 2);
            }
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAX_LEVEL, std::max(0, int(image.mips) - 1));
        } else if (image.format == 0xe3dd9a1eu) {
            if (data.size() < size_t(image.width) * image.height * 4) throw std::runtime_error("Truncated avatar texture");
            glTexImage2D(GL_TEXTURE_2D, 0, srgb ? GL_SRGB8_ALPHA8 : GL_RGBA8, image.width, image.height, 0, GL_RGBA, GL_UNSIGNED_BYTE, data.data()); glGenerateMipmap(GL_TEXTURE_2D);
        } else throw std::runtime_error("Unsupported avatar texture format");
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR_MIPMAP_LINEAR); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameterf(GL_TEXTURE_2D, 0x84FE , 8);
        textures.push_back(t);
        return t;
    }
    Mesh mesh(const Primitive& p) {
        Mesh m; uint32_t n = 0; ok(api.ovrAvatar2VertexBuffer_GetVertexCount(p.vertices, &n), "Avatar vertex count");
        auto read = [&](auto get, int floats, std::vector<float>& out) { out.assign(size_t(n) * floats, 0); get(p.vertices, out.data(), static_cast<uint32_t>(out.size() * 4), floats * 4); };
        std::vector<float> tangent, uv, color, orm, jointWeights, curvature; std::vector<uint16_t> jointIndices(size_t(n) * 4);
        read(api.ovrAvatar2VertexBuffer_GetPositions, 3, m.position); read(api.ovrAvatar2VertexBuffer_GetNormals, 3, m.normal);
        read(api.ovrAvatar2VertexBuffer_GetTangents, 4, tangent); read(api.ovrAvatar2VertexBuffer_GetTexCoord0, 2, uv);
        read(api.ovrAvatar2VertexBuffer_GetColors, 4, color); read(api.ovrAvatar2VertexBuffer_GetColorsORMT, 4, orm);
        read(api.ovrAvatar2VertexBuffer_GetJointWeights, 4, jointWeights); read(api.ovrAvatar2VertexBuffer_GetTexCoord2, 2, curvature);
        api.ovrAvatar2VertexBuffer_GetJointIndices(p.vertices, jointIndices.data(), static_cast<uint32_t>(jointIndices.size() * 2), 8);
        std::vector<float> material(n); api.ovrAvatar2VertexBuffer_GetMaterialTypesFloat(p.id, p.vertices, material.data(), n * 4, 4);
        std::vector<uint16_t> index(p.indexCount); ok(api.ovrAvatar2Primitive_GetIndexData(p.id, index.data(), p.indexCount * 2), "Avatar indices");
        uint32_t morphCount = 0; api.ovrAvatar2VertexBuffer_GetMorphTargetCount(p.morphs, &morphCount);
        std::vector<float> dp(size_t(n) * 3), dn(size_t(n) * 3);
        m.morphs.resize(morphCount);
        for (uint32_t t = 0; t < morphCount; t++) {
            api.ovrAvatar2MorphTarget_GetVertexPositions(p.morphs, t, dp.data(), n * 12, 12);
            std::fill(dn.begin(), dn.end(), 0.f); api.ovrAvatar2MorphTarget_GetVertexNormals(p.morphs, t, dn.data(), n * 12, 12);
            for (uint32_t v = 0; v < n; v++) {
                const float* a = &dp[v*3]; const float* b = &dn[v*3];
                if (a[0] || a[1] || a[2] || b[0] || b[1] || b[2]) m.morphs[t].push_back({v, {a[0], a[1], a[2]}, {b[0], b[1], b[2]}});
            }
        }
        std::vector<JointInfo> joints(p.joints); ok(api.ovrAvatar2Primitive_GetJointInfo(p.id, joints.data(), static_cast<uint32_t>(joints.size() * sizeof(JointInfo))), "Avatar skeleton");
        for (uint32_t j = 0; j < p.joints; j++) {
            Mat ib; std::memcpy(ib.m, joints[j].inverseBind, sizeof ib.m); m.inverseBind.push_back(ib); m.poseJoint.push_back(joints[j].joint);
        }
        std::vector<float> fixed; fixed.reserve(size_t(n) * 18);
        for (uint32_t v = 0; v < n; v++) {
            fixed.insert(fixed.end(), &tangent[v*4], &tangent[v*4] + 3); fixed.insert(fixed.end(), &jointWeights[v*4], &jointWeights[v*4] + 4);
            fixed.insert(fixed.end(), {std::pow(color[v*4], 1 / 2.2f), std::pow(color[v*4+1], 1 / 2.2f), std::pow(color[v*4+2], 1 / 2.2f), material[v] / 255});
            fixed.insert(fixed.end(), {uv[v*2], uv[v*2+1]}); fixed.insert(fixed.end(), &orm[v*4], &orm[v*4] + 4); fixed.push_back(curvature[v*2]);
        }
        glGenVertexArrays(1, &m.vao); glBindVertexArray(m.vao);
        glGenBuffers(1, &m.fixed); glBindBuffer(GL_ARRAY_BUFFER, m.fixed); glBufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(fixed.size() * 4), fixed.data(), GL_STATIC_DRAW);
        const int locations[] = {2, 4, 5, 6, 7, 8}, sizes[] = {3, 4, 4, 2, 4, 1}; int offset = 0;
        for (int a = 0; a < 6; a++) { glEnableVertexAttribArray(locations[a]); glVertexAttribPointer(locations[a], sizes[a], GL_FLOAT, GL_FALSE, 18 * 4, reinterpret_cast<void*>(intptr_t(offset * 4))); offset += sizes[a]; }
        glGenBuffers(1, &m.jointIndices); glBindBuffer(GL_ARRAY_BUFFER, m.jointIndices);
        glBufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(jointIndices.size() * 2), jointIndices.data(), GL_STATIC_DRAW);
        glEnableVertexAttribArray(3); glVertexAttribIPointer(3, 4, GL_UNSIGNED_SHORT, 8, nullptr);
        m.morphed.resize(size_t(n) * 6);
        glGenBuffers(1, &m.dynamic); glBindBuffer(GL_ARRAY_BUFFER, m.dynamic); glBufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(m.morphed.size() * 4), nullptr, GL_STREAM_DRAW);
        glEnableVertexAttribArray(0); glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 24, nullptr);
        glEnableVertexAttribArray(1); glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 24, reinterpret_cast<void*>(12));
        glGenBuffers(1, &m.indices); glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, m.indices); glBufferData(GL_ELEMENT_ARRAY_BUFFER, p.indexCount * 2, index.data(), GL_STATIC_DRAW);
        glBindVertexArray(0);
        glGenBuffers(1, &m.joints); glBindBuffer(GL_UNIFORM_BUFFER, m.joints); glBufferData(GL_UNIFORM_BUFFER, static_cast<GLsizeiptr>(256 * sizeof(Mat)), nullptr, GL_DYNAMIC_DRAW);
        m.indexCount = p.indexCount;
        for (uint32_t t = 0; t < p.textures; t++) {
            MaterialTexture mt{}; if (api.ovrAvatar2Primitive_GetMaterialTextureByIndex(p.id, t, &mt) != Success) continue;
            int slot = mt.type == 0 ? 0 : mt.type == 1 ? 1 : mt.type == 3 ? 2 : -1;
            auto image = images.find(mt.image);
            if (slot >= 0 && image != images.end()) m.textures[slot] = texture(image->second.first, image->second.second, slot == 0);
        }
        uint32_t extensions = 0; api.ovrAvatar2Primitive_GetNumMaterialExtensions(p.id, &extensions);
        static const char* keys[8] = {"anisotropicIntensity", "colorRoughness", "roughness", "specularColorIntensity", "specularColorOffset", "specularNormalIntensity", "specularShiftIntensity", "specularWhiteIntensity"};
        for (uint32_t e = 0; e < extensions; e++) {
            char name[64] = {}; uint32_t size = sizeof name - 1; api.ovrAvatar2Primitive_GetMaterialExtensionName(p.id, e, name, &size);
            float* target = !std::strcmp(name, "FB_materials_hair") ? m.hair : !std::strcmp(name, "FB_materials_facial_hair") ? m.facialHair : nullptr;
            uint32_t entries = 0; if (!target) continue; api.ovrAvatar2Primitive_GetNumEntriesInMaterialExtensionByIndex(p.id, e, &entries);
            for (uint32_t i = 0; i < entries; i++) {
                ExtensionEntry entry{}; if (api.ovrAvatar2Primitive_MaterialExtensionEntryMetaDataByIndex(p.id, e, i, &entry) != Success || entry.dataSize < 4 || entry.nameSize >= 64) continue;
                char key[64] = {}; std::vector<uint8_t> value(entry.dataSize);
                if (api.ovrAvatar2Primitive_MaterialExtensionEntryDataByIndex(p.id, e, i, key, entry.nameSize, value.data(), entry.dataSize) != Success) continue;
                for (int k = 0; k < 8; k++) if (!std::strcmp(key, keys[k])) std::memcpy(&target[k], value.data(), 4);
            }
        }
        return m;
    }
    void morph(Mesh& m) {
        size_t n = m.position.size() / 3;
        for (size_t v = 0; v < n; v++) for (int c = 0; c < 3; c++) { m.morphed[v*6+c] = m.position[v*3+c]; m.morphed[v*6+3+c] = m.normal[v*3+c]; }
        for (size_t t = 0; t < std::min(weights.size(), m.morphs.size()); t++) {
            float w = weights[t]; if (std::abs(w) < 1e-4f) continue;
            for (const Delta& d : m.morphs[t]) for (int c = 0; c < 3; c++) { m.morphed[d.vertex*6+c] += w * d.position[c]; m.morphed[d.vertex*6+3+c] += w * d.normal[c]; }
        }
        glBindBuffer(GL_ARRAY_BUFFER, m.dynamic); glBufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(m.morphed.size() * 4), m.morphed.data(), GL_STREAM_DRAW);
    }
    void resolveJoints(Mesh& m, const Pose& pose) {
        if (!pose.nodes || !pose.parents) return;
        m.arm.assign(m.poseJoint.size(), 0); m.headChain.assign(m.poseJoint.size(), 0);
        std::vector<std::string> names(pose.count);
        for (uint32_t j = 0; j < pose.count; j++) names[j] = nodeName(pose.nodes[j]);
        for (size_t j = 0; j < m.poseJoint.size(); j++) {
            int32_t at = m.poseJoint[j]; if (at < 0 || uint32_t(at) >= pose.count) continue;
            const std::string& name = names[at];
            for (int32_t p = at; p >= 0 && uint32_t(p) < pose.count; p = pose.parents[p]) if (names[p] == "head_joint") { m.headChain[j] = 1; break; }
            if (name == "tongueBase_joint") m.tongueBase = int(j); else if (name == "tongueTip_joint") m.tongueTip = int(j); else if (name == "head_joint") m.head = int(j);
            else if (name == "shoulder_left_joint") m.shoulder[0] = int(j); else if (name == "shoulder_right_joint") m.shoulder[1] = int(j);
            for (int32_t p = at; p >= 0 && uint32_t(p) < pose.count; p = pose.parents[p])
                if (names[p] == "shoulder_left_joint" || names[p] == "shoulder_right_joint") { m.arm[j] = names[p] == "shoulder_left_joint" ? 1 : -1; break; }
        }
    }
    void poseArms(const Mesh& m) {
        for (int s = 0; s < 2; s++) {
            if (m.shoulder[s] < 0) continue;
            float side = s == 0 ? 1.f : -1.f;
            Vec3 pivot = apply(multiply(skin[m.shoulder[s]], inverseRigid(m.inverseBind[m.shoulder[s]])), {0, 0, 0});
            Mat down = multiply(translation({0, .002f * breath(), 0}), about(pivot, multiply(rotation({0, 0, 1}, -side * 72), rotation({1, 0, 0}, -12))));
            for (size_t j = 0; j < m.arm.size(); j++) if (m.arm[j] == side) skin[j] = multiply(down, skin[j]);
        }
    }
    void poseHead(const Mesh& m) {
        if (m.head < 0 || m.headChain.empty()) return;
        float t = clock, yaw = -turn[0] + .35f * std::sin(t * .7f), pitch = -turn[1] - .4f * breath() + .25f * std::sin(t * .9f + 1),
            roll = .5f * std::sin(t * .5f + 4) - .08f * turn[0];
        Vec3 pivot = apply(multiply(skin[m.head], inverseRigid(m.inverseBind[m.head])), {0, 0, 0});
        Mat r = about(pivot, multiply(rotation({0, 1, 0}, yaw), multiply(rotation({1, 0, 0}, pitch), rotation({0, 0, 1}, roll))));
        for (size_t j = 0; j < m.headChain.size(); j++) if (m.headChain[j]) skin[j] = multiply(r, skin[j]);
    }
    void poseTongue(const Mesh& m) {
        if (tongue.out <= 0 || m.tongueTip < 0) return;
        Vec3 base = apply(multiply(skin[m.tongueBase], inverseRigid(m.inverseBind[m.tongueBase])), {0, 0, 0});
        Vec3 tip = apply(multiply(skin[m.tongueTip], inverseRigid(m.inverseBind[m.tongueTip])), {0, 0, 0});
        Vec3 forward = normalize({tip.x - base.x, tip.y - base.y, tip.z - base.z}), lateral = normalize(cross({0, 1, 0}, forward)), vertical = cross(forward, lateral);
        float out = tongue.out * tongueExtend, arc = std::sin(3.14159265f * std::min(1.f, tongue.out)), rise = tongueRise * arc;
        Mat b = multiply(translation({forward.x * out + vertical.x * rise, forward.y * out + vertical.y * rise, forward.z * out + vertical.z * rise}),
            about(base, multiply(rotation(vertical, tongue.side * baseSide), rotation(lateral, -(tongue.up * baseUp + tongueRest * tongue.out + tongueLift * arc)))));
        Mat narrow = identity(); const float l[3] = {lateral.x, lateral.y, lateral.z};
        for (int c = 0; c < 3; c++) for (int i = 0; i < 3; i++) narrow.m[c * 4 + i] += (tongueWidth - 1) * std::min(1.f, 2 * tongue.out) * l[i] * l[c];
        b = multiply(b, about(base, narrow));
        Mat t = multiply(about(apply(b, tip), multiply(rotation(vertical, tongue.side * tipSide), rotation(lateral, -tongue.up * tipUp))), b);
        skin[m.tongueBase] = multiply(b, skin[m.tongueBase]); skin[m.tongueTip] = multiply(t, skin[m.tongueTip]);
    }
};
}
