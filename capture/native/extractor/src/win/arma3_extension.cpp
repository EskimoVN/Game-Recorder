// odx_capture_x64.dll, the Arma 3 extension the @OdxCapture addon calls once per frame (callExtension blocks the
// game's thread, so the call only stamps QPC and queues; a worker thread polls control.txt and writes the log).
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

#include "common.hpp"
#include "odx/arma3_log.hpp"
#include "present_capture.hpp"

namespace {

std::mutex mu;
std::condition_variable cv;
std::deque<std::pair<double, std::string>> queue;
std::atomic<bool> recording{false};
std::once_flag started;

// Same path as Odx.Capture's Arma3Camera.ControlPath.
std::filesystem::path control_path() {
    const wchar_t* local = _wgetenv(L"LOCALAPPDATA");
    return std::filesystem::path(local ? local : L".") / L"Odx" / L"arma3" / L"control.txt";
}

// Detached: the game unloads extensions only at exit, and a thread cannot be joined under the loader lock.
void worker() {
    odx::Arma3Log log(control_path());
    std::unique_lock lock(mu);
    while (true) {
        cv.wait_for(lock, std::chrono::milliseconds(100));
        auto batch = std::move(queue);
        queue.clear();
        lock.unlock();
        for (auto& [t, payload] : batch) log.write(t, payload);
        log.poll();
        recording = log.recording();
        lock.lock();
    }
}

void reply(char* output, unsigned int size, const char* text) { strncpy_s(output, size, text, _TRUNCATE); }

}  // namespace

extern "C" {

__declspec(dllexport) void __stdcall RVExtensionVersion(char* output, unsigned int outputSize) {
    reply(output, outputSize, "odx_capture 1");
}

__declspec(dllexport) void __stdcall RVExtension(char* output, unsigned int outputSize, const char* function) {
    double t = odx::win::qpc_ms();
    std::call_once(started, [] {
        std::thread(worker).detach();
        // Install the Present hook off the call thread (it creates a throwaway D3D device): video capture arms itself
        // once odx record's shared mapping appears. The camera log (above) is independent and works without it.
        std::thread([] {
            std::string error;
            if (!odx::win::install_present_capture(error) && !error.empty())
                OutputDebugStringA(("odx_capture present hook: " + error + "\n").c_str());
        }).detach();
    });
    reply(output, outputSize, "");
    if (!recording) return;
    if (!odx::arma3_frame_ok(function)) {
        reply(output, outputSize, "bad frame");
        return;
    }
    {
        std::lock_guard lock(mu);
        queue.emplace_back(t, function);
    }
}

}
