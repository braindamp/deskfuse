// DeskFuse: read-only Windows display-density investigation.
// Build: cl /nologo /W4 /EHsc /std:c++17 /DUNICODE /D_UNICODE
//           df-density-readonly.cpp user32.lib shcore.lib /Fe:df-density-readonly.exe
//
// This executable NEVER installs devices, changes display modes/scaling, or changes
// driver state. DisplayConfigGetDeviceInfo(-3) is an UNDOCUMENTED READ-ONLY query;
// failure is normal on builds that do not support it.
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellscalingapi.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cwchar>
#include <iostream>
#include <iomanip>
#include <string>
#include <vector>
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shcore.lib")

namespace {
constexpr std::array<int, 12> kScales{{100, 125, 150, 175, 200, 225,
                                         250, 300, 350, 400, 450, 500}};
struct DpiRangeGet {
    DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    std::int32_t minRel;
    std::int32_t curRel;
    std::int32_t maxRel;
};
static_assert(sizeof(DpiRangeGet) == sizeof(DISPLAYCONFIG_DEVICE_INFO_HEADER) + 12,
              "Unexpected DisplayConfig GET DPI query size");

struct DpiValues {
    UINT effective = 0;
    UINT raw = 0;
    UINT window = 0;
    bool windowAvailable = false;
    bool effectiveAvailable = false;
    bool rawAvailable = false;
};

struct Record {
    std::wstring gdi;
    std::wstring friendly;
    UINT32 width = 0;
    UINT32 height = 0;
    bool sourceModeValid = false;
    bool virtualAwarePath = false;
    bool dpiRangeAvailable = false;
    LONG dpiQueryResult = 0;
    std::int32_t minRel = 0;
    std::int32_t curRel = 0;
    std::int32_t maxRel = 0;
    int recommended = 0;
    int current = 0;
    int maximum = 0;
    bool interpreted = false;
    bool dpiConsistent = false;
    DpiValues dpi;
};

bool QueryActive(std::vector<DISPLAYCONFIG_PATH_INFO>& paths,
                 std::vector<DISPLAYCONFIG_MODE_INFO>& modes) {
    // Topology may change between size query and enumeration.
    constexpr UINT32 flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE;
    for (int tries = 0; tries != 6; ++tries) {
        UINT32 np = 0, nm = 0;
        LONG status = GetDisplayConfigBufferSizes(flags, &np, &nm);
        if (status != ERROR_SUCCESS) {
            std::wcerr << L"GetDisplayConfigBufferSizes failed: " << status << L"\n";
            return false;
        }
        paths.resize(np);
        modes.resize(nm);
        status = QueryDisplayConfig(flags, &np, paths.data(),
                                    &nm, modes.data(), nullptr);
        if (status == ERROR_INSUFFICIENT_BUFFER) continue;
        if (status != ERROR_SUCCESS) {
            std::wcerr << L"QueryDisplayConfig failed: " << status << L"\n";
            return false;
        }
        paths.resize(np);
        modes.resize(nm);
        return true;
    }
    std::wcerr << L"Topology changed repeatedly during read-only query.\n";
    return false;
}

struct FindMonitorCtx {
    std::wstring gdi;
    HMONITOR result = nullptr;
};
BOOL CALLBACK FindMonitor(HMONITOR monitor, HDC, LPRECT, LPARAM arg) {
    auto& query = *reinterpret_cast<FindMonitorCtx*>(arg);
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (GetMonitorInfoW(monitor, &info) && _wcsicmp(info.szDevice, query.gdi.c_str()) == 0) {
        query.result = monitor;
        return FALSE;
    }
    return TRUE;
}

// A real PMv2 HWND on the specified monitor provides the effective DPI that a
// DPI-aware Windows application would see. GetDpiForMonitor alone is not a PMv2 proof.
constexpr wchar_t kDpiWindowClass[] = L"DeskFuse.Density.ReadOnlyWindow";
UINT ReadWindowDpi(const RECT& bounds) {
    HWND hwnd = CreateWindowExW(0, kDpiWindowClass, L"", WS_POPUP,
                                bounds.left + 2, bounds.top + 2, 2, 2,
                                nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!hwnd) return 0;
    const UINT dpi = GetDpiForWindow(hwnd);
    DestroyWindow(hwnd);
    return dpi;
}

DpiValues ReadDpi(const std::wstring& gdi) {
    DpiValues result{};
    if (gdi.empty()) return result;
    FindMonitorCtx query{gdi, nullptr};
    EnumDisplayMonitors(nullptr, nullptr, FindMonitor,
                        reinterpret_cast<LPARAM>(&query));
    if (!query.result) return result;
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (GetMonitorInfoW(query.result, &mi)) {
        result.window = ReadWindowDpi(mi.rcMonitor);
        result.windowAvailable = result.window != 0;
    }
    UINT x = 0, y = 0;
    if (GetDpiForMonitor(query.result, MDT_EFFECTIVE_DPI, &x, &y) == S_OK) {
        result.effective = x;
        result.effectiveAvailable = true;
    }
    if (GetDpiForMonitor(query.result, MDT_RAW_DPI, &x, &y) == S_OK) {
        result.raw = x;
        result.rawAvailable = true;
    }
    return result;
}

int IndexOf(int percent) {
    const auto it = std::find(kScales.begin(), kScales.end(), percent);
    return it == kScales.end() ? -1 : static_cast<int>(it - kScales.begin());
}
int IndexForEffective(UINT dpi) {
    if (dpi == 0) return -1;
    // Effective DPI is integer-rounded; custom scales need not match the table.
    const double pct = 100.0 * static_cast<double>(dpi) / 96.0;
    for (int i = 0; i < static_cast<int>(kScales.size()); ++i)
        if (std::abs(pct - kScales[i]) <= 0.75) return i;
    return -1;
}
int AtIndex(int index) {
    return index >= 0 && index < static_cast<int>(kScales.size())
               ? kScales[index] : 0;
}
int MaxUnderFloor(UINT32 w, UINT32 h, int minWidth, int minHeight) {
    int best = 0;
    for (const int step : kScales) {
        if (100.0 * w / step >= minWidth && 100.0 * h / step >= minHeight)
            best = step;
    }
    return best;
}

Record ReadOne(const DISPLAYCONFIG_PATH_INFO& path,
               const std::vector<DISPLAYCONFIG_MODE_INFO>& modes) {
    Record r{};
    // Windows 10 supports virtual-mode-aware CCD paths. The union changes
    // meaning when DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE is set.
    r.virtualAwarePath = (path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE) != 0;
    const UINT32 modeIndex = r.virtualAwarePath
        ? path.sourceInfo.sourceModeInfoIdx : path.sourceInfo.modeInfoIdx;
    if (modeIndex != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && modeIndex < modes.size() &&
        modes[modeIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) {
        const auto& src = modes[modeIndex].sourceMode;
        r.width = src.width;
        r.height = src.height;
        r.sourceModeValid = true;
    }

    DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
    source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
    source.header.size = sizeof(source);
    source.header.adapterId = path.sourceInfo.adapterId;
    source.header.id = path.sourceInfo.id;
    if (DisplayConfigGetDeviceInfo(&source.header) == ERROR_SUCCESS)
        r.gdi = source.viewGdiDeviceName;

    DISPLAYCONFIG_TARGET_DEVICE_NAME target{};
    target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
    target.header.size = sizeof(target);
    target.header.adapterId = path.targetInfo.adapterId;
    target.header.id = path.targetInfo.id;
    if (DisplayConfigGetDeviceInfo(&target.header) == ERROR_SUCCESS)
        r.friendly = target.monitorFriendlyDeviceName;
    r.dpi = ReadDpi(r.gdi);

    DpiRangeGet range{};
    // UNDOCUMENTED information type (-3). Query only; there is NO SetDeviceInfo call.
    range.header.type = static_cast<DISPLAYCONFIG_DEVICE_INFO_TYPE>(-3);
    range.header.size = sizeof(range);
    range.header.adapterId = path.sourceInfo.adapterId;
    range.header.id = path.sourceInfo.id;
    r.dpiQueryResult = DisplayConfigGetDeviceInfo(&range.header);
    if (r.dpiQueryResult != ERROR_SUCCESS) return r;
    r.dpiRangeAvailable = true;
    r.minRel = range.minRel;
    r.curRel = range.curRel;
    r.maxRel = range.maxRel;

    // Interpret only if offsets fit the known Windows Settings scale table.
    // This relies on the common reverse-engineered convention: minimum=100%.
    const int recIdx = -range.minRel;
    const int curIdx = recIdx + range.curRel;
    const int maxIdx = recIdx + range.maxRel;
    if (recIdx < 0 || curIdx < 0 || maxIdx < 0 ||
        recIdx >= static_cast<int>(kScales.size()) ||
        curIdx >= static_cast<int>(kScales.size()) ||
        maxIdx >= static_cast<int>(kScales.size()) ||
        curIdx > maxIdx) return r;
    r.recommended = AtIndex(recIdx);
    r.current = AtIndex(curIdx);
    r.maximum = AtIndex(maxIdx);
    r.interpreted = true;
    r.dpiConsistent = r.dpi.windowAvailable &&
                      IndexForEffective(r.dpi.window) == curIdx;
    return r;
}

void PrintOne(const Record& r) {
    std::wcout << L"\n--- " << (r.gdi.empty() ? L"(unnamed)" : r.gdi)
               << L"   " << (r.friendly.empty() ? L"(no friendly name)" : r.friendly)
               << L" ---\n";
    std::wcout << L"CCD virtual-mode-aware path: " << (r.virtualAwarePath ? L"yes" : L"no") << L"\n";
    if (r.sourceModeValid)
        std::wcout << L"Source: " << r.width << L"x" << r.height << L" physical px\n";
    else std::wcout << L"Source: unavailable (no SOURCE mode in active CCD path)\n";
    std::wcout << L"Actual PMv2 probe HWND DPI (GetDpiForWindow): ";
    if (r.dpi.windowAvailable) std::wcout << r.dpi.window;
    else std::wcout << L"unavailable";
    std::wcout << L"\n";
    std::wcout << L"GetDpiForMonitor EFFECTIVE (secondary reading): ";
    if (r.dpi.effectiveAvailable) std::wcout << r.dpi.effective;
    else std::wcout << L"unavailable";
    std::wcout << L"; raw DPI: ";
    if (r.dpi.rawAvailable) std::wcout << r.dpi.raw;
    else std::wcout << L"unavailable";
    std::wcout << L" (secondary only; PMv2 is above)\n";
    if (!r.dpiRangeAvailable) {
        std::wcout << L"Undocumented DPI range GET (-3): unavailable (Win32 "
                   << r.dpiQueryResult << L")\n";
        return;
    }
    std::wcout << L"Raw DPI range offsets (min/current/max): "
               << r.minRel << L" / " << r.curRel << L" / " << r.maxRel << L"\n";
    if (!r.interpreted) {
        std::wcout << L"Scale-table interpretation: INVALID/UNKNOWN (do not infer max)\n";
        return;
    }
    std::wcout << L"Interpreted scales (recommended/current/max): "
               << r.recommended << L"% / " << r.current << L"% / " << r.maximum
               << L"%  [PMv2 HWND cross-check: "
               << (r.dpiConsistent ? L"consistent" : L"UNCONFIRMED/INCONSISTENT")
               << L"]\n";
    if (!r.sourceModeValid) return;
    std::wcout << std::fixed << std::setprecision(1);
    std::wcout << L"H1 (640x480 DIP floor) predicts max: "
               << MaxUnderFloor(r.width, r.height, 640, 480) << L"%\n";
    std::wcout << L"Alternative (800x600 DIP floor) predicts max: "
               << MaxUnderFloor(r.width, r.height, 800, 600) << L"%\n";
    const int recIdx = IndexOf(r.recommended);
    std::wcout << L"H4 (+5 scale-table steps from recommended) predicts max: "
               << AtIndex(std::min(recIdx + 5, static_cast<int>(kScales.size()) - 1))
               << L"%\n";
    std::wcout << L"Max actual effective dimensions: "
               << r.width * 100.0 / r.maximum << L"x"
               << r.height * 100.0 / r.maximum << L" DIPs\n";
}
}  // namespace

int wmain() {
    // Affects this diagnostic process ONLY; no system-level or monitor DPI change.
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
        std::wcerr << L"WARNING: PMv2 context unavailable (process already configured?); "
                   << L"effective-DPI cross-check may be virtualized.\n";
    }
    std::wcout << L"DeskFuse READ-ONLY density comparison. No driver installs, display SET, "
               << L"registry writes, or mode changes.\n";
    std::wcout << L"DPI range GET (-3) is undocumented; predictions are hypotheses, not OS proof.\n";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kDpiWindowClass;
    if (!RegisterClassExW(&wc)) {
        std::wcerr << L"Could not register PMv2 probe window (Win32 " << GetLastError() << L").\n";
        return 2;
    }
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    if (!QueryActive(paths, modes)) return 2;
    std::vector<Record> result;
    for (const auto& path : paths) result.push_back(ReadOne(path, modes));
    for (const auto& r : result) PrintOne(r);
    int valid = 0, h1Fits = 0, h4Fits = 0;
    for (const auto& r : result) {
        if (!r.interpreted || !r.sourceModeValid || !r.dpiConsistent) continue;
        ++valid;
        if (r.maximum == MaxUnderFloor(r.width, r.height, 640, 480)) ++h1Fits;
        if (r.maximum == AtIndex(std::min(IndexOf(r.recommended) + 5,
                                            static_cast<int>(kScales.size()) - 1))) ++h4Fits;
    }
    std::wcout << L"\n=== COMPARISON (not a proof of either model) ===\n";
    std::wcout << L"Usable readings: " << valid << L" / " << result.size()
               << L". H1(640x480) fits " << h1Fits << L"; H4(+5) fits "
               << h4Fits << L".\n";
    std::wcout << L"Do NOT choose a new EDID/mode based only on a coincidental fit. "
               << L"Compare multiple resolutions or run an isolated driver experiment.\n";
    // An unavailable undocumented query is still a useful measurement; no writes.
    UnregisterClassW(kDpiWindowClass, GetModuleHandleW(nullptr));
    return 0;
}
