#pragma once

//========= Copyright Valve Corporation ============//

#include <algorithm>
#include <cmath>
#include <cstdint>

enum class Contact { None, Pending, Active, Rejected };
enum class Mode { Native, Joystick, Swipe, Mouse, Off };
enum class Rail { Undecided, Vertical, Horizontal, Free };

struct Tuning {
    float restingSize = 75, decideMs = 50, flatMs = 60, pressForce = 0.75f;
    float forceLow = 0.55f, forceHigh = 1.0f, forceCurve = 1.5f, clickOn = 0.7f, clickOff = 0.6f;
    float joystickRange = 0.5f;
    float swipeGain = 0.35f, swipeDecayMs = 350, swipeDeadzone = 0.3f;
    float railAngle = 35, railStart = 0.4f, railRelease = 0.8f;
};

struct Pad {
    float x = 0, y = 0;
    uint8_t state = 0;
    float force = 0, size = 0;
    uint64_t received = 0, touched = 0, flat = 0, lifted = 0;
    Contact contact = Contact::None;
    float anchorX = 0, anchorY = 0, vx = 0, vy = 0;
    bool click = false;
    Rail rail = Rail::Undecided;
    float onPath = 0, offPath = 0;
};

struct Report {
    bool touch = false, click = false;
    float x = 0, y = 0, force = 0;
};

inline uint64_t Us(float ms) { return static_cast<uint64_t>(ms * 1000); }

inline void Rotate(float degrees, float &x, float &y) {
    float r = degrees * 3.14159265f / 180, c = std::cos(r), s = std::sin(r);
    float rx = c * x - s * y, ry = s * x + c * y;
    x = std::clamp(rx, -1.0f, 1.0f);
    y = std::clamp(ry, -1.0f, 1.0f);
}

inline float Force(float raw, const Tuning &k) {
    return std::pow(std::clamp((raw - k.forceLow) / (k.forceHigh - k.forceLow), 0.0f, 1.0f), k.forceCurve);
}

inline void Begin(Pad &pad) {
    pad.anchorX = pad.x;
    pad.anchorY = pad.y;
    pad.rail = Rail::Undecided;
    pad.onPath = pad.offPath = 0;
}

inline void Steer(Pad &pad, const Pad &last, const Tuning &k) {
    if (pad.rail == Rail::Undecided) {
        float dx = pad.x - pad.anchorX, dy = pad.y - pad.anchorY;
        if (std::hypot(dx, dy) < k.railStart) return;
        float angle = std::fabs(std::atan2(dy, dx)) * 180 / 3.14159265f, fromHorizontal = std::min(angle, 180 - angle);
        pad.rail = fromHorizontal <= k.railAngle ? Rail::Horizontal : 90 - fromHorizontal <= k.railAngle ? Rail::Vertical : Rail::Free;
        return;
    }
    if (pad.rail == Rail::Free) return;
    bool vertical = pad.rail == Rail::Vertical;
    pad.onPath += std::fabs(vertical ? pad.y - last.y : pad.x - last.x);
    pad.offPath += std::fabs(vertical ? pad.x - last.x : pad.y - last.y);
    if (pad.offPath > k.railStart && pad.offPath > k.railRelease * pad.onPath) pad.rail = Rail::Free;
}

inline void OnRail(const Pad &pad, const Tuning &k, float &x, float &y) {
    Rail rail = pad.rail;
    if (rail == Rail::Undecided) {
        float dx = pad.x - pad.anchorX, dy = pad.y - pad.anchorY;
        if (dx == 0 && dy == 0) return;
        float angle = std::fabs(std::atan2(dy, dx)) * 180 / 3.14159265f, fromHorizontal = std::min(angle, 180 - angle);
        rail = fromHorizontal <= k.railAngle ? Rail::Horizontal : 90 - fromHorizontal <= k.railAngle ? Rail::Vertical : Rail::Free;
    }
    if (rail == Rail::Vertical) x = 0;
    if (rail == Rail::Horizontal) y = 0;
}

inline void Classify(Pad &pad, const Pad &last, const Tuning &k) {
    bool was = last.state & 1, wasActive = was && last.contact == Contact::Active;
    pad.lifted = last.lifted;
    pad.vx = last.vx;
    pad.vy = last.vy;
    pad.rail = last.rail;
    if (!(pad.state & 1)) {
        if (wasActive) pad.lifted = pad.received;
        return;
    }
    pad.contact = was ? last.contact : Contact::Pending;
    pad.touched = was ? last.touched : pad.received;
    pad.flat = last.flat;
    pad.anchorX = last.anchorX;
    pad.anchorY = last.anchorY;
    pad.onPath = last.onPath;
    pad.offPath = last.offPath;
    pad.click = last.click;
    if (!was) pad.vx = pad.vy = 0;
    if (k.restingSize <= 0 || pad.force >= k.pressForce) {
        pad.contact = Contact::Active;
    } else if (pad.contact == Contact::Pending && pad.size >= k.restingSize) {
        pad.contact = Contact::Rejected;
    } else if (pad.contact == Contact::Active) {
        if (pad.size < k.restingSize + 10) pad.flat = 0;
        else if (pad.flat == 0) pad.flat = pad.received;
        else if (pad.received - pad.flat >= Us(k.flatMs)) pad.contact = Contact::Rejected;
    }
    if (pad.contact == Contact::Active && !wasActive) Begin(pad);
    if (pad.contact == Contact::Active && wasActive && pad.received > last.received) {
        Steer(pad, last, k);
        float dt = (pad.received - last.received) / 1e6f, blend = std::min(1.0f, dt / 0.04f);
        pad.vx += ((pad.x - last.x) / 2 / dt - pad.vx) * blend;
        pad.vy += ((pad.y - last.y) / 2 / dt - pad.vy) * blend;
    }
    if (pad.contact != Contact::Active) pad.vx = pad.vy = 0;
    float force = Force(pad.force, k);
    pad.click = pad.contact == Contact::Active && force >= (pad.click ? k.clickOff : k.clickOn);
}

inline bool Touching(Pad &pad, uint64_t now, const Tuning &k) {
    if (pad.contact == Contact::Pending && now - pad.touched >= Us(k.decideMs)) {
        pad.contact = Contact::Active;
        Begin(pad);
    }
    return (pad.state & 1) && pad.contact == Contact::Active;
}

inline float Glide(float velocity, bool touching, uint64_t lifted, uint64_t now, const Tuning &k) {
    if (std::fabs(velocity) < k.swipeDeadzone) return 0;
    float fade = touching ? 1.0f : std::exp(-static_cast<float>(now - lifted) / Us(k.swipeDecayMs));
    float value = std::clamp(velocity * k.swipeGain * fade, -1.0f, 1.0f);
    return std::fabs(value) < 0.02f ? 0 : value;
}

inline Report Evaluate(Pad &pad, uint64_t now, Mode mode, const Tuning &k) {
    Report r;
    bool touching = Touching(pad, now, k);
    if (mode == Mode::Mouse || mode == Mode::Off) return r;
    r.force = touching ? Force(pad.force, k) : 0;
    r.click = touching && pad.click;
    if (mode == Mode::Native) {
        r.touch = touching;
        r.x = touching ? pad.x : 0;
        r.y = touching ? pad.y : 0;
    } else if (mode == Mode::Joystick) {
        r.touch = touching;
        r.x = touching ? std::clamp((pad.x - pad.anchorX) / k.joystickRange, -1.0f, 1.0f) : 0;
        r.y = touching ? std::clamp((pad.y - pad.anchorY) / k.joystickRange, -1.0f, 1.0f) : 0;
        OnRail(pad, k, r.x, r.y);
    } else {
        r.x = Glide(pad.vx, touching, pad.lifted, now, k);
        r.y = Glide(pad.vy, touching, pad.lifted, now, k);
        OnRail(pad, k, r.x, r.y);
        r.touch = touching || r.x != 0 || r.y != 0;
    }
    return r;
}
