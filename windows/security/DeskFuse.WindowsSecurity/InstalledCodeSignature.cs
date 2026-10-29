using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskFuse.WindowsSecurity;

/// <summary>Cold Authenticode check on the already retained installed file, in addition to independent hash approval.</summary>
[SupportedOSPlatform("windows")]
internal static class InstalledCodeSignature
{
    internal static void RequireValid(FileStream heldFile)
    {
        var fileName = Marshal.StringToHGlobalUni(heldFile.Name);
        var infoMemory = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var settingsMemory = Marshal.AllocHGlobal(Marshal.SizeOf<SignatureSettings>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, Info = infoMemory,
            StateAction = 1, ProviderFlags = 0x8 | 0x80 | 0x1000, SignatureSettings = settingsMemory
        }; // local-machine policy, cache-only revocation chain excluding root; no online/background fetch.
        var action = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), FilePath = fileName,
                File = heldFile.SafeFileHandle.DangerousGetHandle() }, infoMemory, false);
            Marshal.StructureToPtr(new SignatureSettings { Size = (uint)Marshal.SizeOf<SignatureSettings>(), Flags = 2 }, settingsMemory, false);
            var status = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (status != 0) throw new IOException($"Installed setup signature is not Windows-verified: 0x{unchecked((uint)status):x8}.");
            var settings = Marshal.PtrToStructure<SignatureSettings>(settingsMemory);
            if (settings.SecondarySignatureCount != 0 || settings.VerifiedSignatureIndex != 0)
                throw new InvalidDataException("Ambiguous installed setup signature.");
        }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.FreeHGlobal(settingsMemory); Marshal.FreeHGlobal(infoMemory); Marshal.FreeHGlobal(fileName);
            GC.KeepAlive(heldFile);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { internal uint Size; internal nint FilePath, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct SignatureSettings
    { internal uint Size, Index, Flags, SecondarySignatureCount, VerifiedSignatureIndex; internal nint CryptoPolicy; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        internal uint Size; internal nint PolicyCallback, SipClientData; internal uint UiChoice, RevocationChecks, UnionChoice;
        internal nint Info; internal uint StateAction; internal nint StateData, UrlReference; internal uint ProviderFlags, UiContext;
        internal nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
