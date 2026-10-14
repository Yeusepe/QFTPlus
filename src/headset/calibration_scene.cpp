#define XR_USE_PLATFORM_ANDROID
#define XR_USE_GRAPHICS_API_OPENGL_ES
#include <jni.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <android/log.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <map>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
std::atomic<bool> stopping{false};
void require(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); }
void xr(XrResult result, const char* what) {
    if (XR_FAILED(result)) throw std::runtime_error(std::string(what) + " (" + std::to_string(result) + ")");
}
template<class T> T function(XrInstance instance, const char* name) {
    T result; xr(xrGetInstanceProcAddr(instance, name, reinterpret_cast<PFN_xrVoidFunction*>(&result)), name); return result;
}
}
#include "calibration_avatar.hpp"

namespace {
using avatar::Mat; using avatar::Vec3;

#include "calibration_render.hpp"

const char* spaceVertex = R"glsl(#version 300 es
layout(location = 0) in vec3 p; layout(location = 1) in vec2 uv; uniform mat4 u_ModelViewProj; out vec2 v_UV;
void main() { v_UV = uv; gl_Position = u_ModelViewProj * vec4(p, 1.0); })glsl";
const char* spaceFragment = R"glsl(#version 300 es
precision mediump float; in vec2 v_UV; uniform sampler2D u_Texture; out vec4 o_Color;
void main() { o_Color = texture(u_Texture, v_UV); })glsl";
const char* rayVertex = R"glsl(#version 300 es
uniform mat4 u_ViewProj; uniform vec3 u_From, u_To; out float v_T;
void main() { v_T = float(gl_VertexID); gl_Position = u_ViewProj * vec4(mix(u_From, u_To, v_T), 1.0); })glsl";
const char* rayFragment = R"glsl(#version 300 es
precision mediump float; in float v_T; out vec4 o_Color;
void main() { o_Color = vec4(1.0, 1.0, 1.0, 0.85 * (1.0 - 0.7 * v_T)); })glsl";

Mat fromPose(const XrPosef& p) {
    const auto& q = p.orientation; Mat m = avatar::identity();
    m.m[0] = 1 - 2*(q.y*q.y + q.z*q.z); m.m[1] = 2*(q.x*q.y + q.z*q.w); m.m[2] = 2*(q.x*q.z - q.y*q.w);
    m.m[4] = 2*(q.x*q.y - q.z*q.w); m.m[5] = 1 - 2*(q.x*q.x + q.z*q.z); m.m[6] = 2*(q.y*q.z + q.x*q.w);
    m.m[8] = 2*(q.x*q.z + q.y*q.w); m.m[9] = 2*(q.y*q.z - q.x*q.w); m.m[10] = 1 - 2*(q.x*q.x + q.y*q.y);
    m.m[12] = p.position.x; m.m[13] = p.position.y; m.m[14] = p.position.z; return m;
}
XrPosef toPose(const Mat& m) {
    float r00 = m.m[0], r11 = m.m[5], r22 = m.m[10], trace = r00 + r11 + r22, s;
    XrQuaternionf q;
    if (trace > 0) { s = std::sqrt(trace + 1) * 2; q = {(m.m[6] - m.m[9]) / s, (m.m[8] - m.m[2]) / s, (m.m[1] - m.m[4]) / s, s / 4}; }
    else if (r00 > r11 && r00 > r22) { s = std::sqrt(1 + r00 - r11 - r22) * 2; q = {s / 4, (m.m[4] + m.m[1]) / s, (m.m[8] + m.m[2]) / s, (m.m[6] - m.m[9]) / s}; }
    else if (r11 > r22) { s = std::sqrt(1 + r11 - r00 - r22) * 2; q = {(m.m[4] + m.m[1]) / s, s / 4, (m.m[9] + m.m[6]) / s, (m.m[8] - m.m[2]) / s}; }
    else { s = std::sqrt(1 + r22 - r00 - r11) * 2; q = {(m.m[8] + m.m[2]) / s, (m.m[9] + m.m[6]) / s, s / 4, (m.m[1] - m.m[4]) / s}; }
    return {q, {m.m[12], m.m[13], m.m[14]}};
}
Mat viewProjection(const XrView& eye) {
    float l = std::tan(eye.fov.angleLeft), r = std::tan(eye.fov.angleRight), d = std::tan(eye.fov.angleDown), u = std::tan(eye.fov.angleUp), n = .05f, f = 1000;
    Mat p{}; p.m[0] = 2 / (r - l); p.m[5] = 2 / (u - d); p.m[8] = (r + l) / (r - l); p.m[9] = (u + d) / (u - d);
    p.m[10] = -(f + n) / (f - n); p.m[11] = -1; p.m[14] = -2 * f * n / (f - n);
    Mat pose = fromPose(eye.pose), view = avatar::identity();
    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) view.m[j*4+i] = pose.m[i*4+j];
    Vec3 t = avatar::apply(view, {pose.m[12], pose.m[13], pose.m[14]}); view.m[12] = -t.x; view.m[13] = -t.y; view.m[14] = -t.z;
    return avatar::multiply(p, view);
}

struct EyeTarget {
    XrSwapchain swapchain = XR_NULL_HANDLE; std::vector<XrSwapchainImageOpenGLESKHR> images; GLuint depth = 0; int width = 0, height = 0;
};
struct SpaceDraw { GLuint vao = 0, buffers[2] = {}, texture = 0; GLsizei count = 0; bool blend = false; Mat world; };

PFNGLFRAMEBUFFERTEXTURE2DMULTISAMPLEEXTPROC framebufferTextureMultisample;
PFNGLRENDERBUFFERSTORAGEMULTISAMPLEEXTPROC renderbufferStorageMultisample;

struct Scene {
    XrInstance instance = XR_NULL_HANDLE; XrSession session = XR_NULL_HANDLE;
    XrSpace viewSpace = XR_NULL_HANDLE, worldSpace = XR_NULL_HANDLE, stageSpace = XR_NULL_HANDLE, aim[2] = {XR_NULL_HANDLE, XR_NULL_HANDLE};
    XrFaceTracker2FB face = XR_NULL_HANDLE; XrEyeTrackerFB eyes = XR_NULL_HANDLE;
    XrActionSet actions = XR_NULL_HANDLE; XrAction cancel = XR_NULL_HANDLE, start = XR_NULL_HANDLE, select = XR_NULL_HANDLE, pose = XR_NULL_HANDLE;
    XrPath hands[2] = {};
    bool handInteraction = false;
    PFN_xrDestroyFaceTracker2FB destroyFace = nullptr; PFN_xrDestroyEyeTrackerFB destroyEyes = nullptr;
    EGLDisplay display = EGL_NO_DISPLAY; EGLSurface surface = EGL_NO_SURFACE; EGLContext context = EGL_NO_CONTEXT;
    EyeTarget eye[2]; XrSwapchain panel = XR_NULL_HANDLE; jobject panelSurface = nullptr; JNIEnv* env = nullptr;
    GLuint framebuffer = 0, avatarProgram = 0, spaceProgram = 0, rayProgram = 0, empty = 0;
    std::vector<SpaceDraw> space;
    avatar::Guide guide;
    ~Scene() {
        if (eyes && destroyEyes) destroyEyes(eyes); if (face && destroyFace) destroyFace(face);
        if (context != EGL_NO_CONTEXT) {
            guide.release();
            for (auto& d : space) { glDeleteVertexArrays(1, &d.vao); glDeleteBuffers(2, d.buffers); glDeleteTextures(1, &d.texture); }
            for (auto& e : eye) glDeleteRenderbuffers(1, &e.depth);
            glDeleteFramebuffers(1, &framebuffer); glDeleteVertexArrays(1, &empty);
            glDeleteProgram(avatarProgram); glDeleteProgram(spaceProgram); glDeleteProgram(rayProgram);
        }
        if (panelSurface && env) env->DeleteGlobalRef(panelSurface);
        if (panel) xrDestroySwapchain(panel); for (auto& e : eye) if (e.swapchain) xrDestroySwapchain(e.swapchain);
        for (auto s : {viewSpace, worldSpace, stageSpace, aim[0], aim[1]}) if (s) xrDestroySpace(s);
        if (session) xrDestroySession(session); if (actions) xrDestroyActionSet(actions); if (instance) xrDestroyInstance(instance);
        if (display != EGL_NO_DISPLAY) {
            eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
            if (surface != EGL_NO_SURFACE) eglDestroySurface(display, surface);
            if (context != EGL_NO_CONTEXT) eglDestroyContext(display, context); eglTerminate(display);
        }
    }
    XrPath path(const char* p) { XrPath result; xr(xrStringToPath(instance, p, &result), p); return result; }
    XrAction action(const char* name, const char* label, XrActionType type, bool perHand) {
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO}; ai.actionType = type; std::strcpy(ai.actionName, name); std::strcpy(ai.localizedActionName, label);
        if (perHand) { ai.countSubactionPaths = 2; ai.subactionPaths = hands; }
        XrAction a; xr(xrCreateAction(actions, &ai, &a), label); return a;
    }
    void setup(JNIEnv* jni, jobject activity, bool pupils) {
        env = jni; JavaVM* vm; env->GetJavaVM(&vm);
        jclass activityClass = env->GetObjectClass(activity);
        jmethodID inputLabel = env->GetMethodID(activityClass, "inputLabel", "(I)Ljava/lang/String;");
        env->DeleteLocalRef(activityClass);
        require(inputLabel, "Calibration input labels missing");
        auto label = [&](int index) {
            auto text = static_cast<jstring>(env->CallObjectMethod(activity, inputLabel, index));
            require(text && !env->ExceptionCheck(), "Read calibration input label");
            const char* chars = env->GetStringUTFChars(text, nullptr);
            std::string value(chars); env->ReleaseStringUTFChars(text, chars); env->DeleteLocalRef(text);
            if (value.size() >= XR_MAX_LOCALIZED_ACTION_NAME_SIZE) {
                size_t length = XR_MAX_LOCALIZED_ACTION_NAME_SIZE - 1;
                while ((static_cast<unsigned char>(value[length]) & 0xc0) == 0x80) --length;
                value.resize(length);
            }
            return value;
        };
        auto initialize = function<PFN_xrInitializeLoaderKHR>(XR_NULL_HANDLE, "xrInitializeLoaderKHR");
        XrLoaderInitInfoAndroidKHR loader{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR}; loader.applicationVM = vm; loader.applicationContext = activity;
        xr(initialize(reinterpret_cast<XrLoaderInitInfoBaseHeaderKHR*>(&loader)), "Initialize OpenXR");
        std::vector<const char*> extensions = {XR_KHR_ANDROID_CREATE_INSTANCE_EXTENSION_NAME, XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME, XR_FB_FACE_TRACKING2_EXTENSION_NAME,
            XR_FB_EYE_TRACKING_SOCIAL_EXTENSION_NAME, XR_KHR_ANDROID_SURFACE_SWAPCHAIN_EXTENSION_NAME, XR_FB_COMPOSITION_LAYER_IMAGE_LAYOUT_EXTENSION_NAME};
        uint32_t available = 0; xrEnumerateInstanceExtensionProperties(nullptr, 0, &available, nullptr);
        std::vector<XrExtensionProperties> properties(available, {XR_TYPE_EXTENSION_PROPERTIES});
        xrEnumerateInstanceExtensionProperties(nullptr, available, &available, properties.data());
        for (const auto& p : properties) if (!std::strcmp(p.extensionName, XR_EXT_HAND_INTERACTION_EXTENSION_NAME)) handInteraction = true;
        if (handInteraction) extensions.push_back(XR_EXT_HAND_INTERACTION_EXTENSION_NAME);
        XrInstanceCreateInfoAndroidKHR android{XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR}; android.applicationVM = vm; android.applicationActivity = activity;
        XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO}; ci.next = &android; ci.applicationInfo.apiVersion = XR_API_VERSION_1_0;
        std::strcpy(ci.applicationInfo.applicationName, "QFT+ Calibration"); ci.enabledExtensionCount = uint32_t(extensions.size()); ci.enabledExtensionNames = extensions.data();
        xr(xrCreateInstance(&ci, &instance), "Create OpenXR instance");
        XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO}; si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY; XrSystemId system;
        xr(xrGetSystem(instance, &si, &system), "Find headset");
        XrGraphicsRequirementsOpenGLESKHR gr{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
        xr(function<PFN_xrGetOpenGLESGraphicsRequirementsKHR>(instance, "xrGetOpenGLESGraphicsRequirementsKHR")(instance, system, &gr), "Check graphics support");
        display = eglGetDisplay(EGL_DEFAULT_DISPLAY); require(eglInitialize(display, nullptr, nullptr), "Initialize calibration graphics");
        EGLint attributes[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_NONE}; EGLConfig config; EGLint count = 0;
        require(eglChooseConfig(display, attributes, &config, 1, &count) && count, "Choose calibration graphics format");
        EGLint ca[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE}; context = eglCreateContext(display, config, EGL_NO_CONTEXT, ca);
        EGLint pa[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE}; surface = eglCreatePbufferSurface(display, config, pa);
        require(eglMakeCurrent(display, surface, surface, context), "Open calibration graphics context");
        framebufferTextureMultisample = reinterpret_cast<PFNGLFRAMEBUFFERTEXTURE2DMULTISAMPLEEXTPROC>(eglGetProcAddress("glFramebufferTexture2DMultisampleEXT"));
        renderbufferStorageMultisample = reinterpret_cast<PFNGLRENDERBUFFERSTORAGEMULTISAMPLEEXTPROC>(eglGetProcAddress("glRenderbufferStorageMultisampleEXT"));
        require(framebufferTextureMultisample && renderbufferStorageMultisample, "Multisampling is unavailable");
        XrGraphicsBindingOpenGLESAndroidKHR binding{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR}; binding.display = display; binding.config = config; binding.context = context;
        XrSessionCreateInfo sc{XR_TYPE_SESSION_CREATE_INFO}; sc.next = &binding; sc.systemId = system;
        xr(xrCreateSession(instance, &sc, &session), "Create calibration session");
        XrReferenceSpaceCreateInfo space{XR_TYPE_REFERENCE_SPACE_CREATE_INFO}; space.poseInReferenceSpace.orientation.w = 1;
        space.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW; xr(xrCreateReferenceSpace(session, &space, &viewSpace), "Create calibration view");
        space.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL; xr(xrCreateReferenceSpace(session, &space, &worldSpace), "Create calibration room");
        space.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE; if (XR_FAILED(xrCreateReferenceSpace(session, &space, &stageSpace))) stageSpace = XR_NULL_HANDLE;
        destroyFace = function<PFN_xrDestroyFaceTracker2FB>(instance, "xrDestroyFaceTracker2FB"); destroyEyes = function<PFN_xrDestroyEyeTrackerFB>(instance, "xrDestroyEyeTrackerFB");
        XrFaceTrackingDataSource2FB source = XR_FACE_TRACKING_DATA_SOURCE2_VISUAL_FB;
        XrFaceTrackerCreateInfo2FB fc{XR_TYPE_FACE_TRACKER_CREATE_INFO2_FB}; fc.faceExpressionSet = XR_FACE_EXPRESSION_SET2_DEFAULT_FB; fc.requestedDataSourceCount = 1; fc.requestedDataSources = &source;
        xr(function<PFN_xrCreateFaceTracker2FB>(instance, "xrCreateFaceTracker2FB")(session, &fc, &face), "Start face tracker");
        XrEyeTrackerCreateInfoFB ec{XR_TYPE_EYE_TRACKER_CREATE_INFO_FB};
        xr(function<PFN_xrCreateEyeTrackerFB>(instance, "xrCreateEyeTrackerFB")(session, &ec, &eyes), "Start eye tracker");

        XrActionSetCreateInfo ac{XR_TYPE_ACTION_SET_CREATE_INFO}; std::strcpy(ac.actionSetName, "calibration"); std::strcpy(ac.localizedActionSetName, label(0).c_str());
        xr(xrCreateActionSet(instance, &ac, &actions), "Create calibration input");
        hands[0] = path("/user/hand/left"); hands[1] = path("/user/hand/right");
        cancel = action("cancel", label(1).c_str(), XR_ACTION_TYPE_BOOLEAN_INPUT, false);
        start = action("start", label(2).c_str(), XR_ACTION_TYPE_BOOLEAN_INPUT, false);
        select = action("select", label(3).c_str(), XR_ACTION_TYPE_BOOLEAN_INPUT, true);
        pose = action("aim", label(4).c_str(), XR_ACTION_TYPE_POSE_INPUT, true);
        XrActionSuggestedBinding bindings[] = {{cancel, path("/user/hand/right/input/b/click")}, {cancel, path("/user/hand/left/input/y/click")},
            {start, path("/user/hand/right/input/a/click")}, {start, path("/user/hand/left/input/x/click")},
            {select, path("/user/hand/left/input/trigger/value")}, {select, path("/user/hand/right/input/trigger/value")},
            {pose, path("/user/hand/left/input/aim/pose")}, {pose, path("/user/hand/right/input/aim/pose")}};
        XrInteractionProfileSuggestedBinding suggested{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING}; suggested.interactionProfile = path("/interaction_profiles/oculus/touch_controller");
        suggested.countSuggestedBindings = 8; suggested.suggestedBindings = bindings;
        xr(xrSuggestInteractionProfileBindings(instance, &suggested), "Bind calibration controls");
        XrActionSuggestedBinding handBindings[] = {{select, path("/user/hand/left/input/aim_activate_ext/value")}, {select, path("/user/hand/right/input/aim_activate_ext/value")},
            {pose, path("/user/hand/left/input/aim/pose")}, {pose, path("/user/hand/right/input/aim/pose")}};
        if (handInteraction) {
            suggested.interactionProfile = path("/interaction_profiles/ext/hand_interaction_ext"); suggested.countSuggestedBindings = 4; suggested.suggestedBindings = handBindings;
            xr(xrSuggestInteractionProfileBindings(instance, &suggested), "Bind hand controls");
        }
        XrActionSuggestedBinding simpleBindings[] = {{select, path("/user/hand/left/input/select/click")}, {select, path("/user/hand/right/input/select/click")},
            {pose, path("/user/hand/left/input/aim/pose")}, {pose, path("/user/hand/right/input/aim/pose")}};
        suggested.interactionProfile = path("/interaction_profiles/khr/simple_controller"); suggested.suggestedBindings = simpleBindings; suggested.countSuggestedBindings = 4;
        xrSuggestInteractionProfileBindings(instance, &suggested);
        XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO}; attach.countActionSets = 1; attach.actionSets = &actions;
        xr(xrAttachSessionActionSets(session, &attach), "Attach calibration controls");
        for (int h = 0; h < 2; h++) {
            XrActionSpaceCreateInfo as{XR_TYPE_ACTION_SPACE_CREATE_INFO}; as.action = pose; as.subactionPath = hands[h]; as.poseInActionSpace.orientation.w = 1;
            xr(xrCreateActionSpace(session, &as, &aim[h]), "Create pointer");
        }

        XrViewConfigurationView views[2] = {{XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}}; uint32_t viewCount = 0;
        xr(xrEnumerateViewConfigurationViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &viewCount, views), "Read display size");
        for (int i = 0; i < 2; i++) {
            EyeTarget& e = eye[i]; e.width = static_cast<int>(views[i].recommendedImageRectWidth); e.height = static_cast<int>(views[i].recommendedImageRectHeight);
            XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO}; info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT; info.format = GL_SRGB8_ALPHA8;
            info.sampleCount = 1; info.width = e.width; info.height = e.height; info.faceCount = 1; info.arraySize = 1; info.mipCount = 1;
            xr(xrCreateSwapchain(session, &info, &e.swapchain), "Create eye buffer");
            uint32_t n = 0; xr(xrEnumerateSwapchainImages(e.swapchain, 0, &n, nullptr), "Count eye images");
            e.images.resize(n, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
            xr(xrEnumerateSwapchainImages(e.swapchain, n, &n, reinterpret_cast<XrSwapchainImageBaseHeader*>(e.images.data())), "Get eye images");
            glGenRenderbuffers(1, &e.depth); glBindRenderbuffer(GL_RENDERBUFFER, e.depth); renderbufferStorageMultisample(GL_RENDERBUFFER, 4, GL_DEPTH_COMPONENT24, e.width, e.height);
        }
        glGenFramebuffers(1, &framebuffer); glGenVertexArrays(1, &empty);
        rayProgram = program(rayVertex, rayFragment);
        if (pupils) return;
        avatarProgram = program(avatarVertex, avatarFragment); spaceProgram = program(spaceVertex, spaceFragment);
        glUniformBlockBinding(avatarProgram, glGetUniformBlockIndex(avatarProgram, "SkinBlock"), 0);
    }
    jobject createPanel(int width, int height) {
        XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO}; info.width = width; info.height = height;
        jobject local = nullptr;
        xr(function<PFN_xrCreateSwapchainAndroidSurfaceKHR>(instance, "xrCreateSwapchainAndroidSurfaceKHR")(session, &info, &panel, &local), "Create calibration panel");
        panelSurface = env->NewGlobalRef(local);
        return panelSurface;
    }
    void loadSpace(jobjectArray draws) {
        jclass type = env->FindClass("com/qftplus/headset/SetupSpace$Draw");
        jfieldID fWorld = env->GetFieldID(type, "world", "[F"), fPositions = env->GetFieldID(type, "positions", "[F"), fUvs = env->GetFieldID(type, "uvs", "[F"),
            fIndices = env->GetFieldID(type, "indices", "[S"), fTexels = env->GetFieldID(type, "texels", "[B"), fFormat = env->GetFieldID(type, "glFormat", "I"),
            fWidth = env->GetFieldID(type, "width", "I"), fHeight = env->GetFieldID(type, "height", "I"), fMips = env->GetFieldID(type, "mips", "I"), fBlend = env->GetFieldID(type, "blend", "Z");
        env->DeleteLocalRef(type);
        for (jsize i = 0, n = env->GetArrayLength(draws); i < n; i++) {
            jobject d = env->GetObjectArrayElement(draws, i); SpaceDraw s;
            auto floats = [&](jfieldID f) { auto a = static_cast<jfloatArray>(env->GetObjectField(d, f)); std::vector<float> v(env->GetArrayLength(a)); env->GetFloatArrayRegion(a, 0, jsize(v.size()), v.data()); env->DeleteLocalRef(a); return v; };
            std::vector<float> world = floats(fWorld), positions = floats(fPositions), uvs = floats(fUvs); std::memcpy(s.world.m, world.data(), 64);
            auto ia = static_cast<jshortArray>(env->GetObjectField(d, fIndices)); std::vector<uint16_t> indices(env->GetArrayLength(ia));
            env->GetShortArrayRegion(ia, 0, jsize(indices.size()), reinterpret_cast<jshort*>(indices.data())); env->DeleteLocalRef(ia);
            auto ta = static_cast<jbyteArray>(env->GetObjectField(d, fTexels)); std::vector<uint8_t> texels(env->GetArrayLength(ta));
            env->GetByteArrayRegion(ta, 0, jsize(texels.size()), reinterpret_cast<jbyte*>(texels.data())); env->DeleteLocalRef(ta);
            GLenum format = env->GetIntField(d, fFormat); int w = env->GetIntField(d, fWidth), h = env->GetIntField(d, fHeight), mips = env->GetIntField(d, fMips);
            s.blend = env->GetBooleanField(d, fBlend); env->DeleteLocalRef(d);
            int block = format == GL_COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR ? 4 : format == GL_COMPRESSED_SRGB8_ALPHA8_ASTC_6x6_KHR ? 6 : format == GL_COMPRESSED_SRGB8_ALPHA8_ASTC_8x8_KHR ? 8
                : format == GL_COMPRESSED_SRGB8_ALPHA8_ASTC_10x10_KHR ? 10 : 12;
            glGenTextures(1, &s.texture); glBindTexture(GL_TEXTURE_2D, s.texture);
            size_t offset = 0;
            for (int level = 0; level < mips; level++) {
                int lw = std::max(1, w >> level), lh = std::max(1, h >> level); size_t bytes = size_t((lw + block - 1) / block) * ((lh + block - 1) / block) * 16;
                require(offset + bytes <= texels.size(), "Setup space texture is truncated");
                glCompressedTexImage2D(GL_TEXTURE_2D, level, format, lw, lh, 0, GLsizei(bytes), texels.data() + offset); offset += bytes;
            }
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR_MIPMAP_LINEAR); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameterf(GL_TEXTURE_2D, 0x84FE , 8);
            glGenVertexArrays(1, &s.vao); glBindVertexArray(s.vao); glGenBuffers(2, s.buffers);
            std::vector<float> vertices; for (size_t v = 0; v < positions.size() / 3; v++) vertices.insert(vertices.end(), {positions[v*3], positions[v*3+1], positions[v*3+2], uvs[v*2], uvs[v*2+1]});
            glBindBuffer(GL_ARRAY_BUFFER, s.buffers[0]); glBufferData(GL_ARRAY_BUFFER, GLsizeiptr(vertices.size() * 4), vertices.data(), GL_STATIC_DRAW);
            glEnableVertexAttribArray(0); glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 20, nullptr);
            glEnableVertexAttribArray(1); glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 20, reinterpret_cast<void*>(12));
            glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, s.buffers[1]); glBufferData(GL_ELEMENT_ARRAY_BUFFER, GLsizeiptr(indices.size() * 2), indices.data(), GL_STATIC_DRAW);
            glBindVertexArray(0); s.count = GLsizei(indices.size()); space.push_back(s);
        }
    }
    void drawSpace(const Mat& viewProj, const Mat& place) {
        glUseProgram(spaceProgram); glDisable(GL_CULL_FACE); glActiveTexture(GL_TEXTURE0); glUniform1i(glGetUniformLocation(spaceProgram, "u_Texture"), 0);
        GLint mvp = glGetUniformLocation(spaceProgram, "u_ModelViewProj");
        for (const auto& d : space) {
            if (d.blend) { glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA); glDepthMask(GL_FALSE); }
            Mat m = avatar::multiply(viewProj, avatar::multiply(place, d.world)); glUniformMatrix4fv(mvp, 1, GL_FALSE, m.m);
            glBindTexture(GL_TEXTURE_2D, d.texture); glBindVertexArray(d.vao); glDrawElements(GL_TRIANGLES, d.count, GL_UNSIGNED_SHORT, nullptr);
        }
        glDisable(GL_BLEND); glDepthMask(GL_TRUE); glBindVertexArray(0);
    }
};

float clamp01(float x) { return std::min(1.f, std::max(0.f, x)); }
float smooth(float x) { x = clamp01(x); return x * x * x * (x * (x * 6 - 15) + 10); }
float curve(float t, float at, float rise, float out, float fall) {
    if (t < 0) return 1;
    float x = clamp01((t - at) / rise) - 1, back = 1.1f;
    return (1 + (back + 1) * x * x * x + back * x * x) * (1 - smooth((t - out) / fall));
}
float beat(float t, float at, float length) { return t < 0 ? 0 : std::sin(3.14159265f * smooth((t - at) / length)); }

void expression(int step, float t, avatar::Guide& g) {
    std::fill(std::begin(g.pose.weights), std::end(g.pose.weights), 0.f);
    std::fill(std::begin(g.pose.confidence), std::end(g.pose.confidence), 1.f);
    g.tongue = {};
    auto& w = g.pose.weights;
    auto both = [&](int left, float v) { w[left] = w[left + 1] = v; };
    using namespace avatar;
    switch (step) {
        case 1:
            w[JawDrop] = .5f * curve(t, .35f, 1, 4.85f, 1.1f);
            both(InnerBrowRaiserL, .25f * curve(t, .55f, .9f, 4.8f, 1)); both(UpperLidRaiserL, .2f * curve(t, .5f, .8f, 4.8f, 1));
            break;
        case 2:
            both(LipPressorL, .4f * beat(t, .2f, .5f)); both(LipPuckerL, curve(t, .55f, .9f, 4.9f, 1.1f));
            both(LipFunnelerLB, .25f * curve(t, .7f, .9f, 4.8f, 1)); both(LipFunnelerRB, w[LipFunnelerLB]);
            break;
        case 3: case 4: case 5: {
            float fill = curve(t, .75f, .8f, 4.9f, 1.1f);
            w[JawDrop] = .15f * beat(t, .1f, .55f);
            both(LipPressorL, .5f * curve(t, .5f, .35f, 4.85f, .9f)); both(CheekRaiserL, .15f * curve(t, .9f, .8f, 4.8f, 1));
            if (step != 5) w[CheekPuffR] = fill;
            if (step != 4) w[CheekPuffL] = fill;
            break; }
        case 11:
            both(CheekSuckL, curve(t, .55f, 1, 4.9f, 1.1f)); both(LipPuckerL, .25f * curve(t, .65f, .9f, 4.85f, 1));
            break;
        default: if (step >= 6 && step <= 10) {
            static const float direction[5][2] = {{0, 0}, {0, 1}, {0, -1}, {-1, 0}, {1, 0}};
            w[JawDrop] = .7f * curve(t, .3f, .8f, 5.05f, .95f);
            float out = curve(t, .8f, .7f, 4.55f, .55f), aim = curve(t, 1.15f, .6f, 4.4f, .45f);
            g.tongue = {out, direction[step - 6][0] * aim, direction[step - 6][1] * aim};
        }
    }
}
}

extern "C" JNIEXPORT void JNICALL Java_com_qftplus_headset_CalibrationActivity_stopScene(JNIEnv*, jobject) { stopping = true; }
extern "C" JNIEXPORT jstring JNICALL Java_com_qftplus_headset_CalibrationActivity_runScene(JNIEnv* env, jobject activity, jstring library, jstring assets,
        jbyteArray preset, jfloatArray values, jboolean pupils, jobjectArray setupSpace) {
    std::string error; stopping = false;
    try {
        Scene s; s.setup(env, activity, pupils);
        jclass cls = env->GetObjectClass(activity);
        jmethodID update = env->GetMethodID(cls, "updateScene", "(Z)I"), cancelCalibration = env->GetMethodID(cls, "cancelCalibration", "()V"),
            startCalibration = env->GetMethodID(cls, "startCalibration", "()V"), scenePresented = env->GetMethodID(cls, "scenePresented", "()V"),
            panelReady = env->GetMethodID(cls, "panelSurface", "(Landroid/view/Surface;II)V"), pointer = env->GetMethodID(cls, "pointer", "(FFZ)V"),
            avatarReady = env->GetMethodID(cls, "avatarReady", "(Z)V");
        env->DeleteLocalRef(cls);
        require(update && cancelCalibration && startCalibration && scenePresented && panelReady && pointer && avatarReady, "Calibration callback missing");
        const int panelWidth = 1600, panelHeight = 800;
        auto rethrow = [&] { if (env->ExceptionCheck()) { env->ExceptionClear(); throw std::runtime_error("Calibration UI failed"); } };
        env->CallVoidMethod(activity, panelReady, s.createPanel(panelWidth, panelHeight), panelWidth, panelHeight); rethrow();
        if (!pupils && setupSpace) s.loadSpace(setupSpace);
        if (!pupils && preset) {
            auto string = [&](jstring j) { const char* c = env->GetStringUTFChars(j, nullptr); std::string r(c); env->ReleaseStringUTFChars(j, c); return r; };
            std::vector<uint8_t> bytes(env->GetArrayLength(preset)); env->GetByteArrayRegion(preset, 0, jsize(bytes.size()), reinterpret_cast<jbyte*>(bytes.data()));
            s.guide.start(string(library), string(assets), std::move(bytes));
        }
        auto faceWeights = function<PFN_xrGetFaceExpressionWeights2FB>(s.instance, "xrGetFaceExpressionWeights2FB");
        auto eyeGazes = function<PFN_xrGetEyeGazesFB>(s.instance, "xrGetEyeGazesFB");
        bool running = false, focused = false, presented = false, anchored = false, avatarReported = false; float state[8] = {};
        Mat place = avatar::identity(), spacePlace = avatar::identity(); XrPosef panelPose{}, captionPose{}; float yaw = 0; XrTime recenterAt = 0, last = 0; int hand = 1;
        while (!stopping) {
            XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
            while (xrPollEvent(s.instance, &event) == XR_SUCCESS) {
                if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                    auto sessionState = reinterpret_cast<XrEventDataSessionStateChanged*>(&event)->state; focused = sessionState == XR_SESSION_STATE_FOCUSED;
                    __android_log_print(ANDROID_LOG_INFO, "QFTCalibration", "OpenXR state=%d", sessionState);
                    if (sessionState == XR_SESSION_STATE_READY) { XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO}; begin.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; xr(xrBeginSession(s.session, &begin), "Begin calibration"); running = true; }
                    if (sessionState == XR_SESSION_STATE_STOPPING) { xr(xrEndSession(s.session), "End calibration"); running = false; stopping = true; }
                    if (sessionState == XR_SESSION_STATE_EXITING || sessionState == XR_SESSION_STATE_LOSS_PENDING) stopping = true;
                } else if (event.type == XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING) {
                    const auto* change = reinterpret_cast<XrEventDataReferenceSpaceChangePending*>(&event);
                    if (change->referenceSpaceType == XR_REFERENCE_SPACE_TYPE_LOCAL) recenterAt = change->changeTime;
                } else if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) stopping = true;
                event = {XR_TYPE_EVENT_DATA_BUFFER};
            }
            if (stopping) break;
            int changed = env->CallIntMethod(activity, update, focused);
            rethrow();
            if (changed < 0) break;
            env->GetFloatArrayRegion(values, 0, 8, state);
            if (!running) { std::this_thread::sleep_for(std::chrono::milliseconds(25)); continue; }
            XrActiveActionSet active{s.actions, XR_NULL_PATH}; XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO}; sync.countActiveActionSets = 1; sync.activeActionSets = &active;
            xr(xrSyncActions(s.session, &sync), "Read calibration controls");
            XrActionStateGetInfo get{XR_TYPE_ACTION_STATE_GET_INFO}; XrActionStateBoolean button{XR_TYPE_ACTION_STATE_BOOLEAN};
            get.action = s.cancel; xr(xrGetActionStateBoolean(s.session, &get, &button), "Read leave button");
            if (button.isActive && button.currentState) { env->CallVoidMethod(activity, cancelCalibration); rethrow(); break; }
            get.action = s.start; xr(xrGetActionStateBoolean(s.session, &get, &button), "Read start button");
            if (button.isActive && button.changedSinceLastSync && button.currentState) { env->CallVoidMethod(activity, startCalibration); rethrow(); }
            bool pressed[2] = {};
            for (int h = 0; h < 2; h++) {
                get.action = s.select; get.subactionPath = s.hands[h]; XrActionStateBoolean trigger{XR_TYPE_ACTION_STATE_BOOLEAN};
                xr(xrGetActionStateBoolean(s.session, &get, &trigger), "Read trigger"); pressed[h] = trigger.isActive && trigger.currentState;
                if (trigger.isActive && trigger.changedSinceLastSync && trigger.currentState) hand = h;
            }
            get.subactionPath = XR_NULL_PATH;
            XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO}; XrFrameState frame{XR_TYPE_FRAME_STATE}; xr(xrWaitFrame(s.session, &wait, &frame), "Wait for calibration frame");
            XrFrameBeginInfo begin{XR_TYPE_FRAME_BEGIN_INFO}; xr(xrBeginFrame(s.session, &begin), "Begin calibration frame");
            float weights[70] = {}, confidences[2] = {}; XrFaceExpressionInfo2FB fi{XR_TYPE_FACE_EXPRESSION_INFO2_FB}; fi.time = frame.predictedDisplayTime;
            XrFaceExpressionWeights2FB fw{XR_TYPE_FACE_EXPRESSION_WEIGHTS2_FB}; fw.weightCount = 70; fw.weights = weights; fw.confidenceCount = 2; fw.confidences = confidences;
            xr(faceWeights(s.face, &fi, &fw), "Read face tracking"); XrEyeGazesInfoFB ei{XR_TYPE_EYE_GAZES_INFO_FB}; ei.baseSpace = s.viewSpace; ei.time = frame.predictedDisplayTime; XrEyeGazesFB gaze{XR_TYPE_EYE_GAZES_FB}; xr(eyeGazes(s.eyes, &ei, &gaze), "Read eye tracking");

            bool pupil = state[3] != 0;
            if (!pupil) {
                float dt = last ? std::min(.1f, float(frame.predictedDisplayTime - last) / 1e9f) : 0; last = frame.predictedDisplayTime;
                expression(int(state[1]), state[2], s.guide); s.guide.idle = state[6] == 0; s.guide.update(dt);
                if (!avatarReported && (s.guide.ready() || s.guide.failed())) { env->CallVoidMethod(activity, avatarReady, jboolean(s.guide.ready())); rethrow(); avatarReported = true; }
            }
            XrCompositionLayerProjection projection{XR_TYPE_COMPOSITION_LAYER_PROJECTION}; projection.space = s.worldSpace;
            XrCompositionLayerProjectionView projectionViews[2] = {{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
            XrCompositionLayerQuad quad{XR_TYPE_COMPOSITION_LAYER_QUAD}; quad.space = s.worldSpace; quad.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT; quad.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
            quad.subImage.swapchain = s.panel; quad.subImage.imageRect.extent = {panelWidth, panelHeight};
            XrCompositionLayerImageLayoutFB flip{XR_TYPE_COMPOSITION_LAYER_IMAGE_LAYOUT_FB}; flip.flags = XR_COMPOSITION_LAYER_IMAGE_LAYOUT_VERTICAL_FLIP_BIT_FB; quad.next = &flip;
            const XrCompositionLayerBaseHeader* layers[] = {reinterpret_cast<XrCompositionLayerBaseHeader*>(&projection), reinterpret_cast<XrCompositionLayerBaseHeader*>(&quad)};
            uint32_t layerCount = 0;
            if (frame.shouldRender) {
                XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO}; li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; li.displayTime = frame.predictedDisplayTime; li.space = s.worldSpace;
                XrViewState vs{XR_TYPE_VIEW_STATE}; XrView views[2] = {{XR_TYPE_VIEW}, {XR_TYPE_VIEW}}; uint32_t count = 0;
                xr(xrLocateViews(s.session, &li, &vs, 2, &count, views), "Locate calibration views");
                if (count == 2 && (vs.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) && (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT)) {
                    if (recenterAt && frame.predictedDisplayTime >= recenterAt) { anchored = false; recenterAt = 0; }
                    float distance = state[4];
                    if (!anchored) {
                        const auto& q = views[0].pose.orientation;
                        yaw = std::atan2(2*(q.w*q.y + q.x*q.z), 1 - 2*(q.x*q.x + q.y*q.y));
                        Vec3 head{(views[0].pose.position.x + views[1].pose.position.x) * .5f, (views[0].pose.position.y + views[1].pose.position.y) * .5f, (views[0].pose.position.z + views[1].pose.position.z) * .5f};
                        Mat anchor = avatar::multiply(avatar::translation(head), avatar::rotation({0, 1, 0}, yaw * 180 / 3.14159265f));
                        float floor = head.y - 1.2f;
                        XrSpaceLocation stage{XR_TYPE_SPACE_LOCATION};
                        if (s.stageSpace && XR_SUCCEEDED(xrLocateSpace(s.stageSpace, s.worldSpace, frame.predictedDisplayTime, &stage)) && (stage.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT))
                            floor = stage.pose.position.y;
                        spacePlace = avatar::multiply(avatar::translation({head.x, floor, head.z}), avatar::rotation({0, 1, 0}, yaw * 180 / 3.14159265f));
                        place = anchor;
                        float tilt = pupil ? 0 : -.26f;
                        Mat panelMat = avatar::multiply(anchor, avatar::multiply(avatar::translation({0, pupil ? 0 : -.31f, -(pupil ? 1.f : distance + .05f)}), avatar::rotation({1, 0, 0}, tilt * 180 / 3.14159265f)));
                        Mat captionMat = avatar::multiply(anchor, avatar::multiply(avatar::translation({0, -.25f, -(distance - .03f)}),
                            avatar::rotation({1, 0, 0}, -std::atan2(.25f, distance) * 180 / 3.14159265f)));
                        panelPose = toPose(panelMat); captionPose = toPose(captionMat);
                        anchored = true;
                    }
                    bool caption = state[5] == 1;
                    quad.pose = caption ? captionPose : panelPose; quad.size = caption ? XrExtent2Df{.30f, .15f} : pupil ? XrExtent2Df{.40f, .20f} : XrExtent2Df{.56f, .28f};
                    Mat avatarModel = avatar::multiply(place, avatar::multiply(avatar::translation({0, -.02f, -distance}),
                        avatar::translation({-s.guide.head.x, -(s.guide.head.y + .07f), -(s.guide.head.z + .09f)})));
                    Vec3 light = avatar::normalize(avatar::apply(avatar::rotation({0, 1, 0}, yaw * 180 / 3.14159265f), {.25f, .8f, .55f}));
                    XrSpaceLocation aim{XR_TYPE_SPACE_LOCATION}; bool aiming = false; Vec3 from{}, to{};
                    if (XR_SUCCEEDED(xrLocateSpace(s.aim[hand], s.worldSpace, frame.predictedDisplayTime, &aim)) && (aim.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) && (aim.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT)) {
                        Mat a = fromPose(aim.pose); from = {a.m[12], a.m[13], a.m[14]}; Vec3 dir{-a.m[8], -a.m[9], -a.m[10]};
                        Mat p = fromPose(quad.pose); Vec3 n{p.m[8], p.m[9], p.m[10]}, c{p.m[12], p.m[13], p.m[14]};
                        float denom = dir.x*n.x + dir.y*n.y + dir.z*n.z, t = denom < -1e-4f ? ((c.x-from.x)*n.x + (c.y-from.y)*n.y + (c.z-from.z)*n.z) / denom : -1;
                        float u = -1, v = -1;
                        if (t > 0) {
                            Vec3 hit{from.x + dir.x*t - c.x, from.y + dir.y*t - c.y, from.z + dir.z*t - c.z};
                            u = (hit.x*p.m[0] + hit.y*p.m[1] + hit.z*p.m[2]) / quad.size.width + .5f; v = .5f - (hit.x*p.m[4] + hit.y*p.m[5] + hit.z*p.m[6]) / quad.size.height;
                        }
                        bool onPanel = u >= 0 && u <= 1 && v >= 0 && v <= 1;
                        env->CallVoidMethod(activity, pointer, onPanel ? u : -1.f, onPanel ? v : -1.f, jboolean(pressed[hand])); rethrow();
                        float length = onPanel ? t : .6f; to = {from.x + dir.x*length, from.y + dir.y*length, from.z + dir.z*length}; aiming = state[5] != 2;
                    }
                    for (int i = 0; i < 2; i++) {
                        EyeTarget& e = s.eye[i]; uint32_t image; XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO}; xr(xrAcquireSwapchainImage(e.swapchain, &ai, &image), "Acquire eye image");
                        XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO}; wi.timeout = XR_INFINITE_DURATION; xr(xrWaitSwapchainImage(e.swapchain, &wi), "Wait for eye image");
                        glBindFramebuffer(GL_FRAMEBUFFER, s.framebuffer);
                        framebufferTextureMultisample(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, e.images[image].image, 0, 4);
                        glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, e.depth);
                        require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "Invalid eye framebuffer");
                        glViewport(0, 0, e.width, e.height);
                        float level = pupil ? std::pow(state[0], 2.2f) : 0; glClearColor(level, level, level, 1); glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
                        glEnable(GL_DEPTH_TEST);
                        Mat vp = viewProjection(views[i]);
                        if (!pupil) {
                            s.drawSpace(vp, spacePlace);
                            if (s.guide.ready()) {
                                glUseProgram(s.avatarProgram); glEnable(GL_CULL_FACE); glCullFace(GL_BACK); glFrontFace(GL_CCW);
                                glUniformMatrix4fv(glGetUniformLocation(s.avatarProgram, "u_ViewProj"), 1, GL_FALSE, vp.m);
                                glUniformMatrix4fv(glGetUniformLocation(s.avatarProgram, "u_Model"), 1, GL_FALSE, avatarModel.m);
                                glUniform3f(glGetUniformLocation(s.avatarProgram, "u_CameraPos"), views[i].pose.position.x, views[i].pose.position.y, views[i].pose.position.z);
                                glUniform3f(glGetUniformLocation(s.avatarProgram, "u_LightDir"), light.x, light.y, light.z);
                                glUniform3f(glGetUniformLocation(s.avatarProgram, "u_LightColor"), 3.93f, 3.77f, 3.61f);
                                glUniform3f(glGetUniformLocation(s.avatarProgram, "u_SkyColor"), .95f, 1.f, 1.1f);
                                glUniform3f(glGetUniformLocation(s.avatarProgram, "u_GroundColor"), .5f, .48f, .46f);
                                glUniform2f(glGetUniformLocation(s.avatarProgram, "u_Bust"), s.guide.head.y - .32f, s.guide.head.y - .16f);
                                glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
                                s.guide.draw(s.avatarProgram); glDisable(GL_CULL_FACE); glDisable(GL_BLEND);
                            }
                        }
                        if (aiming) {
                            glUseProgram(s.rayProgram); glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
                            glUniformMatrix4fv(glGetUniformLocation(s.rayProgram, "u_ViewProj"), 1, GL_FALSE, vp.m);
                            glUniform3f(glGetUniformLocation(s.rayProgram, "u_From"), from.x, from.y, from.z); glUniform3f(glGetUniformLocation(s.rayProgram, "u_To"), to.x, to.y, to.z);
                            glBindVertexArray(s.empty); glLineWidth(3); glDrawArrays(GL_LINES, 0, 2); glDisable(GL_BLEND);
                        }
                        glBindFramebuffer(GL_FRAMEBUFFER, 0);
                        XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO}; xr(xrReleaseSwapchainImage(e.swapchain, &ri), "Release eye image");
                        projectionViews[i].pose = views[i].pose; projectionViews[i].fov = views[i].fov; projectionViews[i].subImage.swapchain = e.swapchain;
                        projectionViews[i].subImage.imageRect.extent = {e.width, e.height};
                    }
                    projection.viewCount = 2; projection.views = projectionViews; layerCount = 2;
                }
            }
            XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO}; end.displayTime = frame.predictedDisplayTime; end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; end.layerCount = layerCount; end.layers = layers;
            xr(xrEndFrame(s.session, &end), "Submit calibration frame");
            if (focused && layerCount && !presented) { env->CallVoidMethod(activity, scenePresented); rethrow(); presented = true; }
        }
        if (running) xrRequestExitSession(s.session);
    } catch (const std::exception& e) { error = e.what(); __android_log_print(ANDROID_LOG_ERROR, "QFTCalibration", "%s", error.c_str()); }
    __android_log_print(ANDROID_LOG_INFO, "QFTCalibration", "Scene and tracking resources released");
    return env->NewStringUTF(error.c_str());
}
