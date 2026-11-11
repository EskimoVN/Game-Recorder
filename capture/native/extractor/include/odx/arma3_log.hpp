// Arma 3 camera log written by the odx_capture extension (document/games/arma3.md §3, §5). odx-app names the log
// file in control.txt while a round records; the @OdxCapture addon passes the render camera every frame through
// callExtension, already formatted by SQF (toFixed), and the extension prefixes the QPC time of the call. The row
// format is pinned by dotnet/fixtures/arma3_camera.csv; Odx.Capture's Arma3Camera converts it to camera_samples.csv.
#pragma once

#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>

namespace odx {

extern const char* const kArma3CameraHeader;
inline constexpr std::string_view kArma3EndLine = "end";

// view, pos xyz, right xyz, up xyz, fwd xyz, fov_top, object_fov: 15 comma-separated fields on one line.
bool arma3_frame_ok(std::string_view payload);

class Arma3Log {
public:
    explicit Arma3Log(std::filesystem::path control);
    ~Arma3Log();

    // Opens the log control.txt names; ends the open one with "end" once control.txt is gone or names another.
    void poll();
    bool recording() const { return out_.is_open(); }
    void write(double t_qpc_ms, std::string_view payload);

private:
    void close();

    std::filesystem::path control_;
    std::string current_;
    std::ofstream out_;
};

}  // namespace odx
