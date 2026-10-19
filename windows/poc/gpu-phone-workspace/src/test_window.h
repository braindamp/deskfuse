#pragma once

#include <windows.h>
#include <cstdint>
#include <string>

constexpr int kRulerOriginX = 100;
constexpr int kRulerOriginY = 100;
constexpr int kRulerDips = 100;
constexpr int kRulerPx = 350;
constexpr int kCheckerX = 500;
constexpr int kCheckerY = 100;
constexpr int kCheckerSize = 64;
constexpr int kLineX = 600;
constexpr int kLineY = 100;
constexpr int kLineHeight = 64;
constexpr int kDotSize = 24;
constexpr int kColorProbeX = 100;
constexpr int kColorProbeY = 600;
constexpr int kColorProbeSize = 64;
constexpr int kColorProbeCount = 8;

struct RgbProbe
{
    uint8_t r;
    uint8_t g;
    uint8_t b;
};

// Channel-asymmetric swatches make an RGB/BGR swap or a color-matrix error
// visible; magenta and green alone cannot detect a red/blue swap.
constexpr RgbProbe kColorProbes[kColorProbeCount] = {
    {255, 0, 0},     // red
    {0, 255, 0},     // green
    {0, 0, 255},     // blue
    {0, 255, 255},   // cyan
    {255, 0, 255},   // magenta
    {255, 255, 0},   // yellow
    {255, 255, 255}, // white
    {0, 0, 0},       // black
};

struct TestWindowOptions
{
    int x = 0;
    int y = 0;
    int width = 1440;
    int height = 3120;
    int expectedDpi = 336;
    bool animate = true;
};

/// A borderless native PMv2 window whose physical pixel size is the capture
/// target. The ruler is drawn at 1:1 physical pixels; no DPI scaling is applied
/// to the ruler square itself.
class TestWindow
{
public:
    TestWindow() = default;
    ~TestWindow();
    TestWindow(const TestWindow&) = delete;
    TestWindow& operator=(const TestWindow&) = delete;

    bool Create(const TestWindowOptions& options, std::wstring& error);
    void Destroy();
    void PumpMessages();

    HWND Hwnd() const { return hwnd_; }
    int Dpi() const { return dpi_; }
    bool DpiChanged() const { return dpiChanged_; }
    bool Closed() const { return closed_; }
    RECT WindowRect() const { return windowRect_; }
    RECT ClientRect() const { return clientRect_; }
    uint64_t Ticks() const { return ticks_; }

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
    LRESULT Handle(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
    void Paint(HWND hwnd);
    void CreateChecker();
    void DestroyGdi();

    HWND hwnd_ = nullptr;
    int dpi_ = 0;
    int expectedDpi_ = 336;
    int targetX_ = 0;
    int targetY_ = 0;
    int targetWidth_ = 1440;
    int targetHeight_ = 3120;
    bool dpiChanged_ = false;
    bool closed_ = false;
    bool animate_ = true;
    RECT windowRect_{};
    RECT clientRect_{};
    uint64_t ticks_ = 0;
    int dotX_ = 0;
    int dotY_ = 0;
    HBRUSH background_ = nullptr;
    HBRUSH green_ = nullptr;
    HBRUSH white_ = nullptr;
    HBRUSH colorBrushes_[kColorProbeCount]{};
    HBITMAP checkerBitmap_ = nullptr;
    HDC checkerDc_ = nullptr;
    uint64_t paintCount_ = 0;
};
