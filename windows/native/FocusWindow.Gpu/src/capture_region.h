#pragma once

struct CaptureRegion
{
    int left = 0;
    int top = 0;
    int width = 0;
    int height = 0;
};

// Some Windows 10 HWND captures include GetWindowRect's invisible resize
// borders. Copy only the measured DWM visible frame, without scaling pixels.
inline bool ResolveCaptureRegion(int contentWidth, int contentHeight,
    int width, int height, int outerWidth, int outerHeight,
    int visibleLeft, int visibleTop, CaptureRegion& region) noexcept
{
    if (width <= 0 || height <= 0) return false;
    if (contentWidth == width && contentHeight == height)
    {
        region = {0, 0, width, height};
        return true;
    }
    if (contentWidth != outerWidth || contentHeight != outerHeight ||
        visibleLeft < 0 || visibleTop < 0 ||
        visibleLeft > contentWidth - width || visibleTop > contentHeight - height)
        return false;
    region = {visibleLeft, visibleTop, width, height};
    return true;
}
