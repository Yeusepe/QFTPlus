#pragma once
#include <arm_neon.h>
#include <opencv2/core.hpp>
#include <opencv2/geometry/2d.hpp>
#include <opencv2/imgproc.hpp>
#include <vector>
struct FastContours {
    cv::Mat image, storage;
    cv::Point offset;
    cv::Size full;
    std::vector<cv::Point> scratch;
    FastContours(const cv::Mat &src, cv::Point at, cv::Size size)
        : image(src), storage(cv::Mat::zeros(src.rows + 2, src.cols + 2, CV_8U)), offset(at), full(size) {}
    void trace(signed char *p, int step, cv::Point point) {
        int delta[16] = {1, 1 - step, -step, -1 - step, -1, step - 1, step, step + 1};
        for (int i = 0; i < 8; i++)
            delta[i + 8] = delta[i];
        static const cv::Point moves[] = {{1, 0},  {1, -1}, {0, -1}, {-1, -1},
                                          {-1, 0}, {-1, 1}, {0, 1},  {1, 1}};
        int s = 4;
        signed char *first;
        do {
            s = (s - 1) & 7;
            first = p + delta[s];
        } while (*first == 0 && s != 4);
        scratch.clear();
        if (s == 4) {
            *p = -126;
            scratch.push_back(point);
            return;
        }
        auto current = p;
        int previous = s ^ 4;
        for (;;) {
            int end = s;
            signed char *next;
            do {
                next = current + delta[++s];
            } while (!*next);
            s &= 7;
            if ((unsigned)(s - 1) < (unsigned)end)
                *current = -126;
            else if (*current == 1)
                *current = 2;
            if (s != previous) {
                scratch.push_back(point);
                previous = s;
            }
            point += moves[s];
            if (next == p && current == first)
                break;
            current = next;
            s = (s + 4) & 7;
        }
    }
    void contours(int threshold, std::vector<std::vector<cv::Point>> &out) {
        out.clear();
        auto inner = storage(cv::Rect(1, 1, image.cols, image.rows));
        cv::threshold(image, inner, threshold, 1, cv::THRESH_BINARY_INV);
        int step = (int)storage.step, w = image.cols + 1, h = image.rows + 1;
        for (int y = 1; y < h; y++) {
            auto row = reinterpret_cast<signed char *>(storage.ptr<unsigned char>(y));
            int prev = 0, last = 0;
            for (int x = 1; x < w; x++) {
                if (row[x] == prev) {
                    auto equal = vdupq_n_s8((int8_t)prev);
                    while (x + 16 <= w && vminvq_u8(vceqq_s8(vld1q_s8(row + x), equal)) == 255)
                        x += 16;
                    while (x < w && row[x] == prev)
                        x++;
                    if (x == w)
                        break;
                }
                int value = row[x];
                bool hole = false, eligible = true;
                if (!(prev == 0 && value == 1)) {
                    if (value != 0 || prev < 1)
                        eligible = false;
                    else {
                        if (prev & -2)
                            last = x - 1;
                        hole = true;
                    }
                }
                if (eligible && !hole && row[last] <= 0) {
                    last = x;
                    trace(row + x, step, offset + cv::Point(x - 1, y - 1));
                    auto b = cv::boundingRect(scratch);
                    if (b.x >= 8 && b.y >= 8 && b.br().x < full.width - 8 && b.br().y < full.height - 8 &&
                        !(image.cols < full.width &&
                          (b.x <= offset.x || b.y <= offset.y || b.br().x >= offset.x + image.cols ||
                           b.br().y >= offset.y + image.rows)) &&
                        (double)(b.width - 1) * (b.height - 1) >= .90 * CV_PI * 25. - 1e-6)
                        out.push_back(scratch);
                    prev = row[x];
                    continue;
                }
                prev = value;
                if (prev & -2)
                    last = x;
            }
        }
        std::reverse(out.begin(), out.end());
    }
};
