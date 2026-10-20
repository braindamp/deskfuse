#pragma once

#include "common.h"

#include "nvEncodeAPI.h"

#include <string>
#include <vector>

struct EncoderConfig
{
    int width = 1440;
    int height = 3120;
    int fps = 60;
    int bitrate = 20000000;
};

/// Hardware H.264 encoder using NVENC's Direct3D 11 input-resource interop.
/// The registered inputs are the exact WGC capture-slot textures; there is no
/// CPU readback, scaling, or format conversion on the steady-state path.
class NvEncoder
{
public:
    NvEncoder();
    ~NvEncoder();

    NvEncoder(const NvEncoder&) = delete;
    NvEncoder& operator=(const NvEncoder&) = delete;

    bool Initialize(ID3D11Device* device, ID3D11Texture2D* const* slotTextures, int slotCount,
        const EncoderConfig& config, std::wstring& error);
    bool Encode(int slot, uint64_t timestamp, uint32_t frameIndex, uint32_t& bitstreamBytes,
        double& submitMs, double& lockMs, std::wstring& error,
        std::vector<uint8_t>& accessUnit, bool forceIdr = false);
    bool Flush(std::wstring& error);
    void Shutdown();

    bool Ready() const { return session_ != nullptr; }
    const wchar_t* Backend() const { return L"NVENC"; }
    const wchar_t* Preset() const { return L"P3 / ULTRA_LOW_LATENCY"; }
    uint32_t MaxApiVersion() const { return maxApiVersion_; }
    uint64_t FramesEncoded() const { return framesEncoded_; }
    uint64_t BytesWritten() const { return bytesWritten_; }
    const std::wstring& LastError() const { return lastError_; }

private:
    bool Fail(const std::wstring& error);
    std::wstring StatusText(const wchar_t* operation, int status) const;

    void* dll_ = nullptr;
    void* session_ = nullptr;
    NV_ENCODE_API_FUNCTION_LIST* functions_ = nullptr;
    std::vector<NV_ENC_REGISTERED_PTR> registered_;
    void* bitstream_ = nullptr;

    int width_ = 0;
    int height_ = 0;
    NV_ENC_CONFIG encodeConfig_{};
    uint32_t maxApiVersion_ = 0;
    uint64_t framesEncoded_ = 0;
    uint64_t framesSubmitted_ = 0;
    uint64_t bytesWritten_ = 0;
    uint32_t frameIndex_ = 0;
    std::wstring lastError_;
};
