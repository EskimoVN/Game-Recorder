// Video from Arma 3's own Present for odx record --source present (research/arma3-present-capture §2-3). While odx record's
// shared mapping for this process exists and its capture flag is set, each presented backbuffer is converted to NV12 on
// the game's GPU and copied into the mapping, and Present is paced to the capture grid so every video frame is a fresh
// game frame. Install once, off the loader lock (first callExtension). A no-op until the mapping appears, and on any error.
#pragma once

#include <string>

namespace odx::win {

bool install_present_capture(std::string& error);
void remove_present_capture();

}  // namespace odx::win
