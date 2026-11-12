using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DeskFuse.AudioBroker;
internal static class PcmBindDiagnostic
{
    [StructLayout(LayoutKind.Sequential)] internal struct Geometry { public uint State,Packet,Notifications,Rate,Channels,Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct TraceEvent {
        public ulong Sequence,Serial,Generation;
        public uint Kind,Streams,Denials,Flags,Pin;
        public Guid Mode;
        public Geometry Geometry;
        public uint Alignment;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Snapshot {
        public uint Size,Abi,Stage,Status;
        public Guid Source;
        public ulong Generation,Owner,Consumer,Selected;
        public uint Flags,Streams,FirstDenial,Denials,Count,Next;
        public Geometry Geometry;
        public Guid Mode;
        public uint Pin,Alignment;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)] public TraceEvent[] Events;
    }
    internal static void Read(SafeFileHandle file,int originalError) {
        try {
            var size=Marshal.SizeOf<Snapshot>();
            if(!DeviceIoControl(file,0x22e014,IntPtr.Zero,0,out var d,size,out var returned,IntPtr.Zero)) {
                var queryError=Marshal.GetLastPInvokeError();
                StartupTrace.Write($"BIND_DIAGNOSTIC unavailable query_error={queryError} original_error={originalError}");return;
            }
            if(size!=840 || returned!=size || d.Size!=size || d.Abi!=1 || d.Count>8 || d.Next>=8) {
                StartupTrace.Write($"BIND_DIAGNOSTIC invalid original_error={originalError}");return;
            }
            StartupTrace.Write($"BIND_DIAGNOSTIC stage={d.Stage} status=0x{d.Status:X8} source={d.Source} flags={d.Flags} streams={d.Streams} generation={d.Generation} selected={d.Selected} owner={d.Owner} consumer={d.Consumer} first_denial={d.FirstDenial} denials={d.Denials} geometry={d.Geometry.State}/{d.Geometry.Packet}/{d.Geometry.Notifications}/{d.Geometry.Rate}/{d.Geometry.Channels}/{d.Geometry.Bits}");
            for(uint i=0;i<d.Count;i++) {
                var e=d.Events[(d.Next+8-d.Count+i)%8];
                StartupTrace.Write($"BIND_HISTORY seq={e.Sequence} kind={e.Kind} stream={e.Serial} generation={e.Generation} streams={e.Streams} flags={e.Flags} denials={e.Denials} pin={e.Pin} mode={e.Mode} format={e.Geometry.Rate}/{e.Geometry.Channels}/{e.Geometry.Bits}/{e.Alignment} geometry={e.Geometry.State}/{e.Geometry.Packet}/{e.Geometry.Notifications}");
            }
        } catch { /* Diagnostic failure must not replace the original bind error. */ }
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle file,uint code,IntPtr input,int inputSize,out Snapshot output,int outputSize,out int returned,IntPtr overlapped);
}
