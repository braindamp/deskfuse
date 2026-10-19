#include "common.h"
#include "test_window.h"
#include "capture.h"
#include "nvenc_encoder.h"
#include "json_util.h"

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>

#include <d3d10.h>
#include <d3d11.h>
#include <dxgi1_2.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

namespace
{
constexpr int kWindowWidth = 1440;
constexpr int kWindowHeight = 3120;
constexpr int kExpectedDpi = 336;
constexpr int kMonitorWidth = 2100;
constexpr int kMonitorHeight = 3120;

struct Options
{
    bool plan = false;
    bool run = false;
    bool encoderSelfTest = false;
    bool help = false;
    bool animate = true;
    bool saveFullReference = false;
    std::wstring monitorKv;
    std::wstring outDir;
    std::wstring encoder = L"nvenc";
    std::wstring gitSha;
    int durationSec = 20;
    int fps = 60;
    int bitrate = 20000000;
};

struct RulerResult
{
    bool pass = false;
    int width = 0;
    int height = 0;
    uint64_t greenPixels = 0;
    int greenLeft = -1;
    int greenTop = -1;
    int greenRight = -1;
    int greenBottom = -1;
    int checkerMismatches = 0;
    int checkerSamples = 0;
    int lineWhitePixels = 0;
    int colorProbeMismatches = 0;
    int colorProbeSamples = 0;
    bool backgroundOk = false;
    uint8_t corners[4][4]{};
    std::string detail;
};

struct FrameRow
{
    uint32_t frame = 0;
    int64_t arrivalQpc = 0;
    double arrivalMs = 0;
    int64_t systemRelativeTime = 0;
    double gpuCopyMs = 0;
    double captureToSubmitMs = 0;
    double submitMs = 0;
    double lockMs = 0;
    double totalMs = 0;
    uint32_t bitstreamBytes = 0;
};

struct BitstreamCheck
{
    bool exists = false;
    uint64_t size = 0;
    bool annexB = false;
    bool sps = false;
    bool pps = false;
    bool idr = false;
    std::string firstBytes;
};

std::wstring Join(const std::wstring& directory, const std::wstring& name)
{
    if (directory.empty()) return name;
    if (directory.back() == L'\\' || directory.back() == L'/') return directory + name;
    return directory + L"\\" + name;
}

void Print(const std::wstring& text)
{
    std::cout << Utf8(text);
}

void PrintLine(const std::wstring& text)
{
    std::cout << Utf8(text) << "\n" << std::flush;
}

std::wstring WinRtErrorText(const winrt::hresult_error& error)
{
    return L"0x" + Widen(FormatHresult(error.code())) + L" (" +
        std::wstring(error.message().c_str()) + L")";
}

bool IsElevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    TOKEN_ELEVATION elevation{};
    DWORD size = sizeof(elevation);
    const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, size, &size);
    CloseHandle(token);
    return ok && elevation.TokenIsElevated != 0;
}

const char* DpiAwarenessName(DPI_AWARENESS_CONTEXT context)
{
    if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        return "PerMonitorV2";
    if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE))
        return "PerMonitor";
    if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE))
        return "System";
    if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_UNAWARE))
        return "Unaware";
    return "Unknown";
}

const char* FeatureLevelName(D3D_FEATURE_LEVEL level)
{
    switch (level)
    {
    case D3D_FEATURE_LEVEL_11_1: return "11.1";
    case D3D_FEATURE_LEVEL_11_0: return "11.0";
    case D3D_FEATURE_LEVEL_10_1: return "10.1";
    case D3D_FEATURE_LEVEL_10_0: return "10.0";
    case D3D_FEATURE_LEVEL_9_3: return "9.3";
    case D3D_FEATURE_LEVEL_9_2: return "9.2";
    case D3D_FEATURE_LEVEL_9_1: return "9.1";
    default: return "Unknown";
    }
}

Json PercentileJson(const Percentiles& value)
{
    return Json{
        {"count", value.count},
        {"minMs", value.min},
        {"p50Ms", value.p50},
        {"p95Ms", value.p95},
        {"p99Ms", value.p99},
        {"maxMs", value.max},
        {"avgMs", value.avg},
    };
}

std::string HexBytes(const uint8_t* data, size_t size)
{
    std::ostringstream text;
    text << std::hex << std::setfill('0');
    for (size_t i = 0; i < size; ++i)
        text << std::setw(2) << static_cast<unsigned>(data[i]);
    return text.str();
}

bool WriteBgraBmp(const std::wstring& path, const std::vector<uint8_t>& bgra, int width, int height,
    int cropX, int cropY, int cropWidth, int cropHeight, std::wstring& error)
{
    if (width <= 0 || height <= 0 || cropX < 0 || cropY < 0 || cropWidth <= 0 || cropHeight <= 0 ||
        cropX + cropWidth > width || cropY + cropHeight > height ||
        bgra.size() < static_cast<size_t>(width) * static_cast<size_t>(height) * 4)
    {
        error = L"BMP crop is outside the captured frame.";
        return false;
    }

    const uint32_t rowBytes = static_cast<uint32_t>(cropWidth) * 4;
    BITMAPFILEHEADER fileHeader{};
    fileHeader.bfType = 0x4D42;
    fileHeader.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    fileHeader.bfSize = fileHeader.bfOffBits + rowBytes * static_cast<uint32_t>(cropHeight);
    BITMAPINFOHEADER infoHeader{};
    infoHeader.biSize = sizeof(BITMAPINFOHEADER);
    infoHeader.biWidth = cropWidth;
    infoHeader.biHeight = cropHeight; // bottom-up
    infoHeader.biPlanes = 1;
    infoHeader.biBitCount = 32;
    infoHeader.biCompression = BI_RGB;
    infoHeader.biSizeImage = rowBytes * static_cast<uint32_t>(cropHeight);

    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out)
    {
        error = L"Could not open the BMP output: " + path;
        return false;
    }
    out.write(reinterpret_cast<const char*>(&fileHeader), sizeof(fileHeader));
    out.write(reinterpret_cast<const char*>(&infoHeader), sizeof(infoHeader));
    const size_t stride = static_cast<size_t>(width) * 4;
    for (int y = cropY + cropHeight - 1; y >= cropY; --y)
    {
        const uint8_t* row = bgra.data() + static_cast<size_t>(y) * stride +
            static_cast<size_t>(cropX) * 4;
        out.write(reinterpret_cast<const char*>(row), rowBytes);
    }
    if (!out)
    {
        error = L"Writing the BMP failed: " + path;
        return false;
    }
    return true;
}

RulerResult AnalyzeRuler(const std::vector<uint8_t>& bgra, int width, int height)
{
    RulerResult result;
    result.width = width;
    result.height = height;
    if (width != kWindowWidth || height != kWindowHeight)
    {
        result.detail = "Captured frame is " + std::to_string(width) + "x" +
            std::to_string(height) + ", expected exactly 1440x3120.";
        return result;
    }
    if (bgra.size() < static_cast<size_t>(width) * static_cast<size_t>(height) * 4)
    {
        result.detail = "Captured frame buffer is smaller than the expected window.";
        return result;
    }

    auto pixel = [&](int x, int y) -> const uint8_t* {
        return bgra.data() + (static_cast<size_t>(y) * static_cast<size_t>(width) +
            static_cast<size_t>(x)) * 4;
    };

    int minX = kRulerOriginX;
    int minY = kRulerOriginY;
    int maxX = kRulerOriginX + kRulerPx - 1;
    int maxY = kRulerOriginY + kRulerPx - 1;
    for (int y = kRulerOriginY; y < kRulerOriginY + kRulerPx; ++y)
    {
        for (int x = kRulerOriginX; x < kRulerOriginX + kRulerPx; ++x)
        {
            const uint8_t* p = pixel(x, y);
            // BGRA; the exact GDI green is (0, 255, 0). Scan only the ruler
            // rectangle so the green color probe cannot inflate this count.
            if (p[1] >= 245 && p[0] <= 10 && p[2] <= 10)
            {
                ++result.greenPixels;
            }
        }
    }
    result.greenLeft = minX;
    result.greenTop = minY;
    result.greenRight = maxX;
    result.greenBottom = maxY;

    for (int y = 0; y < kCheckerSize; ++y)
    {
        for (int x = 0; x < kCheckerSize; ++x)
        {
            const uint8_t* p = pixel(kCheckerX + x, kCheckerY + y);
            const int expected = ((x + y) & 1) ? 255 : 0;
            ++result.checkerSamples;
            if (std::abs(static_cast<int>(p[0]) - expected) > 8 ||
                std::abs(static_cast<int>(p[1]) - expected) > 8 ||
                std::abs(static_cast<int>(p[2]) - expected) > 8)
            {
                ++result.checkerMismatches;
            }
        }
    }

    for (int i = 0; i < kColorProbeCount; ++i)
    {
        const RgbProbe& expected = kColorProbes[i];
        for (int y = 0; y < kColorProbeSize; ++y)
        {
            for (int x = 0; x < kColorProbeSize; ++x)
            {
                const uint8_t* p = pixel(kColorProbeX + i * kColorProbeSize + x,
                    kColorProbeY + y);
                ++result.colorProbeSamples;
                if (std::abs(static_cast<int>(p[0]) - expected.b) > 2 ||
                    std::abs(static_cast<int>(p[1]) - expected.g) > 2 ||
                    std::abs(static_cast<int>(p[2]) - expected.r) > 2)
                {
                    ++result.colorProbeMismatches;
                }
            }
        }
    }

    for (int y = kLineY; y < kLineY + kLineHeight; ++y)
    {
        const uint8_t* p = pixel(kLineX, y);
        if (p[0] >= 240 && p[1] >= 240 && p[2] >= 240) ++result.lineWhitePixels;
    }

    const int margin = 20;
    const uint8_t* corners[] = {
        pixel(margin, margin),
        pixel(width - margin - 1, margin),
        pixel(margin, height - margin - 1),
        pixel(width - margin - 1, height - margin - 1),
    };
    for (int i = 0; i < 4; ++i)
        std::copy(corners[i], corners[i] + 4, result.corners[i]);
    result.backgroundOk = std::all_of(std::begin(corners), std::end(corners), [](const uint8_t* p) {
        return p[0] >= 240 && p[2] >= 240 && p[1] <= 16; // magenta
    });

    const uint64_t expectedGreen = static_cast<uint64_t>(kRulerPx) * static_cast<uint64_t>(kRulerPx);
    const bool greenExact = result.greenPixels == expectedGreen &&
        result.greenLeft == kRulerOriginX && result.greenTop == kRulerOriginY &&
        result.greenRight == kRulerOriginX + kRulerPx - 1 &&
        result.greenBottom == kRulerOriginY + kRulerPx - 1;
    result.pass = greenExact && result.checkerMismatches <= 64 && result.lineWhitePixels == kLineHeight &&
        result.colorProbeMismatches == 0 && result.backgroundOk;

    std::ostringstream detail;
    detail << "greenPixels=" << result.greenPixels << " expected=" << expectedGreen
           << " bbox=" << result.greenLeft << "," << result.greenTop << ".."
           << result.greenRight << "," << result.greenBottom
           << " checkerMismatches=" << result.checkerMismatches << "/" << result.checkerSamples
           << " lineWhite=" << result.lineWhitePixels << "/" << kLineHeight
           << " colorProbeMismatches=" << result.colorProbeMismatches << "/" << result.colorProbeSamples
           << " background=" << (result.backgroundOk ? "magenta" : "unexpected")
           << " corners=" << HexBytes(reinterpret_cast<const uint8_t*>(result.corners), sizeof(result.corners));
    result.detail = detail.str();
    return result;
}

BitstreamCheck InspectBitstream(const std::wstring& path)
{
    BitstreamCheck check;
    std::ifstream in(path, std::ios::binary);
    if (!in) return check;
    in.seekg(0, std::ios::end);
    const auto end = in.tellg();
    if (end <= 0) return check;
    check.exists = true;
    check.size = static_cast<uint64_t>(static_cast<std::streamoff>(end));
    in.seekg(0, std::ios::beg);
    std::vector<uint8_t> data(std::min<uint64_t>(check.size, 1u << 16));
    in.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(data.size()));
    for (size_t i = 0; i + 3 < data.size(); ++i)
    {
        size_t startCode = 0;
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            startCode = 3;
        else if (i + 3 < data.size() && data[i] == 0 && data[i + 1] == 0 &&
            data[i + 2] == 0 && data[i + 3] == 1)
            startCode = 4;
        if (startCode == 0 || i + startCode >= data.size()) continue;
        check.annexB = true;
        const int nalType = data[i + startCode] & 0x1F;
        if (nalType == 7) check.sps = true;
        else if (nalType == 8) check.pps = true;
        else if (nalType == 5) check.idr = true;
        i += startCode;
    }
    check.firstBytes = HexBytes(data.data(), std::min<size_t>(data.size(), 32));
    return check;
}

bool WriteCsv(const std::wstring& path, const std::vector<FrameRow>& rows, std::wstring& error)
{
    std::ostringstream csv;
    csv << "frame,arrival_qpc,arrival_ms,system_relative_time,gpu_copy_ms,"
        << "capture_to_submit_ms,submit_ms,lock_ms,total_ms,bitstream_bytes\n";
    csv << std::fixed << std::setprecision(3);
    for (const auto& row : rows)
    {
        csv << row.frame << "," << row.arrivalQpc << "," << row.arrivalMs << ","
            << row.systemRelativeTime << "," << row.gpuCopyMs << ","
            << row.captureToSubmitMs << "," << row.submitMs << "," << row.lockMs << ","
            << row.totalMs << "," << row.bitstreamBytes << "\n";
    }
    return WriteTextFile(path, csv.str(), error);
}

bool WriteResult(const std::wstring& outDir, const Json& result, std::wstring& error)
{
    return WriteTextFile(Join(outDir, L"gpu-poc-result.json"), DumpJson(result), error);
}

int Fail(Json& result, const std::wstring& outDir, const std::string& code,
    const std::wstring& detail, int exitCode)
{
    result["failure"] = Json{ {"code", code}, {"detail", Utf8(detail)} };
    result["overall"] = "BLOCKED";
    result["finishedUtc"] = IsoNowUtc();
    std::wstring writeError;
    if (!WriteResult(outDir, result, writeError))
        PrintLine(L"POC-WARN: could not write the failure result: " + writeError);
    std::cerr << "POC-FAIL " << code << ": " << Utf8(detail) << "\n";
    return exitCode;
}

void AddVerdict(Json& result, const std::string& verdict)
{
    result["verdicts"].push_back(verdict);
}

std::string GetKv(const std::map<std::string, std::string>& kv, const char* key)
{
    const auto it = kv.find(key);
    return it == kv.end() ? std::string{} : it->second;
}

bool GetKvInt(const std::map<std::string, std::string>& kv, const char* key, int& value)
{
    const auto it = kv.find(key);
    if (it == kv.end()) return false;
    try
    {
        value = std::stoi(it->second);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

Json MonitorJson(const std::map<std::string, std::string>& kv)
{
    auto intValue = [&](const char* key) -> int {
        int value = 0;
        GetKvInt(kv, key, value);
        return value;
    };
    Json monitor{
        {"schema", GetKv(kv, "schema")},
        {"gdiName", GetKv(kv, "gdi_name")},
        {"left", intValue("left")},
        {"top", intValue("top")},
        {"width", intValue("width")},
        {"height", intValue("height")},
        {"refreshHz", intValue("refresh_hz")},
        {"stableSerialHex", GetKv(kv, "serial_hex")},
        {"scale", Json{
            {"currentBeforePercent", intValue("current_scale_before")},
            {"currentPercent", intValue("current_scale")},
            {"desiredPercent", intValue("desired_scale")},
            {"recommendedPercent", intValue("recommended_scale")},
            {"minPercent", intValue("min_scale")},
            {"maxPercent", intValue("max_scale")},
            {"source", GetKv(kv, "scale_source")},
            {"reapplied", GetKv(kv, "scale_reapplied") == "true"},
            {"simulatedResetApplied", GetKv(kv, "simulated_reset_applied") == "true"},
            {"initial100Observed", GetKv(kv, "initial_100_observed") == "true"},
        }},
        {"identity", Json{
            {"stable", GetKv(kv, "identity_stable") == "true"},
            {"verdict", GetKv(kv, "identity_verdict")},
            {"edidSerial", GetKv(kv, "edid_serial")},
            {"edidSerialMatch", GetKv(kv, "edid_serial_match") == "true"},
            {"edidInstance", GetKv(kv, "edid_instance")},
            {"edidContainerId", GetKv(kv, "edid_container_id")},
            {"edidContainerExpected", GetKv(kv, "edid_container_expected")},
            {"edidContainerMatch", GetKv(kv, "edid_container_match") == "true"},
            {"edidDeviceId", GetKv(kv, "edid_device_id")},
        }},
        {"density", Json{
            {"helperMonitorDpiX", intValue("monitor_dpi_x")},
            {"helperMonitorDpiY", intValue("monitor_dpi_y")},
        }},
        {"readyUtc", GetKv(kv, "ready_utc")},
    };
    return monitor;
}

Json BaseResult(const Options& options)
{
    Json result;
    result["schema"] = "deskfuse.gpu-poc.capture.v1";
    result["startedUtc"] = IsoNowUtc();
    result["finishedUtc"] = nullptr;
    result["gitSha"] = Utf8(options.gitSha);
    result["sdkVersion"] = POC_SDK_VERSION;
    result["osVersion"] = OsVersionString();
    result["elevated"] = IsElevated();
    result["monitorKv"] = Utf8(options.monitorKv);
    result["expected"] = Json{
        {"windowWidth", kWindowWidth},
        {"windowHeight", kWindowHeight},
        {"dpi", kExpectedDpi},
        {"rulerPx", kRulerPx},
        {"monitorWidth", kMonitorWidth},
        {"monitorHeight", kMonitorHeight},
        {"encoder", "NVENC H.264"},
    };
    result["access"] = Json{
        {"mode", IsElevated() ? "ELEVATED_HARDWARE_PHASE" : "NON_ELEVATED"},
        {"installedDriverAclModified", false},
        {"focusIddReinstalled", false},
        {"registryModifiedByPoc", false},
        {"topologyChangedByPoc", false},
    };
    result["verdicts"] = Json::array();
    return result;
}

void Usage()
{
    std::cout <<
        "DeskFuse GPU Phone Workspace POC (isolated; does not modify FocusIdd)\n"
        "\n"
        "  DeskFuse.GpuWorkspacePoc --plan\n"
        "  DeskFuse.GpuWorkspacePoc --run --monitor-kv PATH --out DIR [options]\n"
        "  DeskFuse.GpuWorkspacePoc --encoder-selftest --out DIR\n"
        "\n"
        "Options for --run:\n"
        "  --duration-sec N       capture/encode duration, default 20\n"
        "  --fps N                requested frame rate for the encoder, default 60\n"
        "  --bitrate N            H.264 bits/sec, default 20000000\n"
        "  --static               do not animate the test window\n"
        "  --save-full-reference  also save the full 1440x3120 reference BMP\n"
        "  --encoder nvenc|none   hardware encoder (default nvenc) or capture-only\n"
        "  --git-sha SHA          build/commit metadata for the evidence JSON\n"
        "\n"
        "The hardware phase must run elevated. The POC never opens or changes the\n"
        "installed FocusIdd ACL and refuses to run without the monitor handoff file.\n";
}

bool ParseArgs(int argc, wchar_t** argv, Options& options, std::wstring& error)
{
    auto next = [&](int& index, std::wstring& value) -> bool {
        if (index + 1 >= argc)
        {
            error = L"Missing value for " + std::wstring(argv[index]);
            return false;
        }
        value = argv[++index];
        return true;
    };

    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (arg == L"--plan") options.plan = true;
        else if (arg == L"--run") options.run = true;
        else if (arg == L"--static") options.animate = false;
        else if (arg == L"--save-full-reference") options.saveFullReference = true;
        else if (arg == L"--encoder-selftest") options.encoderSelfTest = true;
        else if (arg == L"--help" || arg == L"-h") options.help = true;
        else if (arg == L"--monitor-kv")
        {
            if (!next(i, options.monitorKv)) return false;
        }
        else if (arg == L"--out")
        {
            if (!next(i, options.outDir)) return false;
        }
        else if (arg == L"--encoder")
        {
            if (!next(i, options.encoder)) return false;
            std::transform(options.encoder.begin(), options.encoder.end(), options.encoder.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            if (options.encoder != L"nvenc" && options.encoder != L"none")
            {
                error = L"--encoder must be nvenc or none.";
                return false;
            }
        }
        else if (arg == L"--git-sha")
        {
            if (!next(i, options.gitSha)) return false;
        }
        else if (arg == L"--duration-sec" || arg == L"--fps" || arg == L"--bitrate")
        {
            std::wstring value;
            if (!next(i, value)) return false;
            try
            {
                const long long parsed = std::stoll(value);
                if (arg == L"--duration-sec")
                    options.durationSec = static_cast<int>(std::clamp<long long>(parsed, 5, 300));
                else if (arg == L"--fps")
                    options.fps = static_cast<int>(std::clamp<long long>(parsed, 1, 120));
                else
                    options.bitrate = static_cast<int>(std::clamp<long long>(parsed, 1000000, 100000000));
            }
            catch (...)
            {
                error = L"Invalid numeric value for " + arg + L": " + value;
                return false;
            }
        }
        else
        {
            error = L"Unknown argument: " + arg;
            return false;
        }
    }

    if (options.help) return true;
    if (options.encoderSelfTest)
    {
        if (options.outDir.empty()) error = L"--encoder-selftest requires --out DIR.";
        return error.empty();
    }
    if (!options.plan && !options.run)
    {
        error = L"Specify --plan, --run, or --encoder-selftest.";
        return false;
    }
    if (options.run)
    {
        if (options.monitorKv.empty()) error = L"--run requires --monitor-kv PATH.";
        else if (options.outDir.empty()) error = L"--run requires --out DIR.";
        if (!error.empty()) return false;
    }
    return true;
}

int PrintPlan()
{
    std::cout << "POC-PLAN schema=deskfuse.gpu-poc.plan.v1\n";
    std::cout << "os=" << OsVersionString() << "\n";
    std::cout << "sdk=" << POC_SDK_VERSION << "\n";
    std::cout << "elevated=" << (IsElevated() ? "true" : "false") << "\n";

    bool wgcSupported = false;
    bool wgcKnown = false;
    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        try
        {
            wgcSupported = winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported();
            wgcKnown = true;
        }
        catch (const winrt::hresult_error& error)
        {
            std::cout << "wgc_error=" << Utf8(WinRtErrorText(error)) << "\n";
        }
        winrt::uninit_apartment();
    }
    catch (const winrt::hresult_error& error)
    {
        std::cout << "winrt_error=" << Utf8(WinRtErrorText(error)) << "\n";
    }
    std::cout << "wgc_supported=" << (wgcKnown ? (wgcSupported ? "true" : "false") : "unknown") << "\n";

    HMODULE nvenc = LoadLibraryW(L"nvEncodeAPI64.dll");
    std::cout << "nvenc_dll=" << (nvenc ? "present" : "missing") << "\n";
    if (nvenc)
    {
        using GetMaxVersion = NVENCSTATUS(NVENCAPI*)(uint32_t*);
        auto getMax = reinterpret_cast<GetMaxVersion>(GetProcAddress(nvenc, "NvEncodeAPIGetMaxSupportedVersion"));
        uint32_t version = 0;
        if (getMax && getMax(&version) == NV_ENC_SUCCESS)
        {
            char raw[32]{};
            sprintf_s(raw, "0x%08X", version);
            std::cout << "nvenc_api=" << (version >> 4) << "." << (version & 0xF)
                      << " raw=" << raw << "\n";
        }
        else
        {
            std::cout << "nvenc_api=unavailable\n";
        }
        FreeLibrary(nvenc);
    }
    std::cout << "expected=1440x3120@60 dpi=336 ruler=350x350\n";
    return 0;
}

int RunEncoderSelfTest(const Options& options)
{
    std::error_code directoryError;
    std::filesystem::create_directories(std::filesystem::path(options.outDir), directoryError);
    if (directoryError)
    {
        std::cerr << "Could not create the self-test directory: " << Utf8(options.outDir) << "\n";
        return 10;
    }

    Json result;
    result["schema"] = "deskfuse.gpu-poc.encoder-selftest.v1";
    result["notAcceptanceEvidence"] = true;
    result["purpose"] = "D3D11 texture -> NVENC H.264 interop diagnostic; no WGC or monitor";
    result["startedUtc"] = IsoNowUtc();
    result["sdkVersion"] = POC_SDK_VERSION;
    result["osVersion"] = OsVersionString();
    result["elevated"] = IsElevated();

    auto finish = [&](const std::string& overall, int code, const std::wstring& detail) -> int {
        result["overall"] = overall;
        result["finishedUtc"] = IsoNowUtc();
        if (!detail.empty())
            result["failure"] = Json{ {"code", overall}, {"detail", Utf8(detail)} };
        std::wstring writeError;
        if (!WriteTextFile(Join(options.outDir, L"encoder-selftest.json"), DumpJson(result), writeError))
            std::cerr << "Could not write the self-test result: " << Utf8(writeError) << "\n";
        PrintLine(L"POC-ENC-SELFTEST " + Widen(overall));
        return code;
    };

    const int width = kWindowWidth;
    const int height = kWindowHeight;
    D3D_FEATURE_LEVEL featureLevel = D3D_FEATURE_LEVEL_11_0;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    auto createDevice = [&](const D3D_FEATURE_LEVEL* levels, UINT count) -> HRESULT {
        device.Reset();
        context.Reset();
        return D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, count, D3D11_SDK_VERSION,
            device.GetAddressOf(), &featureLevel, context.GetAddressOf());
    };
    const D3D_FEATURE_LEVEL levels11[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = createDevice(levels11, 2);
    if (FAILED(hr))
    {
        const D3D_FEATURE_LEVEL levels10[] = { D3D_FEATURE_LEVEL_11_0 };
        hr = createDevice(levels10, 1);
    }
    if (FAILED(hr))
        return finish("SELF_TEST_D3D11_DEVICE_FAILED", 10, L"D3D11CreateDevice failed: " + Widen(FormatHresult(hr)));
    PrintLine(L"POC-ENC-SELFTEST device=" + Widen(FeatureLevelName(featureLevel)));

    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc{};
    if (SUCCEEDED(device.As(&dxgiDevice)) && SUCCEEDED(dxgiDevice->GetAdapter(adapter.GetAddressOf())))
        adapter->GetDesc(&adapterDesc);
    result["gpu"] = Json{
        {"description", Utf8(std::wstring(adapterDesc.Description))},
        {"vendorId", adapterDesc.VendorId},
        {"deviceId", adapterDesc.DeviceId},
        {"featureLevel", FeatureLevelName(featureLevel)},
    };

    std::vector<uint8_t> pixels(static_cast<size_t>(width) * static_cast<size_t>(height) * 4);
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            uint8_t* p = pixels.data() + (static_cast<size_t>(y) * width + x) * 4;
            p[0] = static_cast<uint8_t>((x * 255) / (width - 1));
            p[1] = static_cast<uint8_t>((y * 255) / (height - 1));
            p[2] = ((x / 64 + y / 64) & 1) ? 255 : 0;
            p[3] = 255;
        }
    }

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initial{};
    initial.pSysMem = pixels.data();
    initial.SysMemPitch = static_cast<UINT>(width) * 4;
    ComPtr<ID3D11Texture2D> texture0;
    ComPtr<ID3D11Texture2D> texture1;
    hr = device->CreateTexture2D(&desc, &initial, texture0.GetAddressOf());
    if (SUCCEEDED(hr))
        hr = device->CreateTexture2D(&desc, &initial, texture1.GetAddressOf());
    if (FAILED(hr))
        return finish("SELF_TEST_TEXTURE_FAILED", 10, L"CreateTexture2D failed: " + Widen(FormatHresult(hr)));

    std::vector<ID3D11Texture2D*> slots{ texture0.Get(), texture1.Get() };
    EncoderConfig encoderConfig{};
    encoderConfig.width = width;
    encoderConfig.height = height;
    encoderConfig.fps = 60;
    encoderConfig.bitrate = 20000000;
    encoderConfig.outputPath = Join(options.outDir, L"encoder-selftest.h264");

    NvEncoder encoder;
    std::wstring error;
    const bool initialized = encoder.Initialize(device.Get(), slots.data(),
        static_cast<int>(slots.size()), encoderConfig, error);
    result["encoder"] = Json{
        {"backend", "NVENC"},
        {"codec", "H.264"},
        {"preset", Utf8(encoder.Preset())},
        {"initialized", initialized},
        {"maxApiVersion", encoder.MaxApiVersion()},
        {"output", Utf8(encoderConfig.outputPath)},
        {"error", Utf8(error)},
    };
    if (!initialized)
    {
        PrintLine(L"POC-ENC-SELFTEST init blocked: " + error);
        encoder.Shutdown();
        return finish("ENCODER_BLOCKED", 20, error);
    }
    PrintLine(L"POC-ENC-SELFTEST initialized maxApi=" + std::to_wstring(encoder.MaxApiVersion()));

    std::vector<double> submitTimes;
    std::vector<double> lockTimes;
    std::vector<double> totalTimes;
    int failures = 0;
    std::wstring firstError;
    for (int i = 0; i < 60; ++i)
    {
        uint32_t bytes = 0;
        double submitMs = 0;
        double lockMs = 0;
        const int64_t start = QpcNow();
        if (!encoder.Encode(i % 2, static_cast<uint64_t>(i) * 166667u, static_cast<uint32_t>(i),
            bytes, submitMs, lockMs, error))
        {
            ++failures;
            if (firstError.empty()) firstError = error;
            break;
        }
        submitTimes.push_back(submitMs);
        lockTimes.push_back(lockMs);
        totalTimes.push_back(QpcDeltaMs(start, QpcNow()));
        if ((i + 1) % 10 == 0)
            PrintLine(L"POC-ENC-SELFTEST encoded=" + std::to_wstring(encoder.FramesEncoded()) +
                L" framesSubmitted=" + std::to_wstring(i + 1));
    }

    PrintLine(L"POC-ENC-SELFTEST submit loop done");
    std::wstring flushError;
    const bool flushed = encoder.Flush(flushError);
    PrintLine(L"POC-ENC-SELFTEST flush=" + std::to_wstring(flushed ? 1 : 0) + L" error=" + flushError);
    if (!flushed && failures == 0)
    {
        ++failures;
        firstError = flushError;
    }
    const uint64_t framesEncoded = encoder.FramesEncoded();
    const uint64_t bytesWritten = encoder.BytesWritten();
    PrintLine(L"POC-ENC-SELFTEST before shutdown encoded=" + std::to_wstring(framesEncoded) +
        L" bytes=" + std::to_wstring(bytesWritten));
    encoder.Shutdown();
    PrintLine(L"POC-ENC-SELFTEST shutdown done");

    const BitstreamCheck bitstream = InspectBitstream(encoderConfig.outputPath);
    const bool pass = failures == 0 && framesEncoded >= 60 && bitstream.exists &&
        bitstream.annexB && bitstream.sps && bitstream.pps && bitstream.idr;
    result["frames"] = Json{
        {"submitted", 60},
        {"encoded", framesEncoded},
        {"bytesWritten", bytesWritten},
    };
    result["bitstream"] = Json{
        {"path", Utf8(encoderConfig.outputPath)},
        {"exists", bitstream.exists},
        {"size", bitstream.size},
        {"annexB", bitstream.annexB},
        {"sps", bitstream.sps},
        {"pps", bitstream.pps},
        {"idr", bitstream.idr},
        {"firstBytesHex", bitstream.firstBytes},
    };
    result["latency"] = Json{
        {"submitToEncodeReturn", PercentileJson(ComputePercentiles(submitTimes))},
        {"bitstreamLock", PercentileJson(ComputePercentiles(lockTimes))},
        {"textureToBitstream", PercentileJson(ComputePercentiles(totalTimes))},
    };
    result["flushed"] = flushed;
    if (failures > 0) result["error"] = Utf8(firstError);

    if (!pass)
        return finish("SELF_TEST_NVENC_H264_INCOMPLETE", 20,
            firstError.empty() ? L"Not all 60 frames produced a valid H.264 Annex B bitstream." : firstError);
    return finish("SELF_TEST_NVENC_H264_PASS", 0, L"");
}

int Run(const Options& options)
{
    std::error_code directoryError;
    std::filesystem::create_directories(std::filesystem::path(options.outDir), directoryError);
    if (directoryError)
    {
        std::cerr << "Could not create the evidence directory: " << Utf8(options.outDir) << "\n";
        return 10;
    }

    Json result = BaseResult(options);
    if (!IsElevated())
    {
        return Fail(result, options.outDir, "PRECHECK_ELEVATION_REQUIRED",
            L"The hardware phase must run elevated because the installed FocusIdd device ACL "
            L"does not grant normal interactive users access. Do not change the installed ACL.",
            10);
    }

    std::wstring error;
    const auto kv = ReadKvFile(options.monitorKv, error);
    if (kv.empty())
    {
        return Fail(result, options.outDir, "MONITOR_KV_UNREADABLE", error, 10);
    }
    result["monitor"] = MonitorJson(kv);

    if (GetKv(kv, "schema") != "deskfuse.gpu-poc.monitor.v1")
        return Fail(result, options.outDir, "MONITOR_KV_SCHEMA_MISMATCH",
            L"The monitor handoff is not the POC v1 schema.", 10);

    int left = 0, top = 0, monitorWidth = 0, monitorHeight = 0, refresh = 0;
    int currentScale = 0, desiredScale = 0, minScale = 0, maxScale = 0;
    if (!GetKvInt(kv, "left", left) || !GetKvInt(kv, "top", top) ||
        !GetKvInt(kv, "width", monitorWidth) || !GetKvInt(kv, "height", monitorHeight) ||
        !GetKvInt(kv, "refresh_hz", refresh) || !GetKvInt(kv, "current_scale", currentScale) ||
        !GetKvInt(kv, "desired_scale", desiredScale) || !GetKvInt(kv, "min_scale", minScale) ||
        !GetKvInt(kv, "max_scale", maxScale))
    {
        return Fail(result, options.outDir, "MONITOR_KV_INVALID",
            L"The monitor handoff is missing a numeric field.", 10);
    }
    if (monitorWidth != kMonitorWidth || monitorHeight != kMonitorHeight)
    {
        return Fail(result, options.outDir, "MONITOR_MODE_MISMATCH",
            L"Expected the 2100x3120 virtual monitor; got " + std::to_wstring(monitorWidth) +
            L"x" + std::to_wstring(monitorHeight) + L".", 10);
    }
    if (refresh != 60)
    {
        return Fail(result, options.outDir, "MONITOR_REFRESH_MISMATCH",
            L"Expected 60 Hz; got " + std::to_wstring(refresh) + L" Hz.", 10);
    }
    if (currentScale != 350 || desiredScale != 350 || minScale > 350 || maxScale < 350)
    {
        return Fail(result, options.outDir, "SCALE_DPI_MISMATCH",
            L"Expected a readback-proven 350% scale (supported maximum must cover 350%); got current=" +
            std::to_wstring(currentScale) + L"%, desired=" + std::to_wstring(desiredScale) +
            L"%, min=" + std::to_wstring(minScale) + L"%, max=" + std::to_wstring(maxScale) + L"%.", 11);
    }
    if (GetKv(kv, "identity_stable") != "true" || GetKv(kv, "edid_serial_match") != "true")
    {
        return Fail(result, options.outDir, "IDENTITY_UNVERIFIED",
            L"The helper did not prove the per-client EDID serial on the active monitor; refusing to gate capture.",
            11);
    }

    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    {
        const DWORD dpiError = GetLastError();
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),
            DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            return Fail(result, options.outDir, "PRECHECK_DPI_AWARENESS_FAILED",
                L"Could not establish PerMonitorV2 awareness: " + Widen(FormatWin32(dpiError)), 10);
        }
    }

    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    }
    catch (const winrt::hresult_error& winrtError)
    {
        return Fail(result, options.outDir, "PRECHECK_WINRT_FAILED",
            L"winrt::init_apartment failed: " + WinRtErrorText(winrtError), 10);
    }

    bool wgcSupported = false;
    try
    {
        wgcSupported = winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported();
    }
    catch (const winrt::hresult_error& winrtError)
    {
        return Fail(result, options.outDir, "WGC_PROBE_FAILED",
            L"Windows.Graphics.Capture support probe failed: " + WinRtErrorText(winrtError), 10);
    }
    if (!wgcSupported)
    {
        return Fail(result, options.outDir, "WGC_UNSUPPORTED",
            L"Windows.Graphics.Capture reports that this system does not support capture.", 10);
    }
    result["wgc"] = Json{ {"supported", true}, {"framePool", "free-threaded"}, {"cursor", false} };

    D3D_FEATURE_LEVEL featureLevel = D3D_FEATURE_LEVEL_11_0;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    auto createDevice = [&](const D3D_FEATURE_LEVEL* levels, UINT count) -> HRESULT {
        device.Reset();
        context.Reset();
        return D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, count, D3D11_SDK_VERSION,
            device.GetAddressOf(), &featureLevel, context.GetAddressOf());
    };
    const D3D_FEATURE_LEVEL levels11[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = createDevice(levels11, 2);
    if (FAILED(hr))
    {
        const D3D_FEATURE_LEVEL levels10[] = { D3D_FEATURE_LEVEL_11_0 };
        hr = createDevice(levels10, 1);
    }
    if (FAILED(hr))
    {
        return Fail(result, options.outDir, "D3D11_DEVICE_FAILED",
            L"D3D11CreateDevice failed: " + Widen(FormatHresult(hr)), 10);
    }

    ComPtr<ID3D10Multithread> multithread;
    if (SUCCEEDED(device.As(&multithread)) && multithread)
        multithread->SetMultithreadProtected(TRUE);

    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc{};
    if (SUCCEEDED(device.As(&dxgiDevice)) && SUCCEEDED(dxgiDevice->GetAdapter(adapter.GetAddressOf())))
        adapter->GetDesc(&adapterDesc);
    result["gpu"] = Json{
        {"description", Utf8(std::wstring(adapterDesc.Description))},
        {"vendorId", adapterDesc.VendorId},
        {"deviceId", adapterDesc.DeviceId},
        {"subSysId", adapterDesc.SubSysId},
        {"revision", adapterDesc.Revision},
        {"dedicatedVideoMemory", adapterDesc.DedicatedVideoMemory},
        {"sharedSystemMemory", adapterDesc.SharedSystemMemory},
        {"luidHigh", static_cast<uint64_t>(adapterDesc.AdapterLuid.HighPart)},
        {"luidLow", static_cast<uint64_t>(adapterDesc.AdapterLuid.LowPart)},
        {"featureLevel", FeatureLevelName(featureLevel)},
        {"multithreadProtected", multithread != nullptr},
    };

    TestWindowOptions windowOptions{};
    windowOptions.x = left + (monitorWidth - kWindowWidth) / 2;
    windowOptions.y = top;
    windowOptions.width = kWindowWidth;
    windowOptions.height = kWindowHeight;
    windowOptions.expectedDpi = kExpectedDpi;
    windowOptions.animate = options.animate;

    TestWindow window;
    if (!window.Create(windowOptions, error))
    {
        return Fail(result, options.outDir, "WINDOW_CREATE_FAILED", error, 12);
    }

    for (int i = 0; i < 100 && window.Dpi() != kExpectedDpi; ++i)
    {
        window.PumpMessages();
        Sleep(20);
    }
    window.PumpMessages();

    wchar_t hwndText[32]{};
    swprintf_s(hwndText, L"0x%llX", static_cast<unsigned long long>(
        reinterpret_cast<uintptr_t>(window.Hwnd())));
    result["window"] = Json{
        {"hwnd", Utf8(hwndText)},
        {"x", windowOptions.x},
        {"y", windowOptions.y},
        {"width", kWindowWidth},
        {"height", kWindowHeight},
        {"expectedDpi", kExpectedDpi},
        {"dpi", window.Dpi()},
        {"dpiChanged", window.DpiChanged()},
        {"awareness", DpiAwarenessName(GetWindowDpiAwarenessContext(window.Hwnd()))},
        {"processAwareness", DpiAwarenessName(GetThreadDpiAwarenessContext())},
        {"animated", options.animate},
        {"windowRect", Json::array({
            window.WindowRect().left, window.WindowRect().top,
            window.WindowRect().right, window.WindowRect().bottom })},
        {"clientRect", Json::array({
            window.ClientRect().left, window.ClientRect().top,
            window.ClientRect().right, window.ClientRect().bottom })},
    };

    if (window.Dpi() != kExpectedDpi)
    {
        const int actual = window.Dpi();
        window.Destroy();
        return Fail(result, options.outDir, "SCALE_DPI_MISMATCH",
            L"GetDpiForWindow returned " + std::to_wstring(actual) + L", expected " +
            std::to_wstring(kExpectedDpi) + L" (350%). Capture is gated.", 11);
    }
    if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(window.Hwnd()),
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    {
        window.Destroy();
        return Fail(result, options.outDir, "PMV2_GATE_FAILED",
            L"The test window is not PerMonitorV2-aware.", 11);
    }

    RECT clientRect{};
    GetClientRect(window.Hwnd(), &clientRect);
    if (clientRect.right != kWindowWidth || clientRect.bottom != kWindowHeight)
    {
        window.Destroy();
        return Fail(result, options.outDir, "WINDOW_GEOMETRY_MISMATCH",
            L"Client rectangle is " + std::to_wstring(clientRect.right) + L"x" +
            std::to_wstring(clientRect.bottom) + L", expected 1440x3120.", 12);
    }

    HMONITOR monitor = MonitorFromWindow(window.Hwnd(), MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW monitorInfo{};
    monitorInfo.cbSize = sizeof(monitorInfo);
    if (!monitor || !GetMonitorInfoW(monitor, &monitorInfo))
    {
        window.Destroy();
        return Fail(result, options.outDir, "MONITOR_INFO_FAILED",
            L"GetMonitorInfoW failed for the test window.", 12);
    }
    const std::wstring actualMonitorName = monitorInfo.szDevice;
    const std::wstring expectedMonitorName = Widen(GetKv(kv, "gdi_name"));
    result["window"]["monitorDevice"] = Utf8(actualMonitorName);
    result["window"]["monitorRect"] = Json::array({
        monitorInfo.rcMonitor.left, monitorInfo.rcMonitor.top,
        monitorInfo.rcMonitor.right, monitorInfo.rcMonitor.bottom });
    if (_wcsicmp(actualMonitorName.c_str(), expectedMonitorName.c_str()) != 0)
    {
        window.Destroy();
        return Fail(result, options.outDir, "WINDOW_NOT_ON_VIRTUAL_MONITOR",
            L"The test window is on " + actualMonitorName + L", expected " + expectedMonitorName + L".", 12);
    }
    if (monitorInfo.rcMonitor.left != left || monitorInfo.rcMonitor.top != top ||
        monitorInfo.rcMonitor.right - monitorInfo.rcMonitor.left != monitorWidth ||
        monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top != monitorHeight)
    {
        window.Destroy();
        return Fail(result, options.outDir, "WINDOW_MONITOR_BOUNDS_MISMATCH",
            L"The virtual-monitor bounds changed between the helper handoff and window creation.", 12);
    }

    PrintLine(L"POC-WINDOW hwnd=" + std::wstring(hwndText) + L" dpi=" +
        std::to_wstring(window.Dpi()) + L" awareness=PerMonitorV2 client=" +
        std::to_wstring(clientRect.right) + L"x" + std::to_wstring(clientRect.bottom) +
        L" monitor=" + actualMonitorName);

    // One-off CPU readback to verify the exact 350x350 physical-pixel ruler.
    WgcCapture referenceCapture(device.Get(), context.Get(), kWindowWidth, kWindowHeight, 6);
    if (!referenceCapture.CreateResources(error))
    {
        window.Destroy();
        return Fail(result, options.outDir, "WGC_RESOURCE_FAILED", error, 13);
    }
    if (!referenceCapture.Start(window.Hwnd(), error))
    {
        window.Destroy();
        return Fail(result, options.outDir, "WGC_START_FAILED", error, 13);
    }
    PrintLine(L"POC-WGC reference started");

    std::vector<uint8_t> referencePixels;
    int referenceWidth = 0;
    int referenceHeight = 0;
    int referenceFrames = 0;
    RulerResult ruler;
    bool referencePass = false;
    const int64_t referenceStart = QpcNow();
    int64_t lastReferenceLog = referenceStart;
    int referenceAttempts = 0;
    while (QpcDeltaMs(referenceStart, QpcNow()) < 10000.0)
    {
        window.PumpMessages();
        CapturedFrame frame;
        if (!referenceCapture.WaitFrame(frame, 50))
        {
            if (!referenceCapture.Error().empty()) break;
            if (QpcDeltaMs(lastReferenceLog, QpcNow()) >= 1000.0)
            {
                lastReferenceLog = QpcNow();
                PrintLine(L"POC-WGC reference waiting arrived=" +
                    std::to_wstring(referenceCapture.Arrived()) + L" error=" + referenceCapture.Error());
            }
            continue;
        }
        ++referenceFrames;
        if (!referenceCapture.Readback(frame.slot, referencePixels, referenceWidth, referenceHeight, error))
        {
            PrintLine(L"POC-WGC reference readback failed: " + error);
            referenceCapture.ReleaseFrame(frame);
            break;
        }
        ruler = AnalyzeRuler(referencePixels, referenceWidth, referenceHeight);
        referenceCapture.ReleaseFrame(frame);
        if (ruler.pass)
        {
            referencePass = true;
            break;
        }
        if (++referenceAttempts >= 5)
            break;
    }
    const uint64_t referenceArrived = referenceCapture.Arrived();
    const uint64_t referenceWrongSize = referenceCapture.WrongSize();
    PrintLine(L"POC-WGC reference loop done frames=" + std::to_wstring(referenceFrames) +
        L" arrived=" + std::to_wstring(referenceArrived) + L" pass=" +
        std::to_wstring(referencePass ? 1 : 0));
    referenceCapture.Stop();
    PrintLine(L"POC-WGC reference stopped");

    std::wstring referenceBmp = Join(options.outDir,
        referencePass ? L"gpu-poc-reference.bmp" : L"gpu-poc-reference-failed.bmp");
    bool referenceBmpSaved = false;
    if (!referencePixels.empty())
    {
        std::wstring bmpError;
        referenceBmpSaved = WriteBgraBmp(referenceBmp, referencePixels, referenceWidth, referenceHeight,
            kRulerOriginX - 10, kRulerOriginY - 10, kRulerPx + 20, kRulerPx + 20, bmpError);
        if (!referenceBmpSaved)
            PrintLine(L"POC-WARN: " + bmpError);
    }
    if (referencePass && options.saveFullReference)
    {
        std::wstring bmpError;
        if (!WriteBgraBmp(Join(options.outDir, L"gpu-poc-reference-full.bmp"), referencePixels,
            referenceWidth, referenceHeight, 0, 0, referenceWidth, referenceHeight, bmpError))
            PrintLine(L"POC-WARN: " + bmpError);
    }
    if (!referencePass && !referencePixels.empty())
    {
        // A tiny corner crop is enough to identify an unexpected occluder or
        // color conversion without writing an 18 MB failure bitmap.
        std::wstring bmpError;
        WriteBgraBmp(Join(options.outDir, L"gpu-poc-reference-corner.bmp"), referencePixels,
            referenceWidth, referenceHeight, 0, 0, 240, 240, bmpError);
    }

    result["reference"] = Json{
        {"purpose", "one-off CPU readback for physical-pixel ruler verification"},
        {"framesObserved", referenceFrames},
        {"framesArrived", referenceArrived},
        {"wrongSizeFrames", referenceWrongSize},
        {"width", referenceWidth},
        {"height", referenceHeight},
        {"greenPixels", ruler.greenPixels},
        {"expectedGreenPixels", static_cast<uint64_t>(kRulerPx) * kRulerPx},
        {"greenBBox", Json{
            {"left", ruler.greenLeft}, {"top", ruler.greenTop},
            {"right", ruler.greenRight}, {"bottom", ruler.greenBottom}}},
        {"checkerMismatches", ruler.checkerMismatches},
        {"checkerSamples", ruler.checkerSamples},
        {"lineWhitePixels", ruler.lineWhitePixels},
        {"lineExpectedPixels", kLineHeight},
        {"colorProbeMismatches", ruler.colorProbeMismatches},
        {"colorProbeSamples", ruler.colorProbeSamples},
        {"colorProbes", Json::array({
            "red", "green", "blue", "cyan", "magenta", "yellow", "white", "black"})},
        {"backgroundMagenta", ruler.backgroundOk},
        {"backgroundCornersBgra", Json::array({
            HexBytes(ruler.corners[0], 4),
            HexBytes(ruler.corners[1], 4),
            HexBytes(ruler.corners[2], 4),
            HexBytes(ruler.corners[3], 4)})},
        {"pass", referencePass},
        {"detail", ruler.detail},
        {"bmp", Utf8(referenceBmp)},
        {"bmpSaved", referenceBmpSaved},
        {"cpuReadbackInSteadyState", false},
    };

    if (!referencePass)
    {
        window.Destroy();
        return Fail(result, options.outDir, "RULER_VERIFICATION_FAILED",
            L"The captured HWND did not contain the exact 350x350 green ruler at 1:1 physical pixels; capture is gated. " +
            Widen(ruler.detail), 13);
    }
    AddVerdict(result, "PMV2_336_DPI_VERIFIED");
    AddVerdict(result, "RULER_350x350_1TO1_VERIFIED");
    AddVerdict(result, "WGC_HWND_1440x3120_EXACT");
    PrintLine(L"POC-RULER pass=true green=" + std::to_wstring(ruler.greenPixels) +
        L" checkerMismatches=" + std::to_wstring(ruler.checkerMismatches) +
        L" lineWhite=" + std::to_wstring(ruler.lineWhitePixels));

    WgcCapture capture(device.Get(), context.Get(), kWindowWidth, kWindowHeight, 6);
    if (!capture.CreateResources(error))
    {
        window.Destroy();
        return Fail(result, options.outDir, "WGC_RESOURCE_FAILED", error, 13);
    }

    std::unique_ptr<NvEncoder> encoder;
    bool encoderReady = false;
    std::wstring encoderError;
    result["encoder"] = Json{
        {"backend", Utf8(options.encoder)},
        {"codec", "H.264"},
        {"initialized", false},
        {"verdict", "NOT_STARTED"},
    };

    if (options.encoder == L"nvenc")
    {
        std::vector<ID3D11Texture2D*> slotTextures;
        for (int i = 0; i < capture.SlotCount(); ++i)
            slotTextures.push_back(capture.SlotTexture(i));

        EncoderConfig encoderConfig{};
        encoderConfig.width = kWindowWidth;
        encoderConfig.height = kWindowHeight;
        encoderConfig.fps = options.fps;
        encoderConfig.bitrate = options.bitrate;
        encoderConfig.outputPath = Join(options.outDir, L"gpu-poc-capture.h264");

        auto candidate = std::make_unique<NvEncoder>();
        encoderReady = candidate->Initialize(device.Get(), slotTextures.data(),
            static_cast<int>(slotTextures.size()), encoderConfig, encoderError);
        result["encoder"] = Json{
            {"backend", "NVENC"},
            {"codec", "H.264"},
            {"preset", Utf8(candidate->Preset())},
            {"initialized", encoderReady},
            {"maxApiVersion", candidate->MaxApiVersion()},
            {"width", kWindowWidth},
            {"height", kWindowHeight},
            {"fps", options.fps},
            {"bitrate", options.bitrate},
            {"inputFormat", "ARGB"},
            {"outputRange", "full"},
            {"colorPrimaries", "BT.709"},
            {"transferCharacteristics", "BT.709"},
            {"matrixCoefficients", "BT.709"},
            {"chromaFormat", "4:2:0"},
            {"output", Utf8(encoderConfig.outputPath)},
            {"error", Utf8(encoderError)},
            {"verdict", encoderReady ? "INITIALIZED" : "ENCODER_BLOCKED"},
        };
        if (encoderReady)
            encoder = std::move(candidate);
        else
            PrintLine(L"POC-ENCODER blocked: " + encoderError);
    }
    else
    {
        result["encoder"]["verdict"] = "ENCODER_NOT_REQUESTED";
    }

    if (!capture.Start(window.Hwnd(), error))
    {
        window.Destroy();
        return Fail(result, options.outDir, "WGC_START_FAILED", error, 13);
    }
    PrintLine(L"POC-WGC steady capture started");

    bool gotFirstFrame = false;
    const int64_t warmupStart = QpcNow();
    int64_t lastWarmupLog = warmupStart;
    while (QpcDeltaMs(warmupStart, QpcNow()) < 3000.0)
    {
        window.PumpMessages();
        CapturedFrame frame;
        if (capture.WaitFrame(frame, 50))
        {
            capture.ReleaseFrame(frame);
            gotFirstFrame = true;
            break;
        }
        if (!capture.Error().empty()) break;
        if (QpcDeltaMs(lastWarmupLog, QpcNow()) >= 1000.0)
        {
            lastWarmupLog = QpcNow();
            PrintLine(L"POC-WGC steady waiting arrived=" + std::to_wstring(capture.Arrived()) +
                L" error=" + capture.Error());
        }
    }
    if (!gotFirstFrame)
    {
        const std::wstring captureError = capture.Error();
        capture.Stop();
        window.Destroy();
        return Fail(result, options.outDir, "WGC_NO_FRAME",
            captureError.empty() ? L"No WGC frame arrived within 3 seconds." : captureError, 13);
    }

    int processed = 0;
    int encodeAttempts = 0;
    int encodeErrors = 0;
    int encodedFrames = 0;
    bool encodeFailed = false;
    std::wstring firstEncodeError;
    std::wstring captureRuntimeError;
    bool windowClosedEarly = false;
    int64_t firstArrival = 0;
    int64_t lastArrival = 0;
    std::vector<double> gpuCopyTimes;
    std::vector<double> arrivalIntervals;
    std::vector<double> captureToSubmitTimes;
    std::vector<double> submitTimes;
    std::vector<double> lockTimes;
    std::vector<double> totalTimes;
    std::vector<FrameRow> rows;

    const int64_t runStart = QpcNow();
    while (QpcDeltaMs(runStart, QpcNow()) < static_cast<double>(options.durationSec) * 1000.0)
    {
        window.PumpMessages();
        if (window.Closed())
        {
            windowClosedEarly = true;
            break;
        }

        CapturedFrame frame;
        if (!capture.WaitFrame(frame, 10))
        {
            if (!capture.Error().empty())
            {
                captureRuntimeError = capture.Error();
                break;
            }
            Sleep(1);
            continue;
        }

        ++processed;
        if (firstArrival == 0) firstArrival = frame.qpcArrival;
        const double arrivalMs = QpcDeltaMs(firstArrival, frame.qpcArrival);
        if (lastArrival != 0)
            arrivalIntervals.push_back(QpcDeltaMs(lastArrival, frame.qpcArrival));
        lastArrival = frame.qpcArrival;
        if (frame.gpuCopyMs >= 0) gpuCopyTimes.push_back(frame.gpuCopyMs);

        const int64_t encodeStart = QpcNow();
        double submitMs = 0;
        double lockMs = 0;
        uint32_t bitstreamBytes = 0;
        bool encodeOk = true;
        if (encoderReady)
        {
            const uint32_t frameIndex = static_cast<uint32_t>(encodeAttempts);
            ++encodeAttempts;
            encodeOk = encoder->Encode(frame.slot, static_cast<uint64_t>(frame.systemRelativeTime),
                frameIndex, bitstreamBytes, submitMs, lockMs, encoderError);
            if (!encodeOk)
            {
                if (!encodeFailed) firstEncodeError = encoderError;
                encodeFailed = true;
                ++encodeErrors;
            }
        }
        const int64_t encodeDone = QpcNow();

        if (encoderReady && encodeOk)
        {
            const double captureToSubmit = QpcDeltaMs(frame.qpcArrival, encodeStart);
            const double total = QpcDeltaMs(frame.qpcArrival, encodeDone);
            captureToSubmitTimes.push_back(captureToSubmit);
            submitTimes.push_back(submitMs);
            lockTimes.push_back(lockMs);
            totalTimes.push_back(total);
            if (bitstreamBytes > 0) ++encodedFrames;
            FrameRow row{};
            row.frame = static_cast<uint32_t>(encodeAttempts - 1);
            row.arrivalQpc = frame.qpcArrival;
            row.arrivalMs = arrivalMs;
            row.systemRelativeTime = frame.systemRelativeTime;
            row.gpuCopyMs = frame.gpuCopyMs;
            row.captureToSubmitMs = captureToSubmit;
            row.submitMs = submitMs;
            row.lockMs = lockMs;
            row.totalMs = total;
            row.bitstreamBytes = bitstreamBytes;
            rows.push_back(row);
        }

        capture.ReleaseFrame(frame);
    }

    capture.Stop();

    bool flushed = false;
    if (encoder)
    {
        std::wstring flushError;
        flushed = encoder->Flush(flushError);
        if (!flushed && !encodeFailed)
        {
            firstEncodeError = flushError;
            encodeFailed = true;
        }
    }

    const uint64_t arrived = capture.Arrived();
    const uint64_t wrongSize = capture.WrongSize();
    const uint64_t droppedNoSlot = capture.DroppedNoSlot();
    const uint64_t copyErrors = capture.CopyErrors();
    const uint64_t unsupportedFormat = capture.UnsupportedFormat();
    const uint64_t skippedEmpty = capture.SkippedEmpty();

    uint64_t framesEncoded = 0;
    uint64_t bytesWritten = 0;
    uint32_t maxApiVersion = 0;
    if (encoder)
    {
        framesEncoded = encoder->FramesEncoded();
        bytesWritten = encoder->BytesWritten();
        maxApiVersion = encoder->MaxApiVersion();
    }
    if (encoder) encoder->Shutdown();

    window.Destroy();

    const BitstreamCheck bitstream = InspectBitstream(Join(options.outDir, L"gpu-poc-capture.h264"));
    const bool capturePass = !windowClosedEarly && captureRuntimeError.empty() &&
        arrived >= 30 && processed >= 30 && wrongSize == 0 && copyErrors == 0 &&
        unsupportedFormat == 0;
    const bool encoderPass = encoderReady && framesEncoded > 0 && !encodeFailed &&
        bitstream.exists && bitstream.annexB && bitstream.sps && bitstream.pps && bitstream.idr;
    const bool encoderBlocked = encoderReady ? (framesEncoded == 0 || encodeFailed) : true;

    result["capture"] = Json{
        {"source", "Windows.Graphics.Capture HWND"},
        {"itemWidth", capture.Width()},
        {"itemHeight", capture.Height()},
        {"format", "DXGI_FORMAT_B8G8R8A8_UNORM"},
        {"slots", capture.SlotCount()},
        {"framesArrived", arrived},
        {"framesProcessed", processed},
        {"wrongSizeFrames", wrongSize},
        {"droppedNoFreeSlot", droppedNoSlot},
        {"copyErrors", copyErrors},
        {"unsupportedFormatFrames", unsupportedFormat},
        {"skippedEmptyFrames", skippedEmpty},
        {"durationSec", options.durationSec},
        {"requestedFps", options.fps},
        {"animatedWindow", options.animate},
        {"cpuReadbackInSteadyState", false},
        {"runtimeError", Utf8(captureRuntimeError)},
        {"windowClosedEarly", windowClosedEarly},
        {"pass", capturePass},
    };

    Json encoderJson = result["encoder"];
    encoderJson["initialized"] = encoderReady;
    encoderJson["maxApiVersion"] = maxApiVersion;
    encoderJson["framesAttempted"] = encodeAttempts;
    encoderJson["framesSubmitted"] = rows.size();
    encoderJson["framesEncoded"] = framesEncoded;
    encoderJson["bytesWritten"] = bytesWritten;
    encoderJson["encodeErrors"] = encodeErrors;
    encoderJson["flushed"] = flushed;
    encoderJson["error"] = Utf8(firstEncodeError.empty() ? encoderError : firstEncodeError);
    encoderJson["verdict"] = encoderPass ? "NVENC_H264_HARDWARE_ENCODED" :
        (encoderBlocked ? "ENCODER_BLOCKED" : "ENCODER_INCOMPLETE");
    result["encoder"] = encoderJson;
    result["bitstream"] = Json{
        {"path", Utf8(Join(options.outDir, L"gpu-poc-capture.h264"))},
        {"exists", bitstream.exists},
        {"size", bitstream.size},
        {"annexB", bitstream.annexB},
        {"sps", bitstream.sps},
        {"pps", bitstream.pps},
        {"idr", bitstream.idr},
        {"firstBytesHex", bitstream.firstBytes},
    };

    result["latency"] = Json{
        {"arrivalInterval", PercentileJson(ComputePercentiles(arrivalIntervals))},
        {"gpuCopy", PercentileJson(ComputePercentiles(gpuCopyTimes))},
        {"captureToSubmit", PercentileJson(ComputePercentiles(captureToSubmitTimes))},
        {"submitToEncodeReturn", PercentileJson(ComputePercentiles(submitTimes))},
        {"bitstreamLock", PercentileJson(ComputePercentiles(lockTimes))},
        {"arrivalToBitstream", PercentileJson(ComputePercentiles(totalTimes))},
    };
    result["frames"] = Json{
        {"processed", processed},
        {"encodedRows", rows.size()},
        {"encodedFrames", encodedFrames},
    };

    if (!capturePass)
    {
        std::wstring detail = L"The exact 1440x3120 WGC capture did not pass the continuity gates. ";
        if (!captureRuntimeError.empty()) detail += captureRuntimeError;
        else if (windowClosedEarly) detail += L"The test window closed early.";
        else detail += L"arrived=" + std::to_wstring(arrived) + L", processed=" +
            std::to_wstring(processed) + L", wrongSize=" + std::to_wstring(wrongSize) +
            L", copyErrors=" + std::to_wstring(copyErrors) + L", unsupportedFormat=" +
            std::to_wstring(unsupportedFormat) + L".";
        std::wstring csvError;
        WriteCsv(Join(options.outDir, L"gpu-poc-frames.csv"), rows, csvError);
        return Fail(result, options.outDir, "WGC_CAPTURE_GATE_FAILED", detail, 13);
    }

    std::wstring csvError;
    if (!WriteCsv(Join(options.outDir, L"gpu-poc-frames.csv"), rows, csvError))
        PrintLine(L"POC-WARN: " + csvError);

    if (droppedNoSlot > 0)
        AddVerdict(result, "WGC_FRAME_DROPS_OBSERVED");
    if (encoderPass)
    {
        AddVerdict(result, "NVENC_H264_HARDWARE_ENCODED_1440x3120");
        if (!flushed) AddVerdict(result, "NVENC_FLUSH_INCOMPLETE");
        result["overall"] = "PASS_COMPONENT_WINDOW_WGC_NVENC";
    }
    else
    {
        AddVerdict(result, "ENCODER_BLOCKED");
        result["overall"] = "PARTIAL_ENCODER_BLOCKED";
    }
    result["finishedUtc"] = IsoNowUtc();

    if (!WriteResult(options.outDir, result, error))
    {
        std::cerr << "Could not write the POC result: " << Utf8(error) << "\n";
        return 1;
    }

    PrintLine(L"POC-CAPTURE framesArrived=" + std::to_wstring(arrived) +
        L" processed=" + std::to_wstring(processed) +
        L" wrongSize=" + std::to_wstring(wrongSize) +
        L" dropped=" + std::to_wstring(droppedNoSlot));
    PrintLine(L"POC-NVENC encoded=" + std::to_wstring(framesEncoded) +
        L" bytes=" + std::to_wstring(bytesWritten) +
        L" errors=" + std::to_wstring(encodeErrors));
    PrintLine(L"POC-RESULT " + Widen(result["overall"].get<std::string>()));
    return encoderPass ? 0 : 20;
}
} // namespace

int wmain(int argc, wchar_t** argv)
{
    SetConsoleOutputCP(CP_UTF8);
    Options options;
    std::wstring error;
    if (!ParseArgs(argc, argv, options, error))
    {
        std::cerr << Utf8(error) << "\n\n";
        Usage();
        return 2;
    }
    if (options.help)
    {
        Usage();
        return 0;
    }
    if (options.encoderSelfTest)
        return RunEncoderSelfTest(options);
    if (options.plan)
        return PrintPlan();
    return Run(options);
}
