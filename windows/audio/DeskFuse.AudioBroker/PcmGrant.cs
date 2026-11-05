using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;
internal sealed class PcmGrant : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly Action<SafeFileHandle> revoke;
    public ulong FileHandle {get; private set;}
    public ulong SectionHandle {get; private set;}
    public ulong EventHandle {get; private set;}
    public ulong Generation {get; private set;}
    private bool bound;
    private const uint BindCode=0x0022e000, RevokeCode=0x0022e010;
    internal PcmGrant() : this(CreateFileW(@"\\.\DeskFusePcm",0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero), Revoke)
    {
    }
    internal PcmGrant(SafeFileHandle file, Action<SafeFileHandle> revoke)
    {
        if (file.IsInvalid) throw new Win32Exception();
        _file = file;
        this.revoke = revoke;
    }
    public PcmGrant(string endpoint,Guid source,SafeFileHandle consumer) : this()
    {
        try { Attach(endpoint,source,consumer); }
        catch { _file.Dispose(); throw; }
    }
    internal void Attach(string endpoint,Guid source,SafeHandle consumer)
    {
        if (bound) throw new InvalidOperationException("The PCM export is already bound.");
        RequireEndpoint(endpoint, source);
        var retained=false;
        try
        {
            consumer.DangerousAddRef(ref retained);
            if (consumer.IsInvalid) throw new InvalidOperationException("Invalid PCM consumer process.");
            BindAndDuplicate(() =>
            {
                var request = new Bind {Size=(uint)Marshal.SizeOf<Bind>(), Abi=2, Source=source,
                    Process=unchecked((ulong)consumer.DangerousGetHandle().ToInt64())};
                if (!DeviceIoControl(_file,BindCode,ref request,Marshal.SizeOf<Bind>(),out var reply,
                    Marshal.SizeOf<Reply>(),out var returned,IntPtr.Zero))
                    throw new Win32Exception();
                return (reply, returned);
            }, () =>
            {
                if (!DuplicateHandle(GetCurrentProcess(),_file,consumer,out var remote,0, false,2))
                    throw new Win32Exception();
                return remote.ToUInt64();
            });
        }
        finally { if (retained) consumer.DangerousRelease(); }
    }
    internal void BindAndDuplicate(Func<(Reply Reply, int Bytes)> bind, Func<ulong> duplicate)
    {
        if (bound || _file.IsClosed) throw new InvalidOperationException("The PCM export is unavailable.");
        var (reply, returned) = bind();
        // The kernel has installed the export even if the reply or duplicate fails.
        bound = true;
        if (returned != Marshal.SizeOf<Reply>() || reply.Size != Marshal.SizeOf<Reply>() || reply.Abi != 2)
            throw new InvalidDataException("Invalid kernel PCM bind response.");
        FileHandle = duplicate();
        SectionHandle = reply.Section; EventHandle = reply.Event; Generation = reply.Generation;
    }
    internal static void RequireEndpoint(string endpoint, Guid source)
    {
        // Bind the private source context back to THIS lease's exact endpoint.
        if (deskfuse_endpoint_query(endpoint,out var identity)!=0 || identity.Size!=Marshal.SizeOf<Timing>() || identity.Abi!=1 ||
            identity.Build!=0x2026102502UL || identity.ExportAbi!=2 || (identity.Flags & 3)!=3 || identity.Source!=source)
            throw new InvalidOperationException("Endpoint and running direct-source identity do not match.");
    }
    public void Dispose()
    {
        if (_file.IsClosed) return;
        // Old read-only section views stay backed by their own section object;
        // the driver detaches this grant before releasing its producer storage.
        if (bound) revoke(_file);
        _file.Dispose();
    }
    private static void Revoke(SafeFileHandle file)
    {
        if (!DeviceIoControl(file,RevokeCode,IntPtr.Zero,0,IntPtr.Zero,0,out _,IntPtr.Zero))
            throw new Win32Exception(); // preserve endpoint if producer rundown is unconfirmed
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bind {public uint Size,Abi;public Guid Source;public ulong Process;}
    [StructLayout(LayoutKind.Sequential)] internal struct Reply {public uint Size,Abi;public ulong Section,Event,Generation;}
    [StructLayout(LayoutKind.Sequential)] private struct Timing {public uint Size,Abi;public ulong Build;public Guid Source;public uint Flags,ExportAbi;}
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
    [DllImport("deskfuse-source-control.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] private static extern int deskfuse_endpoint_query(string endpoint,out Timing result);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,ref Bind input,int inputSize,out Reply output,int outputSize,out int returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,IntPtr input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle OpenProcess(uint access,bool inherit,uint process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr source,SafeFileHandle handle,SafeHandle target,out UIntPtr result,uint access,bool inherit,uint options);
}
