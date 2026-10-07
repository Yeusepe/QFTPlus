#include "tracking_model.h"
#include "pupil_detector.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <deque>
#include <fstream>
#include <future>
#include <memory>
#include <opencv2/core.hpp>
#include <stdexcept>
#include <vector>

static_assert(sizeof(TrackingControl) == 312 && sizeof(TrackingResult) == 264, "Tracking wire ABI");
namespace {
double clip(double x, double low = 0, double high = 1) { return std::clamp(x, low, high); }
double ramp(double x, double low, double high) { return clip((x - low) / (high - low)); }
struct Reader {
    std::ifstream file;
    explicit Reader(const char *directory)
        : file(std::string(directory) + "/profile.bin", std::ios::binary) {}
    void read(void *to, size_t bytes) {
        if (!file.read(static_cast<char *>(to), bytes))
            throw std::runtime_error("Incomplete headset profile");
    }
    cv::Mat matrix(int rows, int cols) {
        cv::Mat m(rows, cols, CV_32F);
        read(m.data, m.total() * 4);
        for (size_t i = 0; i < m.total(); i++)
            if (!std::isfinite(m.ptr<float>()[i]))
                throw std::runtime_error("Non-finite headset profile");
        return m;
    }
    template <size_t N> void floats(std::array<float, N> &a) {
        read(a.data(), N * 4);
        for (float x : a)
            if (!std::isfinite(x))
                throw std::runtime_error("Non-finite headset profile");
    }
};
struct Layer {
    cv::Mat w, b;
    Layer(Reader &r, int rows, int cols) : w(r.matrix(rows, cols)), b(r.matrix(rows, 1)) {}
    void run(const float *in, float *out, int activation) const {
        cv::Mat x(w.cols, 1, CV_32F, const_cast<float *>(in)), y(w.rows, 1, CV_32F, out);
        cv::gemm(w, x, 1., b, 1., y);
        for (int i = 0; i < w.rows; i++) {
            float s = 1.f / (1.f + std::exp(-std::clamp(out[i], -60.f, 60.f)));
            if (activation == 1)
                out[i] *= s;
            else if (activation == 2)
                out[i] = s;
        }
    }
};
struct Profile {
    uint32_t capabilities = 0;
    std::vector<Layer> mouth, brows;
    std::array<float, 1024> tongue{};
    std::array<float, 2> tongue_bias{}, small{}, large{}, puff_camera{}, puff_threshold{}, puff_amount{};
    std::array<float, 4> gains{};
    std::array<float, 512> puff_rest{}, puff_direction{};
    std::array<float, 13> neutral{}, reach{};
    std::array<float, 1> bias{};
    explicit Profile(const char *directory) {
        Reader r(directory);
        char magic[8];
        uint32_t bytes;
        r.read(magic, 8);
        r.read(&capabilities, 4);
        r.read(&bytes, 4);
        if (std::memcmp(magic, "QFTHP002", 8) || capabilities > 15 || bytes > 4000000)
            throw std::runtime_error("Unsupported headset profile");
        mouth.emplace_back(r, 512, 512);
        mouth.emplace_back(r, 256, 512);
        mouth.emplace_back(r, 4, 256);
        brows.emplace_back(r, 128, 480);
        brows.emplace_back(r, 8, 128);
        r.floats(tongue);
        r.floats(tongue_bias);
        r.floats(gains);
        r.floats(puff_rest);
        r.floats(puff_direction);
        r.floats(puff_camera);
        r.floats(puff_threshold);
        r.floats(puff_amount);
        r.floats(neutral);
        r.floats(reach);
        r.floats(bias);
        r.floats(small);
        r.floats(large);
        if (static_cast<uint32_t>(r.file.tellg()) != bytes + 16 || r.file.peek() != EOF)
            throw std::runtime_error("Invalid headset profile length");
        if (std::abs(bias[0]) > .10001)
            throw std::runtime_error("Invalid expression bias");
        for (int i = 0; i < 2; i++)
            if (small[i] < 10 || large[i] > 60 || large[i] - small[i] < std::max(3.f, small[i] * .1f))
                throw std::runtime_error("Invalid pupil calibration range");
        for (int i = 0; i < 2; i++)
            if ((capabilities & 2) && ((puff_camera[i] != 0 && puff_camera[i] != 1) || puff_threshold[i] < .15f || puff_threshold[i] > .5f))
                throw std::runtime_error("Invalid cheek calibration");
    }
};
bool lower_face(int i) {
    return (i >= 2 && i <= 11) || (i >= 24 && i <= 27) || (i >= 30 && i <= 54) || (i >= 61 && i <= 69);
}
bool native_brow(int i) { return i <= 1 || i == 22 || i == 23 || i == 57 || i == 58; }
uint64_t stage_ns[6];
uint64_t clock_ns() { timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return uint64_t(t.tv_sec) * 1000000000u + t.tv_nsec; }
}
extern "C" void tracking_model_stages(uint64_t out[6]) { std::memcpy(out, stage_ns, sizeof stage_ns); std::memset(stage_ns, 0, sizeof stage_ns); }
extern "C" void tracking_model_thumbnails(const uint8_t *strip, float out[512]) {
    for (int c = 0; c < 2; c++)
        for (int by = 0; by < 16; by++)
            for (int bx = 0; bx < 16; bx++) {
                unsigned sum = 0;
                for (int y = 0; y < 25; y++) {
                    const uint8_t *row = strip + (by * 25 + y) * 2000 + (2 + c) * 400 + bx * 25;
                    for (int x = 0; x < 25; x++)
                        sum += row[x];
                }
                out[c * 256 + by * 16 + bx] = sum / 159375.f;
            }
}

struct TrackingModel {
    std::unique_ptr<Profile> profile;
    TrackingControl control{};
    std::array<double, 13> neutral{};
    std::array<bool, 13> on{};
    std::array<double, 13> since{};
    double last = -1, last_native = -1, native_time = -1, share_time = -1;
    uint64_t native_sequence = UINT64_MAX;
    std::array<double, 2> share{};
    bool share_ready = false;
    std::array<float, 512> puff_reference{};
    double puff_time = -1;
    std::deque<std::pair<double, double>> speech;
    double pupil_time = -1e9, pupil_valid = -1e9;
    std::array<double, 2> pupil_last{}, pupil_seen{{-1e9, -1e9}}, pupil_values{};
    std::array<float, 14> geometry{};
    std::vector<uint8_t> previous_eyes;
    bool pupil_filtered = false, pupil_calibrating = false;
    std::array<float, 14> pupil_found{};
    double pupil_job_time = 0;
    std::future<void> pupil_job;
    void reset() {
        for (int i = 0; i < 13; i++) {
            neutral[i] = profile->neutral[i];
            on[i] = false;
            since[i] = -1;
        }
    }
    void reload(const char *directory) {
        auto next = std::make_unique<Profile>(directory);
        profile = std::move(next);
        reset();
        puff_reference = profile->puff_rest;
        last = last_native = share_time = puff_time = -1;
        share_ready = false;
        speech.clear();
        pupil_job = {};
        pupil_time = pupil_valid = -1e9;
        pupil_filtered = false;
        previous_eyes.clear();
        pupil_seen = {-1e9, -1e9};
    }
    bool fresh(double now) const { return native_time >= 0 && now - native_time <= .1; }
    bool has(int i) const { return std::isfinite(control.native_values[i]); }
    double native(int i) const { return has(i) ? control.native_values[i] : 0; }
    bool quiet(bool brow) const {
        int count = 0, rest = 0;
        for (int i = 0; i < 70; i++)
            if ((brow ? native_brow(i) : lower_face(i)) && has(i)) {
                count++;
                rest += native(i) < .15;
            }
        return count && rest >= .8 * count;
    }
    bool speaking(double now) const {
        std::vector<double> v;
        double first = 0, last_time = 0, sum = 0;
        for (auto [t, jaw] : speech)
            if (t >= now - 1) {
                if (v.empty())
                    first = t;
                v.push_back(jaw);
                last_time = t;
                sum += jaw;
            }
        if (v.size() < 10 || last_time - first <= .5)
            return false;
        double mean = sum / v.size(), variance = 0;
        int crossings = 0;
        for (size_t i = 0; i < v.size(); i++) {
            variance += (v[i] - mean) * (v[i] - mean);
            if (i)
                crossings += std::signbit(v[i] - mean) != std::signbit(v[i - 1] - mean);
        }
        double hz = crossings / (last_time - first);
        return std::sqrt(variance / v.size()) > .04 && hz >= 4 && hz <= 14;
    }
    void expressions(const uint8_t *strip, const float *features, double now, TrackingResult &out) {
        const auto &p = *profile;
        float a[512], b[256], values[13] = {};
        uint64_t t0 = clock_ns();
        p.mouth[0].run(features, a, 1);
        p.mouth[1].run(a, b, 1);
        p.mouth[2].run(b, values, 2);
        p.brows[0].run(features + 519, a, 1);
        p.brows[1].run(a, values + 4, 2);
        for (int d = 0; d < 2; d++) {
            double x = p.tongue_bias[d];
            for (int i = 0; i < 512; i++)
                x += p.tongue[d * 512 + i] * features[i];
            out.tongue[d + 1] = static_cast<float>(clip(x * p.gains[d * 2 + (x > 0)], -1, 1));
        }
        stage_ns[0] += clock_ns() - t0;
        bool valid = fresh(now);
        stage_ns[5] += !valid;
        double np[2] = {valid ? native(2) : 0, valid ? native(3) : 0};
        if (p.capabilities & 2) {
            float thumb[512];
            tracking_model_thumbnails(strip, thumb);
            double n = p.puff_amount[0], gain = clip(1 / std::max(p.puff_amount[1] - n, .001), .7, 2.5);
            double amount = clip((std::max({static_cast<double>(values[0]), static_cast<double>(values[1]), np[0], np[1]}) - n) * gain);
            double gate = clip((amount - .03) / .07), side[2];
            for (int s = 0; s < 2; s++) {
                int c = static_cast<int>(p.puff_camera[s]) * 256;
                double x = 0;
                for (int i = 0; i < 256; i++)
                    x += (thumb[c + i] - puff_reference[c + i]) * p.puff_direction[s * 256 + i];
                side[s] = clip(x);
                double t = p.puff_threshold[s], v = side[s] * gate;
                values[s] = static_cast<float>(v < t ? v * .5 / t : .5 + (v - t) * .5 / (1 - t));
            }
            double dt = puff_time < 0 ? 0 : std::clamp(now - puff_time, 0., .1);
            puff_time = now;
            if (amount < .05 && std::max(side[0], side[1]) < .15)
                for (int i = 0; i < 512; i++)
                    puff_reference[i] += static_cast<float>(.03 * dt * ((thumb[i] > puff_reference[i]) - (thumb[i] < puff_reference[i])));
        } else
            for (int i = 0; i < 2; i++)
                values[i] = static_cast<float>(std::max(static_cast<double>(values[i]), np[i]));
        double dt_share = share_time < 0 ? 0 : std::max(0., now - share_time);
        share_time = now;
        for (int brow = 0; brow < 2; brow++) {
            int i = 4 + brow * 2, n = brow ? 57 : 22;
            double a = values[i] + .05, b = values[i + 1] + .05,
                   k = share_ready ? 1 - std::exp(-dt_share / .15) : 1;
            share[brow] += k * (a / (a + b) - share[brow]);
            out.shares[2 * brow] = static_cast<float>(share[brow]);
            out.shares[2 * brow + 1] = static_cast<float>(1 - share[brow]);
            if (valid && has(n) && has(n + 1)) {
                double amp = (native(n) + native(n + 1)) / 2;
                values[i] = static_cast<float>(std::min(1., amp * 2 * share[brow]));
                values[i + 1] = static_cast<float>(std::min(1., amp * 2 * (1 - share[brow])));
            }
        }
        share_ready = true;
        values[12] = valid ? static_cast<float>(native(68)) : 0;
        out.tongue[0] = std::max(.4f, values[12]);
        double dt = last < 0 ? 0 : std::max(0., now - last);
        last = now;
        if (valid && native_time != last_native) {
            if (last_native >= 0 && native_time - last_native > 2)
                reset();
            last_native = native_time;
            if (has(24) && (speech.empty() || native_time > speech.back().first))
                speech.emplace_back(native_time, native(24));
            while (!speech.empty() && speech.front().first < native_time - 1)
                speech.pop_front();
        }
        bool talking = valid && speaking(now), qface = valid && quiet(false), qbrow = valid && quiet(true);
        if (valid)
            out.status |= TRACK_NATIVE_FRESH;
        if (talking)
            out.status |= TRACK_SPEAKING;
        double post[13];
        for (int i = 0; i < 13; i++) {
            bool brow = i >= 4 && i < 12;
            double n = neutral[i], gain = clip(1 / std::max(p.reach[i] - n, .001), .7, i < 2 ? 2.5 : 1.5);
            double v = clip((values[i] - n) * gain);
            if (i < 4 && valid && has(24) && has(50) && has(68))
                v *= 1 - std::max(ramp(native(24) - native(50), .2, .4), ramp(native(68), .3, .7));
            post[i] = v;
            bool settled = brow ? v < .5 : !on[i] && v < .5 && !talking;
            if ((brow ? qbrow : qface) && settled && dt)
                neutral[i] = clip(n + (values[i] - n) * std::min(1., dt / 20), 0, .5);
        }
        for (int i = 0; i < 13; i++) {
            double v = post[i];
            bool brow = i >= 4 && i < 12;
            if (!brow) {
                if (i < 4 && v <= std::max(post[i < 2 ? 2 : 0], post[i < 2 ? 3 : 1]))
                    v = 0;
                double raise = i < 4 && talking ? .2 : 0, hold_on = i == 12 ? (talking ? .3 : .15) : i < 2 ? 0 : .12,
                       hold_off = i == 12 ? 0 : .15;
                double threshold = (on[i] ? (i == 12 ? .42 : .35) : .5) + raise + p.bias[0];
                bool changing = on[i] ? v < threshold : v >= threshold;
                if (!changing)
                    since[i] = -1;
                else if (since[i] < 0)
                    since[i] = now;
                bool strong = i == 12 && !talking && v >= .9;
                if (changing && now - since[i] >= (on[i] ? hold_off : strong ? 0 : hold_on)) {
                    on[i] = !on[i];
                    since[i] = -1;
                }
            }
            double value = brow || on[i] ? v : 0;
            if (i < 2 && on[i] && (p.capabilities & 2)) {
                double level = .5 + (talking ? .2 : 0) + p.bias[0];
                value = clip((v - level) / (1 - level));
            }
            if (i < 12)
                out.expressions[i] = static_cast<float>(value);
            out.raw[i] = values[i];
            out.neutral[i] = static_cast<float>(neutral[i]);
            if (on[i])
                out.on |= 1u << i;
        }
        if (on[12])
            out.status |= TRACK_TONGUE_VISIBLE;
    }
    void pupils(const uint8_t *strip, double now, TrackingResult &out) {
        bool calibrating = (control.flags & TRACK_PUPIL_CALIBRATE) != 0;
        if (calibrating != pupil_calibrating) {
            pupil_calibrating = calibrating;
            pupil_job = {};
            pupil_time = pupil_valid = -1e9;
            pupil_filtered = false;
            pupil_seen = {-1e9, -1e9};
            previous_eyes.clear();
        }
        if (pupil_job.valid() && pupil_job.wait_for(std::chrono::seconds(0)) == std::future_status::ready) {
            pupil_job.get();
            out.status |= TRACK_PUPIL_UPDATED;
            geometry = pupil_found;
            double relative[2] = {NAN, NAN}, time = pupil_job_time;
            for (int eye = 0; eye < 2; eye++) {
                double small = profile->small[eye], span = profile->large[eye] - small,
                       range[] = {small - .35 * span, small + 1.35 * span};
                const float *g = geometry.data() + eye * 7;
                if (g[0]) {
                    double diameter = g[6], previous = pupil_last[eye], seen = pupil_seen[eye];
                    pupil_last[eye] = diameter;
                    pupil_seen[eye] = time;
                    if (time - seen <= .25 &&
                        std::abs(diameter - previous) <= std::max(3., previous * .16) &&
                        (profile->capabilities & 8) && diameter >= range[0] && diameter <= range[1])
                        relative[eye] = clip((diameter - small) / span);
                }
            }
            if (std::isfinite(relative[0]) || std::isfinite(relative[1])) {
                if (!std::isfinite(relative[0]))
                    relative[0] = relative[1];
                if (!std::isfinite(relative[1]))
                    relative[1] = relative[0];
                double dt = time - pupil_valid, k = !pupil_filtered || dt > 1 ? 1 : 1 - std::exp(-dt / .25);
                for (int i = 0; i < 2; i++)
                    pupil_values[i] += k * (relative[i] - pupil_values[i]);
                pupil_filtered = true;
                pupil_valid = time;
            }
        }
        if (now - pupil_time >= .125 && !pupil_job.valid()) {
            pupil_time = now;
            bool changed = previous_eyes.empty();
            previous_eyes.resize(400 * 2000);
            for (int y = 0; y < 400; y++) {
                auto old = previous_eyes.data() + y * 2000;
                auto row = strip + y * 2000;
                changed = changed || std::memcmp(row, old, 800) != 0;
                std::memcpy(old, row, 800);
            }
            if (!changed) {
                out.status |= TRACK_PUPIL_UPDATED;
                geometry.fill(0);
            } else {
                bool ranged = (profile->capabilities & 8) && !calibrating;
                std::array<double, 4> ranges{};
                for (int eye = 0; eye < 2; eye++) {
                    double small = profile->small[eye], span = profile->large[eye] - small;
                    ranges[eye * 2] = small - .35 * span;
                    ranges[eye * 2 + 1] = small + 1.35 * span;
                }
                pupil_job_time = now;
                pupil_job = std::async(std::launch::async, [this, ranged, ranges] {
                    pupil_found.fill(0);
                    for (int eye = 0; eye < 2; eye++)
                        pupil_detect(previous_eyes.data(), eye, ranged ? ranges.data() + eye * 2 : nullptr, pupil_found.data() + eye * 7);
                });
            }
        }
        if (pupil_filtered && now - pupil_valid <= 1) {
            out.status |= TRACK_PUPIL_VALID;
            for (int i = 0; i < 2; i++)
                out.pupils[i] = static_cast<float>(pupil_values[i]);
        } else
            pupil_filtered = false;
        std::copy(geometry.begin(), geometry.end(), out.geometry);
    }
};

extern "C" TrackingModel *tracking_model_open(const char *directory) {
    try {
        cv::setNumThreads(1);
        auto m = std::make_unique<TrackingModel>();
        m->reload(directory);
        return m.release();
    } catch (const std::exception &e) {
        std::fprintf(stderr, "QFT_ERROR: %s\n", e.what());
        return nullptr;
    }
}
extern "C" int tracking_model_reload(TrackingModel *m, const char *directory) {
    try {
        m->reload(directory);
        return 1;
    } catch (const std::exception &e) {
        std::fprintf(stderr, "QFT_ERROR: %s\n", e.what());
        return 0;
    }
}
extern "C" void tracking_model_control(TrackingModel *m, const TrackingControl *c, double now) {
    if (c->native_sequence != m->native_sequence) {
        m->native_sequence = c->native_sequence;
        m->native_time = now - c->native_age;
    }
    m->control = *c;
}
extern "C" int tracking_model_run(TrackingModel *m, const uint8_t *strip, const float features[999],
                                  double now, TrackingResult *out) {
    try {
        if (!(m->profile->capabilities & 1))
            m->control.flags &= ~TRACK_TONGUE;
        *out = {};
        out->version = 2;
        out->flags = m->control.flags;
        out->revision = m->control.revision;
        uint64_t t = clock_ns(), u;
        if (m->control.flags & (TRACK_EXTRA | TRACK_TONGUE)) {
            uint64_t networks = stage_ns[0];
            m->expressions(strip, features, now, *out);
            u = clock_ns(); stage_ns[1] += u - t - (stage_ns[0] - networks); t = u;
        }
        if (m->control.flags & (TRACK_PUPILS | TRACK_PUPIL_PREVIEW | TRACK_PUPIL_CALIBRATE)) {
            m->pupils(strip, now, *out);
            u = clock_ns();
            if (out->status & TRACK_PUPIL_UPDATED) { stage_ns[2] += u - t; stage_ns[3]++; } else stage_ns[4] += u - t;
        }
        return 1;
    } catch (const std::exception &e) {
        std::fprintf(stderr, "QFT_ERROR: %s\n", e.what());
        return 0;
    }
}
extern "C" void tracking_model_close(TrackingModel *m) { delete m; }
