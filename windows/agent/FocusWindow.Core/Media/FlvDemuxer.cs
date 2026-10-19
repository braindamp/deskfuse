using System.Buffers.Binary;
using System.Runtime.CompilerServices;
namespace FocusWindow.Core.Media;
public readonly record struct EncodedAccessUnit(byte[] Data, bool Keyframe);
/// <summary>
/// Reads an FLV byte stream (as produced by ffmpeg to a pipe) and yields H.264 access units
/// in Annex-B form, which is what the Android client's MediaCodec path consumes.
///
/// WHY FLV AND NOT RAW ANNEX-B: in a raw Annex-B stream a NAL unit only ends where the NEXT
/// start code begins, so the last NAL of the newest frame is never terminated until another
/// frame arrives. With a desktop that goes quiet (the common case, FR-REF-02) the final
/// update would sit in the pipe and the phone would show a stale picture. FLV tags carry an
/// explicit length and ffmpeg flushes each one, so every access unit is complete the moment
/// it is written.
///
/// Conversion performed per video tag:
///   * AVCDecoderConfigurationRecord (sequence header)  -> remember SPS / PPS + NAL length size
///   * NALU tag: [len][NAL]... -> 00 00 00 01 NAL ...
///   * keyframes get SPS+PPS prepended, so every IDR is independently decodable (the client
///     opens a new target version with a keyframe and configures MediaCodec in-band).
/// </summary>
public static class FlvDemuxer
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };
    public static async IAsyncEnumerable<EncodedAccessUnit> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var header = new byte[9 + 4]; // FLV header + PreviousTagSize0
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
            throw new InvalidDataException("not an FLV stream");
        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        if (dataOffset > 9)
        {
            // Skip any extension bytes between the 9-byte header and the first tag.
            var skip = new byte[dataOffset - 9];
            await stream.ReadExactlyAsync(skip, ct).ConfigureAwait(false);
        }
        byte[]? parameterSets = null; // SPS+PPS in Annex-B
        var nalLengthSize = 4;
        var tagHeader = new byte[11];
        var trailer = new byte[4];
        while (true)
        {
            // EOF is a CLEAN end only at a tag boundary (zero bytes of the next tag). EOF in the
            // middle of a tag means the encoder died mid-write, which must surface as an error:
            // treating it as a normal exit would hide a crashed ffmpeg.
            var first = await stream.ReadAsync(tagHeader.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (first == 0) yield break;
            await stream.ReadExactlyAsync(tagHeader.AsMemory(1), ct).ConfigureAwait(false);
            var type = tagHeader[0] & 0x1F;
            var dataSize = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
            if (dataSize < 0 || dataSize > (1 << 26))
                throw new InvalidDataException($"implausible FLV tag size {dataSize}");
            var data = new byte[dataSize];
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            await stream.ReadExactlyAsync(trailer, ct).ConfigureAwait(false);
            if (type != 9 || data.Length < 5) continue; // only video tags matter
            var frameType = data[0] >> 4;
            var codecId = data[0] & 0x0F;
            if (codecId != 7) continue; // AVC only
            var packetType = data[1];
            if (packetType == 0)
            {
                (parameterSets, nalLengthSize) = ParseAvcc(data.AsSpan(5));
                continue;
            }
            if (packetType != 1) continue; // 2 = end of sequence
            var keyframe = frameType == 1;
            var au = ConvertNalus(data.AsSpan(5), nalLengthSize, keyframe ? parameterSets : null);
            yield return new EncodedAccessUnit(au, keyframe);
        }
    }
    /// <summary>Parses an AVCDecoderConfigurationRecord into Annex-B SPS+PPS and the NAL length size.</summary>
    public static (byte[] annexB, int nalLengthSize) ParseAvcc(ReadOnlySpan<byte> avcc)
    {
        if (avcc.Length < 7 || avcc[0] != 1)
            throw new InvalidDataException("bad AVCDecoderConfigurationRecord");
        var lengthSize = (avcc[4] & 0x03) + 1;
        var pos = 5;
        using var ms = new MemoryStream();
        var spsCount = avcc[pos++] & 0x1F;
        for (var i = 0; i < spsCount; i++) WriteParameterSet(ms, avcc, ref pos);
        var ppsCount = avcc[pos++];
        for (var i = 0; i < ppsCount; i++) WriteParameterSet(ms, avcc, ref pos);
        return (ms.ToArray(), lengthSize);
        static void WriteParameterSet(MemoryStream ms, ReadOnlySpan<byte> src, ref int p)
        {
            var len = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(p, 2));
            p += 2;
            if (p + len > src.Length) throw new InvalidDataException("truncated parameter set");
            ms.Write(StartCode);
            ms.Write(src.Slice(p, len));
            p += len;
        }
    }
    public static byte[] ConvertNalus(ReadOnlySpan<byte> payload, int nalLengthSize, byte[]? prefix)
    {
        using var ms = new MemoryStream(payload.Length + 64);
        if (prefix is { Length: > 0 }) ms.Write(prefix);
        var pos = 0;
        while (pos + nalLengthSize <= payload.Length)
        {
            var len = 0;
            for (var i = 0; i < nalLengthSize; i++) len = (len << 8) | payload[pos + i];
            pos += nalLengthSize;
            if (len <= 0 || pos + len > payload.Length)
                throw new InvalidDataException($"NAL length {len} overruns the access unit");
            ms.Write(StartCode);
            ms.Write(payload.Slice(pos, len));
            pos += len;
        }
        return ms.ToArray();
    }
    /// <summary>NAL unit types present in an Annex-B access unit (used by tests and diagnostics).</summary>
    public static IReadOnlyList<int> NalTypes(ReadOnlySpan<byte> annexB)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                types.Add(annexB[i + 3] & 0x1F);
                i += 2;
            }
        }
        return types;
    }
}
