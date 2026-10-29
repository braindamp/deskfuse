#pragma once

#include <cstdint>
#include <cstring>
#include <span>
#include <vector>

// One complete AU is copied directly out of the locked encoder bitstream into
// the caller's pinned destination. Only an undersized/query call retains a
// native copy for the ABI's required retry; no prefix or reference is dropped.
inline void CopyEncodedAccessUnit(std::span<const uint8_t> source,
    uint8_t* destination, uint32_t capacity, std::vector<uint8_t>& retry)
{
    retry.clear();
    if (destination && capacity >= source.size())
        std::memcpy(destination, source.data(), source.size());
    else
        retry.assign(source.begin(), source.end());
}

enum H264Headers : uint8_t { H264Idr = 1, H264Sps = 2, H264Pps = 4 };

inline uint8_t InspectH264Headers(std::span<const uint8_t> bytes)
{
    uint8_t headers = 0;
    for (size_t i = 0; i + 3 < bytes.size(); ++i)
    {
        if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
        const size_t prefix = bytes[i + 2] == 1 ? 3 :
            bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0;
        if (!prefix || i + prefix >= bytes.size()) continue;
        switch (bytes[i + prefix] & 31)
        {
            case 5: headers |= H264Idr; break;
            case 7: headers |= H264Sps; break;
            case 8: headers |= H264Pps; break;
        }
        if (headers == (H264Idr | H264Sps | H264Pps)) break;
        i += prefix; // the NAL header cannot itself begin another start code
    }
    return headers;
}

enum class H264PictureKind { Unknown, NonIdr, Idr };

inline bool ClassifyH264Output(std::span<const uint8_t> bytes, H264PictureKind kind,
    bool forcedRecovery, bool& isIdr)
{
    isIdr = false;
    if (kind == H264PictureKind::NonIdr && !forcedRecovery) return true;
    const auto headers = InspectH264Headers(bytes);
    isIdr = (headers & H264Idr) != 0;
    if ((kind == H264PictureKind::Idr && !isIdr) ||
        (forcedRecovery && (kind == H264PictureKind::NonIdr || !isIdr))) return false;
    return !isIdr || (headers & (H264Sps | H264Pps)) == (H264Sps | H264Pps);
}
