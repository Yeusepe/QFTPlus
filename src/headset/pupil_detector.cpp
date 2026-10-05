#include "pupil_detector.h"
#include "pupil_contours.hpp"
#include <algorithm>
#include <array>
#include <cmath>
#include <opencv2/core.hpp>
#include <opencv2/geometry/2d.hpp>
#include <opencv2/imgproc.hpp>
#include <unordered_set>
#include <vector>
using Sweep = FastContours;
struct ContourHash {
    size_t operator()(const std::vector<cv::Point> &c) const noexcept {
        size_t h = 1469598103934665603ULL;
        for (auto p : c) {
            h = (h ^ (unsigned)p.x) * 1099511628211ULL;
            h = (h ^ (unsigned)p.y) * 1099511628211ULL;
        }
        return h;
    }
};
struct Pupil {
    bool valid = false;
    cv::RotatedRect ellipse;
    double diameter = 0, score = 0;
};
using Hist = std::array<int, 256>;
static double quantile(const Hist &hist, int count, double q) {
    double at = (count - 1) * q;
    int a = (int)std::floor(at), b = (int)std::ceil(at), lo = 0, hi = 0, sum = 0;
    for (int v = 0; v < 256; v++) {
        sum += hist[v];
        if (sum > a) {
            lo = v;
            break;
        }
    }
    sum = 0;
    for (int v = 0; v < 256; v++) {
        sum += hist[v];
        if (sum > b) {
            hi = v;
            break;
        }
    }
    return lo + (hi - lo) * (at - a);
}
static Pupil detect(const cv::Mat &gray, const double *range, cv::Rect roi, bool simple) {
    cv::Mat smooth, unused, opened, binary;
    cv::GaussianBlur(gray, smooth, cv::Size(5, 5), 0);
    double otsu = cv::threshold(smooth, unused, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    Hist hist{};
    for (int y = 0; y < smooth.rows; y++)
        for (int x = 0; x < smooth.cols; x++)
            ++hist[smooth.ptr<unsigned char>(y)[x]];
    int first = 0;
    while (!hist[first])
        ++first;
    double low = std::min(quantile(hist, (int)smooth.total(), .02), first + 2.),
           high = quantile(hist, (int)smooth.total(), .55);
    cv::morphologyEx(smooth, opened, cv::MORPH_OPEN, cv::Mat::ones(3, 3, CV_8U));
    std::vector<int> thresholds;
    for (int i = 0; i < 28; i++)
        thresholds.push_back((int)std::nearbyint(low + (high - low) * i / 27.));
    thresholds.push_back((int)std::nearbyint(otsu));
    std::sort(thresholds.begin(), thresholds.end());
    thresholds.erase(std::unique(thresholds.begin(), thresholds.end()), thresholds.end());
    std::vector<Pupil> candidates;
    std::unordered_set<std::vector<cv::Point>, ContourHash> seen;
    std::vector<std::vector<cv::Point>> contours;
    std::vector<cv::Point> expanded;
    cv::Mat mask;
    Sweep sweep(opened(roi), roi.tl(), gray.size());
    for (int threshold : thresholds) {
        sweep.contours(threshold, contours);
        for (const auto &raw : contours) {
            if (raw.size() < (simple ? 2u : 20u))
                continue;
            auto box = cv::boundingRect(raw);
            if (box.x < 8 || box.y < 8 || box.x + box.width >= gray.cols - 8 ||
                box.y + box.height >= gray.rows - 8)
                continue;
            if (roi.width < gray.cols &&
                (box.x <= roi.x || box.y <= roi.y || box.x + box.width >= roi.br().x ||
                 box.y + box.height >= roi.br().y))
                continue;
            double area = cv::contourArea(raw);
            if (area < .90 * CV_PI * 10 * 10 / 4. - 1e-6 || area > 1.08 * CV_PI * 60 * 60 / 4. + 1e-6) {
                continue;
            }
            if (!seen.insert(raw).second)
                continue;
            const std::vector<cv::Point> *c = &raw;
            if (simple) {
                expanded.clear();
                for (size_t i = 0; i < raw.size(); i++) {
                    auto a = raw[i], b = raw[(i + 1) % raw.size()];
                    int dx = b.x - a.x, dy = b.y - a.y, n = std::max(std::abs(dx), std::abs(dy));
                    int sx = (dx > 0) - (dx < 0), sy = (dy > 0) - (dy < 0);
                    for (int j = 0; j < n; j++)
                        expanded.emplace_back(a.x + j * sx, a.y + j * sy);
                }
                c = &expanded;
            }
            if (c->size() < 20)
                continue;
            auto e = cv::fitEllipse(*c);
            double minor = std::min(e.size.width, e.size.height),
                   major = std::max(e.size.width, e.size.height);
            if (!(minor >= 10 && minor <= major && major <= 60 && minor / major >= .45))
                continue;
            double fill = area / (CV_PI * minor * major / 4.);
            if (!(fill >= .90 && fill <= 1.08))
                continue;
            int radius = (int)std::ceil(major * .65) + 3, x0 = std::max(0, (int)e.center.x - radius),
                y0 = std::max(0, (int)e.center.y - radius);
            cv::Mat patch = gray(cv::Rect(x0, y0, std::min(gray.cols, (int)e.center.x + radius + 1) - x0,
                                          std::min(gray.rows, (int)e.center.y + radius + 1) - y0));
            mask.create(patch.size(), CV_8U);
            mask.setTo(0);
            cv::Point2f center(e.center.x - x0, e.center.y - y0);
            cv::ellipse(mask,
                        cv::RotatedRect(center,
                                        cv::Size2f((float)(e.size.width * 1.3), (float)(e.size.height * 1.3)),
                                        e.angle),
                        cv::Scalar(2), -1);
            cv::ellipse(mask, cv::RotatedRect(center, e.size, e.angle), cv::Scalar(0), -1);
            cv::ellipse(mask,
                        cv::RotatedRect(center,
                                        cv::Size2f((float)(e.size.width * .85), (float)(e.size.height * .85)),
                                        e.angle),
                        cv::Scalar(1), -1);
            Hist inner{}, outer{};
            int ni = 0, no = 0;
            for (int y = 0; y < patch.rows; y++)
                for (int x = 0; x < patch.cols; x++) {
                    auto m = mask.ptr<unsigned char>(y)[x], v = patch.ptr<unsigned char>(y)[x];
                    if (m == 1) {
                        inner[v]++;
                        ni++;
                    } else if (m == 2) {
                        outer[v]++;
                        no++;
                    }
                }
            double contrast = ni && no ? quantile(outer, no, .5) - quantile(inner, ni, .5) : NAN;
            if (!std::isfinite(contrast) || contrast < 20)
                continue;
            candidates.push_back({true, e, major, contrast * fill});
        }
    }
    Pupil best;
    for (const auto &c : candidates) {
        bool nested = false;
        for (const auto &p : candidates) {
            double dx = (double)p.ellipse.center.x - c.ellipse.center.x,
                   dy = (double)p.ellipse.center.y - c.ellipse.center.y;
            if (p.diameter < c.diameter * .78 && std::hypot(dx, dy) < c.diameter * .18) {
                nested = true;
                break;
            }
        }
        if (nested || (range && (c.diameter < range[0] || c.diameter > range[1])))
            continue;
        if (!best.valid || c.score > best.score)
            best = c;
    }
    return best;
}

extern "C" int pupil_detect(const unsigned char *strip, int eye, const double *range, float out[7]) {
    try {
        cv::Mat gray(400, 400, CV_8U, const_cast<unsigned char *>(strip) + eye * 400, 2000);
        auto p = detect(gray, range, {0, 0, 400, 400}, true);
        std::fill(out, out + 7, 0.f);
        if (p.valid) {
            out[0] = 1;
            out[1] = p.ellipse.center.x;
            out[2] = p.ellipse.center.y;
            out[3] = p.ellipse.size.width;
            out[4] = p.ellipse.size.height;
            out[5] = p.ellipse.angle;
            out[6] = (float)p.diameter;
        }
        return 1;
    } catch (const cv::Exception &) {
        return 0;
    }
}
