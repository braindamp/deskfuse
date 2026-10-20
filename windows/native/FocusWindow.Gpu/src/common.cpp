#include "common.h"

#include <winternl.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>

std::string Utf8(const std::wstring& value)
{
    if (value.empty()) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        out.data(), needed, nullptr, nullptr);
    return out;
}

std::wstring Widen(const std::string& value)
{
    if (value.empty()) return {};
    const int needed = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0);
    if (needed <= 0) return {};
    std::wstring out(static_cast<size_t>(needed), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        out.data(), needed);
    return out;
}

std::string IsoNowUtc()
{
    SYSTEMTIME st{};
    GetSystemTime(&st);
    char buffer[64]{};
    sprintf_s(buffer, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return buffer;
}

std::string OsVersionString()
{
    // GetVersionEx is manifested/lying on Windows 10; RtlGetVersion returns the real build.
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    RTL_OSVERSIONINFOW info{};
    info.dwOSVersionInfoSize = sizeof(info);
    if (auto ntdll = GetModuleHandleW(L"ntdll.dll"))
    {
        auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion"));
        if (fn && fn(&info) == 0)
        {
            char buffer[128]{};
            sprintf_s(buffer, "%lu.%lu.%lu", info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber);
            return buffer;
        }
    }
    return "unknown";
}

static std::string FormatMessageText(DWORD code)
{
    wchar_t* text = nullptr;
    const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
        FORMAT_MESSAGE_IGNORE_INSERTS;
    const DWORD chars = FormatMessageW(flags, nullptr, code, 0,
        reinterpret_cast<LPWSTR>(&text), 0, nullptr);
    std::wstring message = chars && text ? std::wstring(text, chars) : L"";
    if (text) LocalFree(text);
    while (!message.empty() && (message.back() == L'\r' || message.back() == L'\n' || message.back() == L' '))
        message.pop_back();
    return Utf8(message);
}

std::string FormatHresult(HRESULT hr)
{
    char buffer[32]{};
    sprintf_s(buffer, "0x%08X", static_cast<unsigned>(hr));
    return buffer;
}

std::string FormatWin32(DWORD error)
{
    char buffer[32]{};
    sprintf_s(buffer, "%lu", error);
    const auto text = FormatMessageText(error);
    return text.empty() ? buffer : std::string(buffer) + " (" + text + ")";
}

int64_t QpcNow()
{
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

int64_t QpcFrequency()
{
    static const int64_t frequency = [] {
        LARGE_INTEGER value{};
        QueryPerformanceFrequency(&value);
        return value.QuadPart;
    }();
    return frequency;
}

double QpcDeltaMs(int64_t from, int64_t to)
{
    return static_cast<double>(to - from) * 1000.0 / static_cast<double>(QpcFrequency());
}

std::map<std::string, std::string> ReadKvFile(const std::wstring& path, std::wstring& error)
{
    std::ifstream in(path, std::ios::binary);
    if (!in)
    {
        error = L"Could not open monitor handoff: " + path;
        return {};
    }
    std::map<std::string, std::string> values;
    std::string line;
    while (std::getline(in, line))
    {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        const auto equals = line.find('=');
        if (equals == std::string::npos) continue;
        std::string key = line.substr(0, equals);
        std::string value = line.substr(equals + 1);
        while (!key.empty() && (key.back() == ' ' || key.back() == '\t')) key.pop_back();
        while (!value.empty() && (value.front() == ' ' || value.front() == '\t')) value.erase(value.begin());
        values[key] = value;
    }
    return values;
}

bool WriteTextFile(const std::wstring& path, const std::string& text, std::wstring& error)
{
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out)
    {
        error = L"Could not write " + path;
        return false;
    }
    out.write(text.data(), static_cast<std::streamsize>(text.size()));
    if (!out)
    {
        error = L"Write failed for " + path;
        return false;
    }
    return true;
}

Percentiles ComputePercentiles(std::vector<double> values)
{
    Percentiles result;
    if (values.empty()) return result;
    std::sort(values.begin(), values.end());
    result.count = values.size();
    result.min = values.front();
    result.max = values.back();
    double sum = 0;
    for (double v : values) sum += v;
    result.avg = sum / static_cast<double>(values.size());
    auto at = [&](double p) {
        const double index = p * static_cast<double>(values.size() - 1);
        const auto lower = static_cast<size_t>(std::floor(index));
        const auto upper = static_cast<size_t>(std::ceil(index));
        if (lower == upper) return values[lower];
        const double t = index - static_cast<double>(lower);
        return values[lower] * (1.0 - t) + values[upper] * t;
    };
    result.p50 = at(0.50);
    result.p95 = at(0.95);
    result.p99 = at(0.99);
    return result;
}
