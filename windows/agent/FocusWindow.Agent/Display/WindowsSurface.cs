using System.IO.MemoryMappedFiles;
using FocusWindow.Core.Display;
namespace FocusWindow.Agent.Display;
/// <summary>
/// Opens the driver's shared frame surface (Global\FocusWindowSurface.v2) read-only, plus the
/// auto-reset event it signals per published frame, and exposes it as an <see cref="IFrameSource"/>.
/// The seqlock protocol itself is in Core (SurfaceReader) where it is stress-tested.
/// </summary>
internal sealed unsafe class WindowsSurface : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _frameReady;
    private byte* _pointer;
    public SurfaceReader Reader { get; }
    private WindowsSurface(MemoryMappedFile file, MemoryMappedViewAccessor view, EventWaitHandle ev, byte* pointer, long length)
    {
        _file = file;
        _view = view;
        _frameReady = ev;
        _pointer = pointer;
        Reader = new SurfaceReader(pointer, length);
    }
    public static WindowsSurface Open()
    {
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? ev = null;
        var stage = "opening shared frame mapping";
        try
        {
            file = MemoryMappedFile.OpenExisting(DriverContract.SectionName, MemoryMappedFileRights.Read);
            stage = "mapping frame bytes read-only";
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            // The driver grants SYNCHRONIZE explicitly, not GENERIC_READ, on this event.
            stage = "opening frame-ready event for synchronization";
            ev = EventWaitHandleAcl.OpenExisting(DriverContract.FrameEventName, System.Security.AccessControl.EventWaitHandleRights.Synchronize);
            stage = "acquiring read-only frame pointer";
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            var length = (long)view.SafeMemoryMappedViewHandle.ByteLength;
            return new WindowsSurface(file, view, ev, pointer + view.PointerOffset, length);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Dispose(file, view, ev);
            throw new DisplayException("driver",
                "The driver's shared frame surface does not exist.",
                "Is the FocusIdd driver installed and started? (devcon status Root\\FocusIdd)");
        }
        catch (FileNotFoundException)
        {
            Dispose(file, view, ev);
            throw new DisplayException("driver",
                "The driver's shared frame surface does not exist.",
                "Is the FocusIdd driver installed and started? (devcon status Root\\FocusIdd)");
        }
        catch (UnauthorizedAccessException ex)
        {
            Dispose(file, view, ev);
            throw new DisplayException("driver",
                $"Access denied while {stage} (HRESULT 0x{ex.HResult:X8}).",
                "Check the driver's named frame-mapping/event permissions and the identity of the interactive probe process.");
        }
    }
    private static void Dispose(MemoryMappedFile? f, MemoryMappedViewAccessor? v, EventWaitHandle? e)
    {
        e?.Dispose();
        v?.Dispose();
        f?.Dispose();
    }
    public IFrameSource CreateFrameSource() => new SurfaceFrameSource(Reader, ms => _frameReady.WaitOne(ms));
    public SurfaceStatus Status => Reader.ReadStatus();
    public void Dispose()
    {
        if (_pointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }
        _frameReady.Dispose();
        _view.Dispose();
        _file.Dispose();
    }
}
