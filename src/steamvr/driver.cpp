#include <winsock2.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "contact.h"
#include "openvr_driver.h"

using namespace vr;

namespace {

constexpr u_short kPort = 27055;
constexpr uint64_t kStaleUs = 200000;
constexpr const char *kSection = "driver_qftplus";

struct Trackpad {
    PropertyContainerHandle_t container = k_ulInvalidPropertyContainer;
    VRInputComponentHandle_t x = k_ulInvalidInputComponentHandle, y = k_ulInvalidInputComponentHandle;
    VRInputComponentHandle_t touch = k_ulInvalidInputComponentHandle, force = k_ulInvalidInputComponentHandle;
    VRInputComponentHandle_t click = k_ulInvalidInputComponentHandle;
    VRInputComponentHandle_t slideX = k_ulInvalidInputComponentHandle, slideY = k_ulInvalidInputComponentHandle;
    VRInputComponentHandle_t slideTouch = k_ulInvalidInputComponentHandle;
};

struct Cursor {
    bool tracking = false, down = false;
    float x = 0, y = 0, carryX = 0, carryY = 0;
};

std::mutex g_lock;
Pad g_pads[2];
Trackpad g_trackpads[2];
Cursor g_cursors[2];
struct Slide {
    float position = 0, curl = 0;
    uint64_t received = 0;
} g_slides[2];
Tuning g_tuning;
Mode g_mode = Mode::Native;
uint64_t g_settingsRead = 0;
std::atomic<bool> g_running{false};
std::thread g_receiver;

void Log(const std::string &line) { VRDriverLog()->Log(("[qftplus] " + line).c_str()); }

uint64_t Now() {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

float Axis(uint16_t raw, float lo, float hi) {
    return std::clamp((raw - lo) / (hi - lo) * 2.0f - 1.0f, -1.0f, 1.0f);
}

float Setting(const char *key, float fallback) {
    EVRSettingsError error = VRSettingsError_None;
    float value = VRSettings()->GetFloat(kSection, key, &error);
    return error == VRSettingsError_None && std::isfinite(value) ? value : fallback;
}

constexpr const char *kModes[] = {"native", "joystick", "swipe", "mouse"};

Mode ReadMode() {
    char text[32] = "";
    EVRSettingsError error = VRSettingsError_None;
    VRSettings()->GetString(kSection, "mode", text, sizeof(text), &error);
    for (int i = 0; error == VRSettingsError_None && i < 4; i++)
        if (std::strcmp(text, kModes[i]) == 0) return static_cast<Mode>(i);
    return Mode::Native;
}

void ReadSettings() {
    const Tuning d;
    Tuning k;
    k.restingSize = Setting("restingSize", d.restingSize);
    k.decideMs = Setting("restingDecideMs", d.decideMs);
    k.flatMs = Setting("restingFlatMs", d.flatMs);
    k.pressForce = Setting("pressForce", d.pressForce);
    k.forceLow = Setting("forceLow", d.forceLow);
    k.forceHigh = Setting("forceHigh", d.forceHigh);
    k.forceCurve = Setting("forceCurve", d.forceCurve);
    k.clickOn = Setting("clickOn", d.clickOn);
    k.clickOff = Setting("clickOff", d.clickOff);
    k.joystickRange = Setting("joystickRange", d.joystickRange);
    k.swipeGain = Setting("swipeGain", d.swipeGain);
    k.swipeDecayMs = Setting("swipeDecayMs", d.swipeDecayMs);
    k.swipeDeadzone = Setting("swipeDeadzone", d.swipeDeadzone);
    k.railAngle = Setting("railAngle", d.railAngle);
    k.railStart = Setting("railStart", d.railStart);
    k.railRelease = Setting("railRelease", d.railRelease);
    k.xMin = Setting("trackpadXMin", d.xMin);
    k.xMax = Setting("trackpadXMax", d.xMax);
    k.yMin = Setting("trackpadYMin", d.yMin);
    k.yMax = Setting("trackpadYMax", d.yMax);
    k.rotation[0] = Setting("leftRotation", d.rotation[0]);
    k.rotation[1] = Setting("rightRotation", d.rotation[1]);
    k.mouseSpeed = Setting("mouseSpeed", d.mouseSpeed);
    k.slideTouchCurl = Setting("triggerTouchCurl", d.slideTouchCurl);
    k.slideReversed = Setting("triggerSlideReversed", d.slideReversed) != 0;
    if (k.forceHigh <= k.forceLow || k.forceCurve <= 0 || k.joystickRange <= 0 || k.swipeDecayMs < 1 ||
        k.xMax <= k.xMin || k.yMax <= k.yMin || std::fabs(k.mouseSpeed) > 1e6f)
        k = d;
    Mode mode = ReadMode();
    std::lock_guard<std::mutex> hold(g_lock);
    g_tuning = k;
    if (mode != g_mode) Log(std::string("thumbrest mode ") + kModes[static_cast<int>(mode)]);
    g_mode = mode;
}

void Adopt(uint32_t id) {
    CVRPropertyHelpers *props = VRProperties();
    PropertyContainerHandle_t c = props->TrackedDeviceToPropertyContainer(id);
    std::string model = props->GetStringProperty(c, Prop_RenderModelName_String);
    if (model.find("quest_pro") == std::string::npos) return;
    std::string profile = props->GetStringProperty(c, Prop_InputProfilePath_String);
    int role = props->GetInt32Property(c, Prop_ControllerRoleHint_Int32);
    if (profile != "{oculus}/input/touch_profile.json" ||
        (role != TrackedControllerRole_LeftHand && role != TrackedControllerRole_RightHand)) {
        Log("left " + model + " alone (input profile " + profile + ")");
        return;
    }
    int side = role == TrackedControllerRole_RightHand;
    props->SetStringProperty(c, Prop_InputProfilePath_String, "{qftplus}/input/quest_pro_touch_profile.json");
    props->SetStringProperty(c, Prop_ControllerType_String, "quest_pro_touch");
    Trackpad pad{c};
    IVRDriverInput *input = VRDriverInput();
    input->CreateScalarComponent(c, "/input/trackpad/x", &pad.x, VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided);
    input->CreateScalarComponent(c, "/input/trackpad/y", &pad.y, VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided);
    input->CreateBooleanComponent(c, "/input/trackpad/touch", &pad.touch);
    input->CreateScalarComponent(c, "/input/trackpad/force", &pad.force, VRScalarType_Absolute, VRScalarUnits_NormalizedOneSided);
    input->CreateBooleanComponent(c, "/input/trackpad/click", &pad.click);
    input->CreateScalarComponent(c, "/input/trigger_slide/x", &pad.slideX, VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided);
    input->UpdateScalarComponent(pad.slideX, 0, 0);
    input->CreateScalarComponent(c, "/input/trigger_slide/y", &pad.slideY, VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided);
    input->CreateBooleanComponent(c, "/input/trigger_slide/touch", &pad.slideTouch);
    std::lock_guard<std::mutex> hold(g_lock);
    g_trackpads[side] = pad;
    Log(std::string("thumbrest trackpad added to ") + (side ? "right " : "left ") + model);
}

class Shim : public ITrackedDeviceServerDriver {
public:
    explicit Shim(ITrackedDeviceServerDriver *inner) : inner_(inner) {}
    EVRInitError Activate(uint32_t id) override {
        EVRInitError result = inner_->Activate(id);
        if (result == VRInitError_None) Adopt(id);
        return result;
    }
    void Deactivate() override { inner_->Deactivate(); }
    void EnterStandby() override { inner_->EnterStandby(); }
    void *GetComponent(const char *name) override { return inner_->GetComponent(name); }
    void DebugRequest(const char *request, char *response, uint32_t size) override { inner_->DebugRequest(request, response, size); }
    DriverPose_t GetPose() override { return inner_->GetPose(); }

private:
    ITrackedDeviceServerDriver *inner_;
};

using AddedFn = bool (*)(IVRServerDriverHost *, const char *, ETrackedDeviceClass, ITrackedDeviceServerDriver *);
AddedFn g_added = nullptr;
void **g_slot = nullptr;
std::vector<std::unique_ptr<Shim>> g_shims;

bool Added(IVRServerDriverHost *host, const char *serial, ETrackedDeviceClass kind, ITrackedDeviceServerDriver *driver) {
    if (kind == TrackedDeviceClass_Controller && driver != nullptr) {
        std::lock_guard<std::mutex> hold(g_lock);
        driver = g_shims.emplace_back(std::make_unique<Shim>(driver)).get();
    }
    return g_added(host, serial, kind, driver);
}

bool Patch(void *target) {
    DWORD old;
    if (!VirtualProtect(g_slot, sizeof(void *), PAGE_READWRITE, &old)) return false;
    g_slot[0] = target;
    VirtualProtect(g_slot, sizeof(void *), old, &old);
    return true;
}

bool Hook() {
    auto *host = static_cast<IVRServerDriverHost *>(VRDriverContext()->GetGenericInterface(IVRServerDriverHost_Version));
    if (host == nullptr) return false;
    g_slot = *reinterpret_cast<void ***>(host);
    g_added = reinterpret_cast<AddedFn>(g_slot[0]);
    return Patch(reinterpret_cast<void *>(&Added));
}

void Store(const char *packet) {
    if (packet[0] == 2 || packet[0] == 3) {
        Slide slide;
        std::memcpy(&slide.position, packet + 6, 4);
        std::memcpy(&slide.curl, packet + 10, 4);
        if (!std::isfinite(slide.position) || !std::isfinite(slide.curl)) return;
        slide.received = Now();
        std::lock_guard<std::mutex> hold(g_lock);
        g_slides[packet[0] - 2] = slide;
        return;
    }
    if (packet[0] != 0 && packet[0] != 1) return;
    uint16_t x, y;
    Pad pad;
    std::memcpy(&x, packet + 2, 2);
    std::memcpy(&y, packet + 4, 2);
    std::memcpy(&pad.force, packet + 6, 4);
    std::memcpy(&pad.size, packet + 10, 4);
    if (!std::isfinite(pad.force) || !std::isfinite(pad.size)) return;
    pad.state = static_cast<uint8_t>(packet[1]);
    pad.received = Now();
    std::lock_guard<std::mutex> hold(g_lock);
    pad.x = Axis(x, g_tuning.xMin, g_tuning.xMax);
    pad.y = Axis(y, g_tuning.yMin, g_tuning.yMax);
    Rotate(g_tuning.rotation[packet[0]], pad.x, pad.y);
    Classify(pad, g_pads[packet[0]], g_tuning);
    g_pads[packet[0]] = pad;
}

void Receive() {
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(kPort);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    while (g_running) {
        SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        bool streaming = false;
        if (connect(s, reinterpret_cast<sockaddr *>(&addr), sizeof(addr)) == 0) {
            char packet[14];
            int have = 0;
            while (g_running) {
                fd_set readable;
                FD_ZERO(&readable);
                FD_SET(s, &readable);
                timeval wait{0, 250000};
                int ready = select(0, &readable, nullptr, nullptr, &wait);
                if (ready == 0) continue;
                int count = ready < 0 ? -1 : recv(s, packet + have, static_cast<int>(sizeof(packet)) - have, 0);
                if (count <= 0) break;
                have += count;
                if (have < static_cast<int>(sizeof(packet))) continue;
                have = 0;
                if (!streaming) Log("headset thumbrest stream connected");
                streaming = true;
                Store(packet);
            }
        }
        closesocket(s);
        if (streaming) Log("headset thumbrest stream disconnected");
        for (int i = 0; i < 4 && g_running; i++) Sleep(250);
    }
    WSACleanup();
}

void Send(DWORD flags, LONG dx = 0, LONG dy = 0) {
    INPUT event{};
    event.type = INPUT_MOUSE;
    event.mi.dx = dx;
    event.mi.dy = dy;
    event.mi.dwFlags = flags;
    SendInput(1, &event, sizeof(event));
}

void MoveCursor(Cursor &cursor, const Pad &pad, bool active) {
    if (!active || g_mode != Mode::Mouse) {
        cursor.tracking = false;
        if (cursor.down) Send(MOUSEEVENTF_LEFTUP);
        cursor.down = false;
        return;
    }
    if (cursor.tracking) {
        cursor.carryX += (pad.x - cursor.x) / 2 * g_tuning.mouseSpeed;
        cursor.carryY -= (pad.y - cursor.y) / 2 * g_tuning.mouseSpeed;
        LONG dx = static_cast<LONG>(cursor.carryX), dy = static_cast<LONG>(cursor.carryY);
        cursor.carryX -= dx;
        cursor.carryY -= dy;
        if (dx != 0 || dy != 0) Send(MOUSEEVENTF_MOVE, dx, dy);
    }
    cursor.tracking = true;
    cursor.x = pad.x;
    cursor.y = pad.y;
    if (pad.click != cursor.down) Send(pad.click ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
    cursor.down = pad.click;
}

class Provider : public IServerTrackedDeviceProvider {
public:
    EVRInitError Init(IVRDriverContext *context) override {
        VR_INIT_SERVER_DRIVER_CONTEXT(context);
        ReadSettings();
        if (!Hook()) {
            Log("could not hook controller creation");
            return VRInitError_None;
        }
        g_running = true;
        g_receiver = std::thread(Receive);
        Log("ready; waiting for Quest Pro controllers");
        return VRInitError_None;
    }
    void Cleanup() override {
        g_running = false;
        if (g_receiver.joinable()) g_receiver.join();
        if (g_slot != nullptr && g_slot[0] == reinterpret_cast<void *>(&Added)) Patch(reinterpret_cast<void *>(g_added));
        g_shims.clear();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
    }
    const char *const *GetInterfaceVersions() override { return k_InterfaceVersions; }
    void RunFrame() override {
        if (g_running && Now() - g_settingsRead >= 1000000) {
            g_settingsRead = Now();
            ReadSettings();
        }
        struct Frame {
            Trackpad t;
            Report r;
            Pad pad;
            bool active = false, onTrigger = false;
            float slide = 0;
        } frames[2];
        {
            std::lock_guard<std::mutex> hold(g_lock);
            uint64_t now = Now();
            for (int side = 0; side < 2; side++) {
                Frame &f = frames[side];
                f.t = g_trackpads[side];
                if (f.t.container == k_ulInvalidPropertyContainer) continue;
                Pad &p = g_pads[side];
                if ((p.state & 1) && now - p.received >= kStaleUs) {
                    if (p.contact == Contact::Active) p.lifted = now;
                    p.state = 0;
                    p.contact = Contact::None;
                }
                f.r = Evaluate(p, now, g_mode, g_tuning);
                f.pad = p;
                f.active = (p.state & 1) && p.contact == Contact::Active;
                const Slide &s = g_slides[side];
                f.onTrigger = now - s.received < kStaleUs && s.curl >= g_tuning.slideTouchCurl;
                float along = std::clamp(s.position, 0.0f, 1.0f) * 2 - 1;
                f.slide = f.onTrigger ? (g_tuning.slideReversed ? -along : along) : 0;
            }
        }
        IVRDriverInput *input = VRDriverInput();
        for (int side = 0; side < 2; side++) {
            const Frame &f = frames[side];
            if (f.t.container == k_ulInvalidPropertyContainer) continue;
            MoveCursor(g_cursors[side], f.pad, f.active);
            input->UpdateBooleanComponent(f.t.touch, f.r.touch, 0);
            input->UpdateScalarComponent(f.t.x, f.r.x, 0);
            input->UpdateScalarComponent(f.t.y, f.r.y, 0);
            input->UpdateScalarComponent(f.t.force, f.r.force, 0);
            input->UpdateBooleanComponent(f.t.click, f.r.click, 0);
            input->UpdateBooleanComponent(f.t.slideTouch, f.onTrigger, 0);
            input->UpdateScalarComponent(f.t.slideY, f.slide, 0);
        }
    }
    bool ShouldBlockStandbyMode() override { return false; }
    void EnterStandby() override {}
    void LeaveStandby() override {}
};

Provider g_provider;

}

extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *name, int *error) {
    if (std::strcmp(name, IServerTrackedDeviceProvider_Version) == 0) return &g_provider;
    if (error != nullptr) *error = VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
