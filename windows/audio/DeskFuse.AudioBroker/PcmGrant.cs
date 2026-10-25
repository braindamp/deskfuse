using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;
internal sealed class PcmGrant : IDisposable
{
    private readonly SafeFileHandle _file;
    public ulong FileHandle {get;}
    public ulong SectionHandle {get;}
    public ulong EventHandle {get;}
    public ulong Generation {get;}
    private const uint BindCode=0x0022e000, RevokeCode=0x0022e010;
    public PcmGrant(string endpoint,Guid source,SafeFileHandle consumer)
    {
        // Bind the private source context back to THIS lease's exact endpoint.
        if (deskfuse_endpoint_query(endpoint,out var identity)!=0 || identity.Abi!=1 ||
            identity.Build!=0x2026102501UL || identity.ExportAbi!=1 || identity.Source!=source)
            throw new InvalidOperationException("Endpoint and running direct-source identity do not match.");
        _file=CreateFileW(@"\\.\DeskFusePcm",0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero);
        if (_file.IsInvalid) throw new Win32Exception();
        try
        {
            var request=new Bind {Size=(uint)Marshal.SizeOf<Bind>(),Abi=1,Source=source,
                Process=unchecked((ulong)consumer.DangerousGetHandle().ToInt64())};
            if (!DeviceIoControl(_file,BindCode,ref request,Marshal.SizeOf<Bind>(),out var reply,
                Marshal.SizeOf<Reply>(),out var returned,IntPtr.Zero) || returned!=Marshal.SizeOf<Reply>() ||
                reply.Size!=Marshal.SizeOf<Reply>() || reply.Abi!=1)
                throw new Win32Exception();
            if (consumer.IsInvalid || !DuplicateHandle(GetCurrentProcess(),_file,consumer,out var remote,0, false,2))
                throw new Win32Exception();
            FileHandle=remote.ToUInt64();SectionHandle=reply.Section;EventHandle=reply.Event;Generation=reply.Generation;
        }
        catch {_file.Dispose();throw;}
    }
    public void Dispose()
    {
        if (_file.IsClosed) return;
        // Old read-only section views stay backed by their own section object;
        // the driver detaches this grant before releasing its producer storage.
        if (!DeviceIoControl(_file,RevokeCode,IntPtr.Zero,0,IntPtr.Zero,0,out _,IntPtr.Zero))
            throw new Win32Exception(); // preserve endpoint if producer rundown is unconfirmed
        _file.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bind {public uint Size,Abi;public Guid Source;public ulong Process;}
    [StructLayout(LayoutKind.Sequential)] private struct Reply {public uint Size,Abi;public ulong Section,Event,Generation;}
    [StructLayout(LayoutKind.Sequential)] private struct Timing {public uint Size,Abi;public ulong Build;public Guid Source;public uint Flags,ExportAbi;}
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
    [DllImport("deskfuse-source-control.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] private static extern int deskfuse_endpoint_query(string endpoint,out Timing result);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,ref Bind input,int inputSize,out Reply output,int outputSize,out int returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,IntPtr input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle OpenProcess(uint access,bool inherit,uint process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr source,SafeFileHandle handle,SafeFileHandle target,out UIntPtr result,uint access,bool inherit,uint options);
}
