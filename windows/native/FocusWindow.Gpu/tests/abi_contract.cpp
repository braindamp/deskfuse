#include "focus_window_gpu.h"
#include <iostream>

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
    std::cout << "GPU ABI contract passed without hardware\n";
    return 0;
}
