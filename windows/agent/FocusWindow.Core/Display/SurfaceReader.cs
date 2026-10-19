namespace FocusWindow.Core.Display;
public readonly record struct FrameInfo(ulong Seq, int Width, int Height, long PresentQpc);
public enum ReadOutcome
{
    /// <summary>A consistent frame newer than the caller's last one was copied out.</summary>
    Ok,
    /// <summary>Nothing newer than <c>afterSeq</c> has been published.</summary>
    NoNewFrame,
    /// <summary>The writer kept overtaking us for every retry (extremely slow reader).</summary>
    Torn,
    /// <summary>The slot header describes an impossible frame. Treated as a hard error.</summary>
    Invalid,
}
public readonly record struct SurfaceStatus(
    bool Initialized,
    uint MonitorArrived,
    uint LastError,
    ulong DroppedFrames,
    ulong PublishedSeq,
    uint Width,
    uint Height);
/// <summary>
/// Reader side of the driver's seqlock surface (layout and protocol: shared/FocusShared.h).
///
///   n = hdr.PublishedSeq; i = hdr.LatestSlotIndex; s0 = slot[i].Seq
///   if (s0 != n) retry                          -- slot was reused, or is mid-write (Seq == 0)
///   copy pixels
///   barrier; s1 = slot[i].Seq
///   if (s1 != s0) retry                         -- writer reclaimed the slot during our copy
///
/// The writer sets Seq = 0 BEFORE it touches a slot's pixels. Without that, the reader could
/// see the same (old) Seq before and after a copy that overlapped a rewrite and accept torn
/// pixels. SurfaceReaderTests runs a concurrent writer/reader stress test against exactly
/// this protocol.
///
/// The reader works on a raw pointer so it can sit on top of a MemoryMappedFile view in
/// production and on plain native memory in tests.
/// </summary>
public sealed unsafe class SurfaceReader
{
    private readonly byte* _base;
    private readonly long _length;
    public SurfaceReader(byte* basePtr, long length)
    {
        if (basePtr == null) throw new ArgumentNullException(nameof(basePtr));
        if (length < DriverContract.HeaderBytes) throw new ArgumentException("section smaller than the header", nameof(length));
        _base = basePtr;
        _length = length;
    }
    private uint U32(int offset) => Volatile.Read(ref *(uint*)(_base + offset));
    private ulong U64(int offset) => Volatile.Read(ref *(ulong*)(_base + offset));
    public bool IsInitialized =>
        U32(DriverContract.HdrMagic) == DriverContract.SurfaceMagic &&
        U32(DriverContract.HdrVersion) == DriverContract.SurfaceVersion;
    public SurfaceStatus ReadStatus() => new(
        IsInitialized,
        U32(DriverContract.HdrMonitorArrived),
        U32(DriverContract.HdrLastError),
        U64(DriverContract.HdrDropped),
        U64(DriverContract.HdrPublishedSeq),
        U32(DriverContract.HdrWidth),
        U32(DriverContract.HdrHeight));
    public ReadOutcome TryReadNewest(ulong afterSeq, ref byte[] buffer, out FrameInfo info)
    {
        info = default;
        if (!IsInitialized) return ReadOutcome.NoNewFrame;
        var slotCount = U32(DriverContract.HdrSlotCount);
        var slotStride = U32(DriverContract.HdrSlotStrideBytes);
        if (slotCount is 0 or > 16 || slotStride <= DriverContract.SlotHeaderBytes ||
            DriverContract.HeaderBytes + (long)slotCount * slotStride > _length)
            return ReadOutcome.Invalid;
        var pixelCapacity = (long)slotStride - DriverContract.SlotHeaderBytes;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var seq = U64(DriverContract.HdrPublishedSeq);
            if (seq == 0 || seq <= afterSeq) return ReadOutcome.NoNewFrame;
            var index = U32(DriverContract.HdrLatestSlot);
            if (index >= slotCount) continue; // transient: header update in flight
            var slot = _base + DriverContract.HeaderBytes + (long)index * slotStride;
            var s0 = Volatile.Read(ref *(ulong*)(slot + DriverContract.SlotSeq));
            if (s0 != seq) continue;
            var width = (int)Volatile.Read(ref *(uint*)(slot + DriverContract.SlotWidth));
            var height = (int)Volatile.Read(ref *(uint*)(slot + DriverContract.SlotHeight));
            var qpc = *(long*)(slot + DriverContract.SlotQpc);
            var bytes = (long)width * height * 4;
            if (width <= 0 || height <= 0 || bytes > pixelCapacity)
            {
                // Dimensions are only trustworthy if the slot did not change under us.
                if (Volatile.Read(ref *(ulong*)(slot + DriverContract.SlotSeq)) == s0) return ReadOutcome.Invalid;
                continue;
            }
            if (buffer.Length < bytes) buffer = new byte[bytes];
            fixed (byte* dst = buffer)
            {
                Buffer.MemoryCopy(slot + DriverContract.SlotHeaderBytes, dst, buffer.Length, bytes);
            }
            Interlocked.MemoryBarrier();
            var s1 = Volatile.Read(ref *(ulong*)(slot + DriverContract.SlotSeq));
            if (s1 != s0) continue; // torn: the writer reclaimed the slot while we copied
            info = new FrameInfo(seq, width, height, qpc);
            return ReadOutcome.Ok;
        }
        return ReadOutcome.Torn;
    }
}
