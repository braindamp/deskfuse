#include "focus_window_gpu.h"
#include "capture_region.h"
#include "encoded_access_unit.h"
#include <algorithm>
#include <array>
#include <iostream>

int TestLatestFrameQueue();

// No monitor, HWND, adapter or driver is opened by this contract test.
int main() {
    if(fwgpu_abi_version() != 1) return 1;
    if(fwgpu_open(nullptr, 1440, 3120, 60, 20000000) != nullptr) return 2;
    wchar_t error[128]{};
    if(fwgpu_last_error(nullptr, error, 128) <= 0 || error[0] == L'\0') return 3;
    wchar_t shortError[1]{L'x'};
    if(fwgpu_last_error(nullptr, shortError, 1) != 0 || shortError[0] != L'\0') return 4;
    if(fwgpu_last_error(nullptr, nullptr, 0) <= 1) return 5;
    // Non-null synthetic HWND still MUST be rejected before touching hardware
    // when geometry, cadence or bitrate violate the ABI.
    auto fake = reinterpret_cast<void*>(1);
    if(fwgpu_open(fake, 1441, 3120, 60, 20000000)) return 6;
    if(fwgpu_open(fake, 1440, 3120, 0, 20000000)) return 7;
    if(fwgpu_open(fake, 1440, 3120, 60, 0)) return 8;
    uint32_t written=0; uint64_t qpc=0; uint8_t idr=0;
    if(fwgpu_read(nullptr, 0, nullptr, 0, &written, &qpc, &idr) != -1) return 9;
    fwgpu_force_idr(nullptr);
    fwgpu_close(nullptr);
    fwgpu_metrics metrics{};
    if (sizeof(metrics) != 64 || fwgpu_get_metrics(nullptr, &metrics, sizeof(metrics)) != -1) return 25;
    CaptureRegion region;
    // Actual Windows 10 Notepad observation at 288 DPI: WGC includes 17 px
    // invisible borders, while DWM's visible frame is exactly the phone size.
    if (!ResolveCaptureRegion(1114, 2417, 1080, 2400, 1114, 2417, 17, 0, region) ||
        region.left != 17 || region.top != 0 || region.width != 1080 || region.height != 2400) return 10;
    if (!ResolveCaptureRegion(1080, 2400, 1080, 2400, 1114, 2417, 17, 0, region) ||
        region.left != 0 || region.top != 0) return 11;
    if (!ResolveCaptureRegion(1080, 2400, 1080, 2400, 1080, 2400, 0, 0, region)) return 12;
    if (ResolveCaptureRegion(1200, 2400, 1080, 2400, 1114, 2417, 17, 0, region)) return 13;
    if (ResolveCaptureRegion(1114, 2417, 1080, 2400, 1114, 2417, -1, 0, region)) return 14;
    if (ResolveCaptureRegion(1114, 2417, 1080, 2400, 1114, 2417, 35, 0, region)) return 15;
    const std::array<uint8_t,17> encoded{0,0,0,1,0x67,0,0,1,0x68,0,0,0,1,0x65,0x11,0x22,0x33};
    if (InspectH264Headers(encoded) != (H264Idr | H264Sps | H264Pps)) return 16;
    std::array<uint8_t,20> destination{};
    destination.fill(0xcd);
    std::vector<uint8_t> retry;
    CopyEncodedAccessUnit(encoded, destination.data(), 4, retry);
    if (retry.size() != encoded.size() || destination[0] != 0xcd) return 17;
    CopyEncodedAccessUnit(encoded, destination.data(), (uint32_t)destination.size(), retry);
    if (!retry.empty() || !std::equal(encoded.begin(),encoded.end(),destination.begin()) || destination[17] != 0xcd) return 18;
    CopyEncodedAccessUnit(encoded, nullptr, 0, retry);
    if (!std::equal(encoded.begin(),encoded.end(),retry.begin(),retry.end())) return 19;
    bool classifiedIdr = false;
    if (!ClassifyH264Output(encoded, H264PictureKind::Idr, true, classifiedIdr) || !classifiedIdr) return 20;
    if (!ClassifyH264Output({}, H264PictureKind::NonIdr, false, classifiedIdr) || classifiedIdr) return 21;
    if (ClassifyH264Output({}, H264PictureKind::Idr, false, classifiedIdr)) return 22;
    if (ClassifyH264Output(encoded, H264PictureKind::NonIdr, true, classifiedIdr)) return 23;
    if (!ClassifyH264Output(encoded, H264PictureKind::Unknown, false, classifiedIdr) || !classifiedIdr) return 24;
    std::cout << "GPU ABI contract passed without hardware\n";
    return TestLatestFrameQueue();
}
