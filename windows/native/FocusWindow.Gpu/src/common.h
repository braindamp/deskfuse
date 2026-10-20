#pragma once

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <map>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

std::string Utf8(const std::wstring& value);
std::wstring Widen(const std::string& value);
std::string IsoNowUtc();
std::string OsVersionString();
std::string FormatHresult(HRESULT hr);
std::string FormatWin32(DWORD error);

int64_t QpcNow();
int64_t QpcFrequency();
double QpcDeltaMs(int64_t from, int64_t to);

std::map<std::string, std::string> ReadKvFile(const std::wstring& path, std::wstring& error);
bool WriteTextFile(const std::wstring& path, const std::string& text, std::wstring& error);

struct Percentiles
{
    double min = 0;
    double p50 = 0;
    double p95 = 0;
    double p99 = 0;
    double max = 0;
    double avg = 0;
    size_t count = 0;
};

Percentiles ComputePercentiles(std::vector<double> values);
