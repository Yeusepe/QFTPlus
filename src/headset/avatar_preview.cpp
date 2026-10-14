#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#include <android/log.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <poll.h>
#include <stdexcept>
#include <string>
#include <thread>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#include <vector>
#include <zlib.h>

static void require(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); }
#include "calibration_avatar.hpp"
#include "calibration_render.hpp"

int main(int argc, char** argv) {
    if (argc != 5 || std::strlen(argv[4]) > 80) return 2;
    try {
        int server = socket(AF_UNIX, SOCK_STREAM, 0);
        sockaddr_un address{}; address.sun_family = AF_UNIX; std::strcpy(address.sun_path + 1, argv[4]);
        require(bind(server, reinterpret_cast<sockaddr*>(&address), offsetof(sockaddr_un, sun_path) + 1 + std::strlen(argv[4])) == 0 && listen(server, 1) == 0, "Open avatar preview socket");
        fputs("QFT_AVATAR_READY\n", stdout); fflush(stdout);
        pollfd connection{server, POLLIN, 0};
        require(poll(&connection, 1, 30000) > 0, "Wait for avatar preview connection");
        int client = accept(server, nullptr, nullptr); close(server);
        require(client >= 0, "Connect avatar preview");
        timeval timeout{10, 0}; setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof timeout);
        FILE* output = fdopen(dup(client), "wb");
        require(output, "Open avatar frame stream");
        constexpr int width = 360, height = 250;
        EGLDisplay display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
        require(eglInitialize(display, nullptr, nullptr), "Initialize avatar graphics");
        EGLint attributes[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
        EGLConfig config; EGLint count = 0;
        require(eglChooseConfig(display, attributes, &config, 1, &count) && count, "Choose avatar graphics format");
        EGLint ca[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE}, pa[] = {EGL_WIDTH, width, EGL_HEIGHT, height, EGL_NONE};
        EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, ca);
        EGLSurface surface = eglCreatePbufferSurface(display, config, pa);
        require(eglMakeCurrent(display, surface, surface, context), "Open avatar graphics context");
        GLuint shader = program(avatarVertex, avatarFragment), framebuffer, color, depth;
        glUniformBlockBinding(shader, glGetUniformBlockIndex(shader, "SkinBlock"), 0);
        glGenFramebuffers(1, &framebuffer); glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
        glGenRenderbuffers(1, &color); glBindRenderbuffer(GL_RENDERBUFFER, color);
        glRenderbufferStorage(GL_RENDERBUFFER, GL_SRGB8_ALPHA8, width, height);
        glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, color);
        glGenRenderbuffers(1, &depth); glBindRenderbuffer(GL_RENDERBUFFER, depth);
        glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, width, height);
        glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, depth);
        require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "Create avatar frame");
        std::ifstream file(argv[3], std::ios::binary);
        std::vector<uint8_t> preset((std::istreambuf_iterator<char>(file)), {});
        require(!preset.empty() && preset.size() < 32 * 1024 * 1024, "Read avatar preset");
        avatar::Guide guide; guide.start(argv[1], argv[2], std::move(preset));
        auto until = std::chrono::steady_clock::now() + std::chrono::seconds(20);
        while (!guide.ready() && !guide.failed() && std::chrono::steady_clock::now() < until) {
            guide.update(1 / 60.f); std::this_thread::sleep_for(std::chrono::milliseconds(16));
        }
        require(guide.ready(), "Load avatar preview");
        uint32_t ready[] = {0x31564651, 0};
        fwrite(ready, sizeof ready, 1, output); fflush(output);
        glViewport(0, 0, width, height); glEnable(GL_DEPTH_TEST); glEnable(GL_CULL_FACE);
        glUseProgram(shader);
        auto light = avatar::normalize({.25f, .8f, .55f});
        glUniform3f(glGetUniformLocation(shader, "u_LightDir"), light.x, light.y, light.z);
        glUniform3f(glGetUniformLocation(shader, "u_CameraPos"), 0, 0, .8f);
        glUniform3f(glGetUniformLocation(shader, "u_LightColor"), 3.93f, 3.77f, 3.61f);
        glUniform3f(glGetUniformLocation(shader, "u_SkyColor"), .95f, 1.f, 1.1f);
        glUniform3f(glGetUniformLocation(shader, "u_GroundColor"), .5f, .48f, .46f);
        avatar::Mat projection{};
        projection.m[0] = 2.f / .62f; projection.m[5] = 2.f / .43f;
        projection.m[10] = -1; projection.m[15] = 1;
        glUniformMatrix4fv(glGetUniformLocation(shader, "u_ViewProj"), 1, GL_FALSE, projection.m);
        std::vector<uint8_t> pixels(width * height * 4), compressed(compressBound(pixels.size()));
        float pose[76];
        while (true) {
            size_t received = 0;
            while (received < sizeof pose) {
                pollfd input{client, POLLIN, 0};
                if (poll(&input, 1, 10000) <= 0) return 0;
                auto n = read(client, reinterpret_cast<char*>(pose) + received, sizeof pose - received);
                if (n <= 0) return 0;
                received += size_t(n);
            }
            for (float v : pose) require(std::isfinite(v) && v >= -1 && v <= 1, "Invalid avatar pose");
            std::copy(pose, pose + 72, guide.pose.weights);
            std::fill(std::begin(guide.pose.confidence), std::end(guide.pose.confidence), 1.f);
            guide.tongue = {pose[72], pose[73], pose[74]}; guide.idle = pose[75] > 0;
            guide.update(1 / 12.f);
            auto model = avatar::translation({-guide.head.x, -(guide.head.y + .055f), -guide.head.z});
            glUniformMatrix4fv(glGetUniformLocation(shader, "u_Model"), 1, GL_FALSE, model.m);
            glUniform2f(glGetUniformLocation(shader, "u_Bust"), guide.head.y - .22f, guide.head.y - .12f);
            glClearColor(0, 0, 0, 0); glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT); guide.draw(shader);
            glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pixels.data());
            require(glGetError() == GL_NO_ERROR, "Read avatar frame");
            uLongf size = compressed.size();
            require(compress2(compressed.data(), &size, pixels.data(), pixels.size(), 1) == Z_OK, "Compress avatar frame");
            uint32_t header[] = {0x31564651, uint32_t(size)};
            if (fwrite(header, sizeof header, 1, output) != 1 || fwrite(compressed.data(), size, 1, output) != 1 || fflush(output)) return 0;
        }
    } catch (const std::exception& error) { fprintf(stderr, "Avatar preview: %s\n", error.what()); return 1; }
}
