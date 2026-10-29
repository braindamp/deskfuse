#include "nvenc_encoder.h"
#include "encoded_access_unit.h"

#include <filesystem>

namespace
{
using PFN_NVENC_CREATE_INSTANCE = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
using PFN_NVENC_GET_MAX_VERSION = NVENCSTATUS(NVENCAPI*)(uint32_t*);

// The driver returns (major << 4) | minor from
// NvEncodeAPIGetMaxSupportedVersion; NVENCAPI_VERSION itself is only used in
// structure version fields and has a different packing.
constexpr uint32_t kClientVersion = (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;
} // namespace

NvEncoder::NvEncoder() = default;

NvEncoder::~NvEncoder()
{
    Shutdown();
}

bool NvEncoder::Fail(const std::wstring& error)
{
    lastError_ = error;
    return false;
}

std::wstring NvEncoder::StatusText(const wchar_t* operation, int status) const
{
    std::wstring text = std::wstring(operation) + L" failed with NVENC status " +
        std::to_wstring(status);
    if (functions_ && session_ && functions_->nvEncGetLastErrorString)
    {
        const char* detail = functions_->nvEncGetLastErrorString(session_);
        if (detail && *detail)
            text += L": " + Widen(detail);
    }
    return text;
}

bool NvEncoder::Initialize(ID3D11Device* device, ID3D11Texture2D* const* slotTextures, int slotCount,
    const EncoderConfig& config, std::wstring& error)
{
    Shutdown();
    width_ = config.width;
    height_ = config.height;
    lastError_.clear();

    // The NVIDIA driver owns this DLL. Do not search an application's current
    // directory or PATH for code loaded into the capture/encoder owner.
    dll_ = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll_)
    {
        error = L"ENCODER_BLOCKED: nvEncodeAPI64.dll could not be loaded (";
        error += Widen(FormatWin32(GetLastError())) + L").";
        return Fail(error);
    }

    auto createInstance = reinterpret_cast<PFN_NVENC_CREATE_INSTANCE>(
        GetProcAddress(static_cast<HMODULE>(dll_), "NvEncodeAPICreateInstance"));
    auto getMaxVersion = reinterpret_cast<PFN_NVENC_GET_MAX_VERSION>(
        GetProcAddress(static_cast<HMODULE>(dll_), "NvEncodeAPIGetMaxSupportedVersion"));
    if (!createInstance || !getMaxVersion)
    {
        error = L"ENCODER_BLOCKED: nvEncodeAPI64.dll is missing the NVENC entry points.";
        Shutdown();
        return Fail(error);
    }

    NVENCSTATUS status = getMaxVersion(&maxApiVersion_);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: NvEncodeAPIGetMaxSupportedVersion failed (" +
            std::to_wstring(static_cast<int>(status)) + L").";
        Shutdown();
        return Fail(error);
    }
    if (maxApiVersion_ < kClientVersion)
    {
        error = L"ENCODER_BLOCKED: the installed NVIDIA driver supports NVENC API " +
            std::to_wstring(maxApiVersion_ >> 4) + L"." +
            std::to_wstring(maxApiVersion_ & 0xF) + L", but this client requires " +
            std::to_wstring(kClientVersion >> 4) + L"." +
            std::to_wstring(kClientVersion & 0xF) + L".";
        Shutdown();
        return Fail(error);
    }

    functions_ = new NV_ENCODE_API_FUNCTION_LIST{};
    functions_->version = NV_ENCODE_API_FUNCTION_LIST_VER;
    status = createInstance(functions_);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: NvEncodeAPICreateInstance failed with status " +
            std::to_wstring(static_cast<int>(status)) + L".";
        Shutdown();
        return Fail(error);
    }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS openParams{};
    openParams.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    openParams.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    openParams.device = device;
    openParams.apiVersion = NVENCAPI_VERSION;
    status = functions_->nvEncOpenEncodeSessionEx(&openParams, &session_);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncOpenEncodeSessionEx", status);
        Shutdown();
        return Fail(error);
    }

    uint32_t guidCount = 0;
    status = functions_->nvEncGetEncodeGUIDCount(session_, &guidCount);
    if (status != NV_ENC_SUCCESS || guidCount == 0)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncGetEncodeGUIDCount", status);
        Shutdown();
        return Fail(error);
    }
    std::vector<GUID> codecs(guidCount);
    status = functions_->nvEncGetEncodeGUIDs(session_, codecs.data(), guidCount, &guidCount);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncGetEncodeGUIDs", status);
        Shutdown();
        return Fail(error);
    }
    const bool hasH264 = std::any_of(codecs.begin(), codecs.begin() + guidCount,
        [](const GUID& guid) { return IsEqualGUID(guid, NV_ENC_CODEC_H264_GUID) != FALSE; });
    if (!hasH264)
    {
        error = L"ENCODER_BLOCKED: the NVENC session does not advertise H.264.";
        Shutdown();
        return Fail(error);
    }

    uint32_t formatCount = 0;
    status = functions_->nvEncGetInputFormatCount(session_, NV_ENC_CODEC_H264_GUID, &formatCount);
    if (status != NV_ENC_SUCCESS || formatCount == 0)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncGetInputFormatCount", status);
        Shutdown();
        return Fail(error);
    }
    std::vector<NV_ENC_BUFFER_FORMAT> formats(formatCount);
    status = functions_->nvEncGetInputFormats(session_, NV_ENC_CODEC_H264_GUID, formats.data(),
        formatCount, &formatCount);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncGetInputFormats", status);
        Shutdown();
        return Fail(error);
    }
    const bool hasArgb = std::find(formats.begin(), formats.begin() + formatCount,
        NV_ENC_BUFFER_FORMAT_ARGB) != formats.begin() + formatCount;
    if (!hasArgb)
    {
        error = L"ENCODER_BLOCKED: H.264 ARGB (B8G8R8A8) input is not supported by this NVENC session.";
        Shutdown();
        return Fail(error);
    }

    NV_ENC_PRESET_CONFIG preset{};
    preset.version = NV_ENC_PRESET_CONFIG_VER;
    preset.presetCfg.version = NV_ENC_CONFIG_VER;
    if (functions_->nvEncGetEncodePresetConfigEx)
    {
        status = functions_->nvEncGetEncodePresetConfigEx(session_, NV_ENC_CODEC_H264_GUID,
            NV_ENC_PRESET_P3_GUID, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, &preset);
    }
    else
    {
        status = functions_->nvEncGetEncodePresetConfig(session_, NV_ENC_CODEC_H264_GUID,
            NV_ENC_PRESET_P3_GUID, &preset);
    }
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncGetEncodePresetConfigEx", status);
        Shutdown();
        return Fail(error);
    }

    encodeConfig_ = preset.presetCfg;
    encodeConfig_.version = NV_ENC_CONFIG_VER;
    encodeConfig_.profileGUID = NV_ENC_H264_PROFILE_HIGH_GUID;
    // Avoid periodic full-screen keyframes on an ordered authenticated stream.
    // Connect/reconfigure/decoder recovery explicitly requests SPS/PPS + IDR.
    encodeConfig_.gopLength = NVENC_INFINITE_GOPLENGTH;
    encodeConfig_.frameIntervalP = 1;
    // Preserve the configured scene bitrate budget without paying a constant
    // transport rate for sparse desktop updates. No lookahead or B-frame queue.
    encodeConfig_.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR;
    encodeConfig_.rcParams.averageBitRate = static_cast<uint32_t>(config.bitrate);
    encodeConfig_.rcParams.maxBitRate = static_cast<uint32_t>(config.bitrate);
    encodeConfig_.rcParams.vbvBufferSize = static_cast<uint32_t>(config.bitrate);
    encodeConfig_.rcParams.vbvInitialDelay = static_cast<uint32_t>(config.bitrate);
    encodeConfig_.rcParams.enableLookahead = 0;
    encodeConfig_.rcParams.zeroReorderDelay = 1;
    encodeConfig_.rcParams.enableAQ = 0;
    encodeConfig_.rcParams.enableTemporalAQ = 0;
    encodeConfig_.encodeCodecConfig.h264Config.idrPeriod = NVENC_INFINITE_GOPLENGTH;
    encodeConfig_.encodeCodecConfig.h264Config.enableFillerDataInsertion = 0;
    encodeConfig_.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    encodeConfig_.encodeCodecConfig.h264Config.level = NV_ENC_LEVEL_AUTOSELECT;
    encodeConfig_.encodeCodecConfig.h264Config.chromaFormatIDC = 1;
    encodeConfig_.encodeCodecConfig.h264Config.sliceMode = 0;
    encodeConfig_.encodeCodecConfig.h264Config.sliceModeData = 0;
    auto& vui = encodeConfig_.encodeCodecConfig.h264Config.h264VUIParameters;
    vui.videoSignalTypePresentFlag = 1;
    vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_COMPONENT;
    vui.videoFullRangeFlag = 1;
    vui.colourDescriptionPresentFlag = 1;
    vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
    vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
    vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
    vui.timingInfoPresentFlag = 1;
    vui.numUnitInTicks = 1;
    vui.timeScale = static_cast<uint32_t>(config.fps) * 2;

    NV_ENC_INITIALIZE_PARAMS init{};
    init.version = NV_ENC_INITIALIZE_PARAMS_VER;
    init.encodeGUID = NV_ENC_CODEC_H264_GUID;
    init.presetGUID = NV_ENC_PRESET_P3_GUID;
    init.encodeWidth = static_cast<uint32_t>(width_);
    init.encodeHeight = static_cast<uint32_t>(height_);
    init.darWidth = static_cast<uint32_t>(width_);
    init.darHeight = static_cast<uint32_t>(height_);
    init.frameRateNum = static_cast<uint32_t>(config.fps);
    init.frameRateDen = 1;
    init.enableEncodeAsync = 0;
    init.enablePTD = 1;
    init.encodeConfig = &encodeConfig_;
    init.maxEncodeWidth = static_cast<uint32_t>(width_);
    init.maxEncodeHeight = static_cast<uint32_t>(height_);
    init.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
    init.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;

    status = functions_->nvEncInitializeEncoder(session_, &init);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncInitializeEncoder", status);
        Shutdown();
        return Fail(error);
    }

    for (int i = 0; i < slotCount; ++i)
    {
        if (!slotTextures[i])
        {
            error = L"Encoder slot " + std::to_wstring(i) + L" has no texture.";
            Shutdown();
            return Fail(error);
        }
        NV_ENC_REGISTER_RESOURCE registration{};
        registration.version = NV_ENC_REGISTER_RESOURCE_VER;
        registration.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        registration.width = static_cast<uint32_t>(width_);
        registration.height = static_cast<uint32_t>(height_);
        registration.pitch = 0;
        registration.subResourceIndex = 0;
        registration.resourceToRegister = slotTextures[i];
        registration.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
        registration.bufferUsage = NV_ENC_INPUT_IMAGE;
        status = functions_->nvEncRegisterResource(session_, &registration);
        if (status != NV_ENC_SUCCESS)
        {
            error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncRegisterResource", status) +
                L" (slot " + std::to_wstring(i) + L").";
            Shutdown();
            return Fail(error);
        }
        registered_.push_back(registration.registeredResource);
    }

    NV_ENC_CREATE_BITSTREAM_BUFFER createBitstream{};
    createBitstream.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
    status = functions_->nvEncCreateBitstreamBuffer(session_, &createBitstream);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncCreateBitstreamBuffer", status);
        Shutdown();
        return Fail(error);
    }
    bitstream_ = createBitstream.bitstreamBuffer;

    // Encoded H.264 belongs to the live session; never write a POC file.
    return true;
}

bool NvEncoder::Encode(int slot, uint64_t timestamp, uint32_t frameIndex, uint32_t& bitstreamBytes,
    double& submitMs, double& lockMs, std::wstring& error,
    uint8_t* output, uint32_t capacity, std::vector<uint8_t>& retry, bool& isIdr, bool forceIdr)
{
    retry.clear();
    isIdr = false;
    bitstreamBytes = 0;
    submitMs = 0;
    lockMs = 0;
    if (!session_ || !functions_)
    {
        error = L"Encoder is not initialized.";
        return Fail(error);
    }
    if (slot < 0 || slot >= static_cast<int>(registered_.size()))
    {
        error = L"Encoder slot is out of range.";
        return Fail(error);
    }

    const int64_t submitStart = QpcNow();
    NV_ENC_MAP_INPUT_RESOURCE map{};
    map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    map.registeredResource = registered_[static_cast<size_t>(slot)];
    NVENCSTATUS status = functions_->nvEncMapInputResource(session_, &map);
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncMapInputResource", status);
        return Fail(error);
    }
    struct MappedInputScope {
        NV_ENCODE_API_FUNCTION_LIST* functions;
        void* session;
        NV_ENC_INPUT_PTR mapped;
        ~MappedInputScope() { functions->nvEncUnmapInputResource(session, mapped); }
    } mappedInput{functions_, session_, map.mappedResource};

    NV_ENC_PIC_PARAMS picture{};
    picture.version = NV_ENC_PIC_PARAMS_VER;
    picture.inputWidth = static_cast<uint32_t>(width_);
    picture.inputHeight = static_cast<uint32_t>(height_);
    picture.inputPitch = static_cast<uint32_t>(width_);
    picture.encodePicFlags = forceIdr ?
        NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS : 0;
    picture.frameIdx = frameIndex;
    picture.inputTimeStamp = timestamp;
    picture.inputDuration = 1;
    picture.inputBuffer = map.mappedResource;
    picture.outputBitstream = bitstream_;
    picture.bufferFmt = map.mappedBufferFmt;
    picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;

    status = functions_->nvEncEncodePicture(session_, &picture);
    submitMs = QpcDeltaMs(submitStart, QpcNow());
    submitMaxUs_ = std::max(submitMaxUs_, static_cast<uint64_t>(submitMs * 1000));
    if (status != NV_ENC_SUCCESS)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncEncodePicture", status);
        return Fail(error);
    }
    ++framesSubmitted_;

    const int64_t lockStart = QpcNow();
    NV_ENC_LOCK_BITSTREAM lock{};
    lock.version = NV_ENC_LOCK_BITSTREAM_VER;
    lock.outputBitstream = bitstream_;
    lock.doNotWait = 0;
    status = functions_->nvEncLockBitstream(session_, &lock);
    lockMs = QpcDeltaMs(lockStart, QpcNow());
    lockMaxUs_ = std::max(lockMaxUs_, static_cast<uint64_t>(lockMs * 1000));
    if (status == NV_ENC_SUCCESS)
    {
        struct BitstreamScope {
            NV_ENCODE_API_FUNCTION_LIST* functions;
            void* session;
            NV_ENC_OUTPUT_PTR bitstream;
            ~BitstreamScope() { functions->nvEncUnlockBitstream(session, bitstream); }
        } lockedBitstream{functions_, session_, bitstream_};
        if (lock.bitstreamSizeInBytes > 0 && lock.bitstreamBufferPtr)
        {
            const auto* ptr = static_cast<const uint8_t*>(lock.bitstreamBufferPtr);
            if (lock.bitstreamSizeInBytes > 8u * 1024u * 1024u)
            {
                error = L"Encoded access unit exceeds budget";
                return Fail(error);
            }
            // This synchronous no-reorder profile may never attribute a
            // delayed/different output to the newest captured frame.
            if (lock.outputTimeStamp != timestamp)
            {
                error = L"NVENC output timestamp differs from the submitted source frame";
                return Fail(error);
            }
            const std::span<const uint8_t> encoded(ptr, lock.bitstreamSizeInBytes);
            const auto inspectStarted = QpcNow();
            const auto kind = lock.pictureType == NV_ENC_PIC_TYPE_IDR ? H264PictureKind::Idr :
                (lock.pictureType == NV_ENC_PIC_TYPE_P || lock.pictureType == NV_ENC_PIC_TYPE_I) ?
                    H264PictureKind::NonIdr : H264PictureKind::Unknown;
            if (!ClassifyH264Output(encoded, kind, forceIdr, isIdr))
            {
                error = L"NVENC did not provide SPS/PPS/IDR for recovery";
                return Fail(error);
            }
            CopyEncodedAccessUnit(encoded, output, capacity, retry);
            inspectCopyMaxUs_ = std::max(inspectCopyMaxUs_,
                static_cast<uint64_t>(QpcDeltaMs(inspectStarted, QpcNow()) * 1000));
            bitstreamBytes = lock.bitstreamSizeInBytes;
            bytesWritten_ += bitstreamBytes;
            ++framesEncoded_;
        }
    }
    else if (status != NV_ENC_ERR_NEED_MORE_INPUT)
    {
        error = L"ENCODER_BLOCKED: " + StatusText(L"NvEncLockBitstream", status);
        return Fail(error);
    }
    return true;
}

bool NvEncoder::Flush(std::wstring& error)
{
    // Synchronous H.264: no file sink to drain. Production teardown
    // always stops the owning pipeline before closing NVENC.
    if (framesSubmitted_ > framesEncoded_)
    {
        error = L"NVENC still has pending frames.";
        return false;
    }
    return true;
}

void NvEncoder::Shutdown()
{
    if (session_ && functions_)
    {
        for (auto resource : registered_)
        {
            if (resource && functions_->nvEncUnregisterResource)
                functions_->nvEncUnregisterResource(session_, resource);
        }
        if (bitstream_ && functions_->nvEncDestroyBitstreamBuffer)
            functions_->nvEncDestroyBitstreamBuffer(session_, bitstream_);
        if (functions_->nvEncDestroyEncoder)
            functions_->nvEncDestroyEncoder(session_);
    }
    registered_.clear();
    bitstream_ = nullptr;
    session_ = nullptr;
    delete functions_;
    functions_ = nullptr;
    if (dll_)
    {
        FreeLibrary(static_cast<HMODULE>(dll_));
        dll_ = nullptr;
    }
}
