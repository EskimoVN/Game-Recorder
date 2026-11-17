#include "present_capture.hpp"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <MinHook.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>

#include "common.hpp"
#include "odx/present_shm.hpp"

namespace odx::win {

namespace {

using namespace odx::present;

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

PresentFn g_present = nullptr;
ResizeFn g_resize = nullptr;
void* g_present_target = nullptr;
void* g_resize_target = nullptr;

// odx record's mapping for this process (it creates it; we open it, write the slots, bump write_seq, signal the event).
HANDLE g_map = nullptr;
char* g_view = nullptr;
HANDLE g_frame_event = nullptr;
HANDLE g_timer = nullptr;
double g_last_map_try = 0;

template <class T>
void release(T*& p) {
    if (p) { p->Release(); p = nullptr; }
}

// The GPU pipeline that turns the backbuffer into NV12. Recreated when the device or the backbuffer size changes.
struct Pipeline {
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D11VideoDevice* vdev = nullptr;
    ID3D11VideoContext* vctx = nullptr;
    ID3D11VideoProcessorEnumerator* venum = nullptr;
    ID3D11VideoProcessor* vp = nullptr;
    ID3D11VideoProcessorOutputView* oview = nullptr;
    ID3D11Texture2D* nv12 = nullptr;      // VideoProcessorBlt output (GPU)
    ID3D11Texture2D* resolve = nullptr;   // MSAA resolve target, when the backbuffer is multisampled
    ID3D11Texture2D* staging[3] = {};     // CPU-readable ring; the oldest is mapped so the copy has finished
    double ring_qpc[3] = {};
    int cur = 0;
    UINT w = 0, h = 0;                    // even output dimensions (NV12 needs even)
    bool ok = false;

    void reset() {
        for (auto& s : staging) release(s);
        release(oview);
        release(nv12);
        release(resolve);
        release(vp);
        release(venum);
        release(vctx);
        release(vdev);
        release(ctx);
        release(device);
        cur = 0;
        w = h = 0;
        ok = false;
    }
};
Pipeline g_pipe;

bool open_mapping(const Header*& header) {
    if (!g_view) {
        std::string name = std::string("Local\\") + kNamePrefix + std::to_string(GetCurrentProcessId());
        g_map = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
        if (!g_map) return false;
        g_view = static_cast<char*>(MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, kMappingSize));
        if (!g_view) {
            CloseHandle(g_map);
            g_map = nullptr;
            return false;
        }
        std::string ev = std::string("Local\\") + kEventPrefix + std::to_string(GetCurrentProcessId());
        g_frame_event = OpenEventA(EVENT_MODIFY_STATE, FALSE, ev.c_str());
    }
    header = reinterpret_cast<const Header*>(g_view);
    return header->version == kVersion;
}

bool ensure_pipeline(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& bb) {
    UINT w = bb.Width & ~1u, h = bb.Height & ~1u;
    if (g_pipe.ok && g_pipe.device == device && g_pipe.w == w && g_pipe.h == h) return true;
    g_pipe.reset();
    if (w == 0 || h == 0) return false;

    device->AddRef();
    g_pipe.device = device;
    device->GetImmediateContext(&g_pipe.ctx);
    if (FAILED(device->QueryInterface(__uuidof(ID3D11VideoDevice), reinterpret_cast<void**>(&g_pipe.vdev))) ||
        FAILED(g_pipe.ctx->QueryInterface(__uuidof(ID3D11VideoContext), reinterpret_cast<void**>(&g_pipe.vctx)))) {
        g_pipe.reset();
        return false;
    }

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};
    cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    cd.InputWidth = w;
    cd.InputHeight = h;
    cd.OutputWidth = w;
    cd.OutputHeight = h;
    cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    if (FAILED(g_pipe.vdev->CreateVideoProcessorEnumerator(&cd, &g_pipe.venum)) ||
        FAILED(g_pipe.vdev->CreateVideoProcessor(g_pipe.venum, 0, &g_pipe.vp))) {
        g_pipe.reset();
        return false;
    }
    // Full-range RGB in, limited-range BT.601 YCbCr out, matching Odx.Recorder/Nv12Converter (tune after a live round).
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE in{};
    in.RGB_Range = 0;  // 0-255
    g_pipe.vctx->VideoProcessorSetStreamColorSpace(g_pipe.vp, 0, &in);
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE out{};
    out.YCbCr_Matrix = 0;   // BT.601
    out.Nominal_Range = 0;  // 16-235
    g_pipe.vctx->VideoProcessorSetOutputColorSpace(g_pipe.vp, &out);

    D3D11_TEXTURE2D_DESC td{};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_NV12;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET;
    if (FAILED(device->CreateTexture2D(&td, nullptr, &g_pipe.nv12))) {
        g_pipe.reset();
        return false;
    }
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
    ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    if (FAILED(g_pipe.vdev->CreateVideoProcessorOutputView(g_pipe.nv12, g_pipe.venum, &ovd, &g_pipe.oview))) {
        g_pipe.reset();
        return false;
    }
    D3D11_TEXTURE2D_DESC sd = td;
    sd.BindFlags = 0;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    for (auto& s : g_pipe.staging)
        if (FAILED(device->CreateTexture2D(&sd, nullptr, &s))) {
            g_pipe.reset();
            return false;
        }

    if (bb.SampleDesc.Count > 1) {
        D3D11_TEXTURE2D_DESC rd{};
        rd.Width = bb.Width;
        rd.Height = bb.Height;
        rd.MipLevels = 1;
        rd.ArraySize = 1;
        rd.Format = bb.Format;
        rd.SampleDesc.Count = 1;
        rd.Usage = D3D11_USAGE_DEFAULT;
        if (FAILED(device->CreateTexture2D(&rd, nullptr, &g_pipe.resolve))) {
            g_pipe.reset();
            return false;
        }
    }
    g_pipe.w = w;
    g_pipe.h = h;
    g_pipe.ok = true;
    return true;
}

// Converts the backbuffer to NV12 and publishes the oldest ready ring frame into the shared slot. Never blocks the GPU.
void capture_frame(IDXGISwapChain* swapchain, double present_qpc) {
    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(swapchain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backbuffer)))) return;
    ID3D11Device* device = nullptr;
    backbuffer->GetDevice(&device);
    D3D11_TEXTURE2D_DESC bb{};
    backbuffer->GetDesc(&bb);
    if (!ensure_pipeline(device, bb)) {
        release(device);
        release(backbuffer);
        return;
    }

    ID3D11Texture2D* input = backbuffer;
    if (g_pipe.resolve) {
        g_pipe.ctx->ResolveSubresource(g_pipe.resolve, 0, backbuffer, 0, bb.Format);
        input = g_pipe.resolve;
    }
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ID3D11VideoProcessorInputView* iview = nullptr;
    if (SUCCEEDED(g_pipe.vdev->CreateVideoProcessorInputView(input, g_pipe.venum, &ivd, &iview))) {
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = iview;
        if (SUCCEEDED(g_pipe.vctx->VideoProcessorBlt(g_pipe.vp, g_pipe.oview, 0, 1, &stream))) {
            g_pipe.ctx->CopyResource(g_pipe.staging[g_pipe.cur], g_pipe.nv12);
            g_pipe.ring_qpc[g_pipe.cur] = present_qpc;
        }
        release(iview);
    }

    // Map the oldest ring frame (its copy finished two Presents ago): DO_NOT_WAIT, so a still-busy one is skipped.
    int oldest = (g_pipe.cur + 1) % 3;
    if (g_pipe.ring_qpc[oldest] > 0) {
        D3D11_MAPPED_SUBRESOURCE m{};
        HRESULT hr = g_pipe.ctx->Map(g_pipe.staging[oldest], 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
        if (SUCCEEDED(hr)) {
            const Header* chk = reinterpret_cast<const Header*>(g_view);
            if (chk->capture) {
                Header* header = reinterpret_cast<Header*>(g_view);
                uint32_t seq = header->write_seq + 1;
                char* slot = g_view + sizeof(Header) + (static_cast<size_t>((seq - 1) % kSlotCount)) * kSlotStride;
                SlotHeader* sh = reinterpret_cast<SlotHeader*>(slot);
                char* payload = slot + sizeof(SlotHeader);
                UINT rows = g_pipe.h * 3 / 2;  // NV12: Y (h rows) then UV (h/2 rows), each w bytes
                const char* src = static_cast<const char*>(m.pData);
                for (UINT r = 0; r < rows; ++r) std::memcpy(payload + r * g_pipe.w, src + r * m.RowPitch, g_pipe.w);
                sh->present_qpc_ms = g_pipe.ring_qpc[oldest];
                header->width = g_pipe.w;
                header->height = g_pipe.h;
                sh->seq = seq;                 // slot complete
                header->write_seq = seq;       // publish
                if (g_frame_event) SetEvent(g_frame_event);
            }
            g_pipe.ctx->Unmap(g_pipe.staging[oldest], 0);
        }
    }
    g_pipe.cur = (g_pipe.cur + 1) % 3;
    release(device);
    release(backbuffer);
}

// Paces the caller to the next grid tick (origin + k * period) so Present lands on the capture grid.
void limit(const Header* header, double now) {
    if (header->fps == 0) return;
    double period = 1000.0 / header->fps;
    double k = std::ceil((now - header->origin_qpc_ms) / period);
    double target = header->origin_qpc_ms + k * period;
    double remain = target - now;
    if (remain <= 0 || remain > 1000) return;  // no wait, or an implausible gap (clock reset): don't stall the game
    if (g_timer && remain > 0.6) {
        LARGE_INTEGER due;
        due.QuadPart = -static_cast<LONGLONG>((remain - 0.4) * 10000.0);  // 100 ns units, relative
        if (SetWaitableTimer(g_timer, &due, 0, nullptr, nullptr, FALSE))
            WaitForSingleObject(g_timer, static_cast<DWORD>(remain) + 2);
    }
    while (qpc_ms() < target) YieldProcessor();
}

HRESULT STDMETHODCALLTYPE hooked_present(IDXGISwapChain* swapchain, UINT sync_interval, UINT flags) {
    double now = qpc_ms();
    const Header* header = nullptr;
    bool capturing = false;
    if (g_view || now - g_last_map_try > 1000) {  // retry the open at most once a second until odx record creates it
        g_last_map_try = now;
        capturing = open_mapping(header) && header->capture != 0;
    } else if (g_view) {
        capturing = open_mapping(header) && header->capture != 0;
    }
    if (capturing) {
        try {
            capture_frame(swapchain, now);
            limit(header, now);
        } catch (...) {
            // Capture must never take down the game's render thread.
        }
        return g_present(swapchain, 0, flags);  // no vsync while pacing to the grid ourselves
    }
    if (g_pipe.ok) g_pipe.reset();  // capture stopped: let go of the GPU resources
    return g_present(swapchain, sync_interval, flags);
}

HRESULT STDMETHODCALLTYPE hooked_resize(IDXGISwapChain* swapchain, UINT count, UINT w, UINT h, DXGI_FORMAT fmt, UINT flags) {
    g_pipe.reset();  // the backbuffer is about to change size: drop the textures, rebuilt on the next Present
    return g_resize(swapchain, count, w, h, fmt, flags);
}

// A throwaway device + swapchain to read Present (vtable 8) and ResizeBuffers (13) from IDXGISwapChain's vtable.
bool find_targets(std::string& error) {
    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"odx_capture_dummy";
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);

    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 1;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    bool found = false;
    for (D3D_DRIVER_TYPE type : {D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP}) {
        HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, type, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &sd, &sc, &dev, nullptr, &ctx);
        if (SUCCEEDED(hr)) {
            void** vtable = *reinterpret_cast<void***>(sc);
            g_present_target = vtable[8];
            g_resize_target = vtable[13];
            found = true;
            break;
        }
        error = "D3D11CreateDeviceAndSwapChain failed: 0x" + std::to_string(static_cast<unsigned long>(hr));
    }
    release(sc);
    release(ctx);
    release(dev);
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return found;
}

}  // namespace

bool install_present_capture(std::string& error) {
    if (!find_targets(error)) return false;
    MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
        error = std::string("MH_Initialize: ") + MH_StatusToString(st);
        return false;
    }
    if (MH_CreateHook(g_present_target, reinterpret_cast<void*>(&hooked_present), reinterpret_cast<void**>(&g_present)) != MH_OK ||
        MH_CreateHook(g_resize_target, reinterpret_cast<void*>(&hooked_resize), reinterpret_cast<void**>(&g_resize)) != MH_OK) {
        error = "MH_CreateHook failed";
        return false;
    }
    g_timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (MH_EnableHook(g_present_target) != MH_OK || MH_EnableHook(g_resize_target) != MH_OK) {
        error = "MH_EnableHook failed";
        return false;
    }
    return true;
}

void remove_present_capture() {
    if (g_present_target) {
        MH_DisableHook(g_present_target);
        MH_RemoveHook(g_present_target);
    }
    if (g_resize_target) {
        MH_DisableHook(g_resize_target);
        MH_RemoveHook(g_resize_target);
    }
    g_pipe.reset();
    if (g_view) UnmapViewOfFile(g_view);
    if (g_map) CloseHandle(g_map);
    if (g_frame_event) CloseHandle(g_frame_event);
    if (g_timer) CloseHandle(g_timer);
    g_view = nullptr;
    g_map = nullptr;
    g_frame_event = nullptr;
    g_timer = nullptr;
}

}  // namespace odx::win
