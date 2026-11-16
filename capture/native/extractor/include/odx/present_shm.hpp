// Cross-process, cross-language contract for Arma 3 video from the game's own Present (research/arma3-present-capture).
// odx_capture_x64.dll (in the game) writes NV12 frames here; `odx record --source present` (dotnet) reads them.
// The byte layout is pinned by a test on each side (test_core.cpp here, PresentShmTests in Odx.Tests): keep the two
// in step. odx record owns the control fields (capture, fps, origin_qpc_ms); the DLL owns the slots and the sizes.
#pragma once

#include <cstddef>
#include <cstdint>

namespace odx::present {

constexpr uint32_t kVersion = 1;
constexpr uint32_t kSlotCount = 4;

// The mapping is sized for this backbuffer up front (odx record creates it before the DLL reports the real size);
// a larger backbuffer falls back to WGC. Even dimensions keep the NV12 byte count a multiple of 8.
constexpr uint32_t kMaxWidth = 3840;
constexpr uint32_t kMaxHeight = 2160;
constexpr uint32_t kMaxNv12Bytes = kMaxWidth * kMaxHeight * 3 / 2;  // 12,441,600

// `Local\` + this + the game pid; the frame-written event is kEventPrefix + pid (auto-reset).
constexpr char kNamePrefix[] = "OdxCapture_";
constexpr char kEventPrefix[] = "OdxCaptureFrame_";

struct Header {
    uint32_t version;        // kVersion
    uint32_t capture;        // odx record sets 1 to ask for frames; the DLL captures only while set
    uint32_t fps;            // capture-grid fps, the limiter target (odx record)
    uint32_t slot_count;     // kSlotCount
    double origin_qpc_ms;    // grid origin in QPC ms (odx record)
    uint32_t width;          // backbuffer size the DLL is writing (0 until the first frame)
    uint32_t height;
    uint32_t write_seq;      // bumped by the DLL after each full slot write; the reader takes the newest
    uint32_t pad;
};

struct SlotHeader {
    uint64_t seq;            // equals the Header.write_seq this slot was written for (torn-read guard)
    double present_qpc_ms;   // QPC ms at the Present that produced this frame
};

constexpr size_t kSlotStride = sizeof(SlotHeader) + kMaxNv12Bytes;
constexpr size_t kMappingSize = sizeof(Header) + kSlotCount * kSlotStride;

// Layout is fixed across compilers and languages: no padding surprises.
static_assert(sizeof(double) == 8);
static_assert(offsetof(Header, version) == 0);
static_assert(offsetof(Header, capture) == 4);
static_assert(offsetof(Header, fps) == 8);
static_assert(offsetof(Header, slot_count) == 12);
static_assert(offsetof(Header, origin_qpc_ms) == 16);
static_assert(offsetof(Header, width) == 24);
static_assert(offsetof(Header, height) == 28);
static_assert(offsetof(Header, write_seq) == 32);
static_assert(sizeof(Header) == 40);
static_assert(offsetof(SlotHeader, seq) == 0);
static_assert(offsetof(SlotHeader, present_qpc_ms) == 8);
static_assert(sizeof(SlotHeader) == 16);
static_assert(kMaxNv12Bytes % 8 == 0);
static_assert(kMappingSize == 40 + kSlotCount * (16 + 12441600));

}  // namespace odx::present
