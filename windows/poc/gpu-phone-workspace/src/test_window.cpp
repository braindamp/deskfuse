#include "test_window.h"

#include <algorithm>
#include <sstream>

namespace
{
constexpr wchar_t kClassName[] = L"DeskFuseGpuWorkspacePocWindow";

std::wstring RectText(const RECT& rect)
{
    std::wstringstream text;
    text << rect.left << L"," << rect.top << L" " << (rect.right - rect.left) << L"x"
         << (rect.bottom - rect.top);
    return text.str();
}
} // namespace

TestWindow::~TestWindow()
{
    Destroy();
}

bool TestWindow::Create(const TestWindowOptions& options, std::wstring& error)
{
    expectedDpi_ = options.expectedDpi;
    animate_ = options.animate;
    targetX_ = options.x;
    targetY_ = options.y;
    targetWidth_ = options.width;
    targetHeight_ = options.height;

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.style = CS_HREDRAW | CS_VREDRAW;
    windowClass.lpfnWndProc = &TestWindow::WndProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = nullptr;
    windowClass.lpszClassName = kClassName;
    if (!RegisterClassExW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
    {
        error = L"RegisterClassExW failed: " + std::to_wstring(GetLastError());
        return false;
    }

    background_ = CreateSolidBrush(RGB(255, 0, 255));
    green_ = CreateSolidBrush(RGB(0, 255, 0));
    white_ = CreateSolidBrush(RGB(255, 255, 255));
    bool colorBrushesOk = true;
    for (int i = 0; i < kColorProbeCount; ++i)
    {
        colorBrushes_[i] = CreateSolidBrush(
            RGB(kColorProbes[i].r, kColorProbes[i].g, kColorProbes[i].b));
        if (!colorBrushes_[i]) colorBrushesOk = false;
    }
    CreateChecker();
    if (!background_ || !green_ || !white_ || !colorBrushesOk || !checkerBitmap_ || !checkerDc_)
    {
        error = L"Could not create the GDI test pattern resources.";
        DestroyGdi();
        return false;
    }

    dotX_ = 100;
    dotY_ = options.height - 80;

    hwnd_ = CreateWindowExW(
        0,
        kClassName,
        L"DeskFuse GPU POC 1440x3120",
        WS_POPUP | WS_VISIBLE,
        options.x, options.y, options.width, options.height,
        nullptr, nullptr, GetModuleHandleW(nullptr), this);
    if (!hwnd_)
    {
        error = L"CreateWindowExW failed: " + std::to_wstring(GetLastError());
        DestroyGdi();
        return false;
    }

    // WS_POPUP has no nonclient area; force the exact physical rectangle once
    // more after creation so DPI suggested rectangles cannot change it.
    SetWindowPos(hwnd_, HWND_TOP, options.x, options.y, options.width, options.height,
        SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd_);

    GetWindowRect(hwnd_, &windowRect_);
    GetClientRect(hwnd_, &clientRect_);
    dpi_ = GetDpiForWindow(hwnd_);

    if (windowRect_.right - windowRect_.left != options.width ||
        windowRect_.bottom - windowRect_.top != options.height)
    {
        error = L"Window rectangle is not exact: " + RectText(windowRect_);
        Destroy();
        return false;
    }
    if (clientRect_.right != options.width || clientRect_.bottom != options.height)
    {
        error = L"Client rectangle is not exact: " + RectText(clientRect_);
        Destroy();
        return false;
    }

    if (animate_)
    {
        if (!SetTimer(hwnd_, 1, 16, nullptr))
        {
            error = L"SetTimer failed: " + std::to_wstring(GetLastError());
            Destroy();
            return false;
        }
    }

    SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
    return true;
}

void TestWindow::Destroy()
{
    if (hwnd_)
    {
        KillTimer(hwnd_, 1);
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    DestroyGdi();
    SetThreadExecutionState(ES_CONTINUOUS);
}

void TestWindow::DestroyGdi()
{
    if (checkerDc_)
    {
        DeleteDC(checkerDc_);
        checkerDc_ = nullptr;
    }
    if (checkerBitmap_)
    {
        DeleteObject(checkerBitmap_);
        checkerBitmap_ = nullptr;
    }
    if (background_) { DeleteObject(background_); background_ = nullptr; }
    if (green_) { DeleteObject(green_); green_ = nullptr; }
    if (white_) { DeleteObject(white_); white_ = nullptr; }
    for (auto& brush : colorBrushes_)
    {
        if (brush) { DeleteObject(brush); brush = nullptr; }
    }
}

void TestWindow::CreateChecker()
{
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = kCheckerSize;
    info.bmiHeader.biHeight = -kCheckerSize; // top-down
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    checkerBitmap_ = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!checkerBitmap_ || !bits) return;
    auto* pixels = static_cast<uint32_t*>(bits);
    for (int y = 0; y < kCheckerSize; ++y)
    {
        for (int x = 0; x < kCheckerSize; ++x)
        {
            // BGRA: white = 0xFFFFFFFF, black = 0xFF000000.
            pixels[y * kCheckerSize + x] = ((x + y) & 1) ? 0xFFFFFFFFu : 0xFF000000u;
        }
    }
    checkerDc_ = CreateCompatibleDC(nullptr);
    if (checkerDc_) SelectObject(checkerDc_, checkerBitmap_);
}

void TestWindow::Paint(HWND hwnd)
{
    PAINTSTRUCT paint{};
    HDC dc = BeginPaint(hwnd, &paint);
    RECT client{};
    GetClientRect(hwnd, &client);

    FillRect(dc, &client, background_);

    RECT ruler{kRulerOriginX, kRulerOriginY, kRulerOriginX + kRulerPx, kRulerOriginY + kRulerPx};
    FillRect(dc, &ruler, green_);

    if (checkerDc_)
        BitBlt(dc, kCheckerX, kCheckerY, kCheckerSize, kCheckerSize, checkerDc_, 0, 0, SRCCOPY);

    RECT line{kLineX, kLineY, kLineX + 1, kLineY + kLineHeight};
    FillRect(dc, &line, white_);

    for (int i = 0; i < kColorProbeCount; ++i)
    {
        RECT probe{kColorProbeX + i * kColorProbeSize, kColorProbeY,
            kColorProbeX + (i + 1) * kColorProbeSize, kColorProbeY + kColorProbeSize};
        FillRect(dc, &probe, colorBrushes_[i]);
    }

    RECT dot{dotX_, dotY_, dotX_ + kDotSize, dotY_ + kDotSize};
    if (dot.left >= 0 && dot.top >= 0 && dot.right <= client.right && dot.bottom <= client.bottom)
        FillRect(dc, &dot, white_);

    EndPaint(hwnd, &paint);
    ++paintCount_;
}

void TestWindow::PumpMessages()
{
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
    {
        if (message.message == WM_QUIT)
        {
            closed_ = true;
            continue;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

LRESULT CALLBACK TestWindow::WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    TestWindow* self = reinterpret_cast<TestWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = reinterpret_cast<TestWindow*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    if (self)
        return self->Handle(hwnd, message, wParam, lParam);
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT TestWindow::Handle(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case WM_PAINT:
        Paint(hwnd);
        return 0;

    case WM_ERASEBKGND:
        return 1;

    case WM_TIMER:
        if (wParam == 1 && animate_)
        {
            RECT previous{dotX_, dotY_, dotX_ + kDotSize, dotY_ + kDotSize};
            ++ticks_;
            if (clientRect_.right > 200)
                dotX_ = 100 + static_cast<int>((ticks_ * 7) % static_cast<uint64_t>(clientRect_.right - 100 - kDotSize));
            RECT current{dotX_, dotY_, dotX_ + kDotSize, dotY_ + kDotSize};
            InvalidateRect(hwnd, &previous, FALSE);
            InvalidateRect(hwnd, &current, FALSE);
            UpdateWindow(hwnd);
        }
        return 0;

    case WM_DPICHANGED:
        dpi_ = HIWORD(wParam);
        dpiChanged_ = dpi_ != expectedDpi_;
        // Keep the exact physical rectangle instead of accepting the suggested
        // DPI rectangle. The capture source must remain 1440x3120.
        SetWindowPos(hwnd, HWND_TOP, targetX_, targetY_, targetWidth_, targetHeight_,
            SWP_NOACTIVATE | SWP_NOZORDER);
        GetWindowRect(hwnd, &windowRect_);
        GetClientRect(hwnd, &clientRect_);
        return 0;

    case WM_CLOSE:
        closed_ = true;
        return 0;

    case WM_DESTROY:
        return 0;

    default:
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
