using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.DriverTestOwner;

// Layouts are checked against the actual Windows SDK by driver-test-abi.cpp.
// These declarations alone do not authorize any installation or verify trust.
internal static class WindowsTrustInterop
{
    internal const uint UiNone = 2, ChoiceFile = 1, ChoiceCatalog = 2;
    internal const uint StateVerify = 1, StateClose = 2;
    internal const uint CacheOnlyUrlRetrieval = 0x1000, RevocationChainExcludeRoot = 0x80;
    internal const uint UseLocalMachineCerts = 0x8, GetSecondarySignatureCount = 0x2;
    internal static readonly Guid AuthenticodeAction = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
    internal static readonly Guid DriverSubsystem = new("f750e6c3-38ee-11d1-85e5-00c04fc295ee");

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrustData
    {
        public uint Size;
        public nint PolicyCallback, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint Info;
        public uint StateAction;
        public nint StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfo
    {
        public uint Size;
        public nint FilePath, File, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CatalogInfo
    {
        public uint Size, CatalogVersion;
        public nint CatalogPath, MemberTag, MemberPath, MemberFile, CalculatedHash;
        public uint CalculatedHashSize;
        public nint CatalogContext, CatalogAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignatureSettings
    {
        public uint Size, Index, Flags, SecondarySignatureCount, VerifiedSignatureIndex;
        public nint CryptoPolicy;
    }

    // Prefixes used only while the provider owns the full objects.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderCertificatePrefix { public uint Size; public nint CertContext; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CertContextPrefix { public uint EncodingType; public nint Encoded; public uint EncodedLength; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderSignerPrefix
    {
        public uint Size, VerifyTimeLow, VerifyTimeHigh, CertCount;
        public nint CertChain;
        public uint SignerType;
        public nint SignerInfo;
        public uint Error, CounterSignerCount;
        public nint CounterSigners, ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ChainContextPrefix { public uint Size, ErrorStatus, InfoStatus, ChainCount; public nint Chains; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SimpleChainPrefix { public uint Size, ErrorStatus, InfoStatus, ElementCount; public nint Elements; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ChainElementPrefix { public uint Size; public nint CertContext; public uint ErrorStatus, InfoStatus; public nint RevocationInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RevocationInfoPrefix { public uint Size, Result; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern nint WTHelperGetProvCertFromChain(nint signer, uint certIndex);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminAcquireContext2(out nint context, ref Guid subsystem,
        string algorithm, nint strongPolicy, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminCalcHashFromFileHandle2(nint context, SafeFileHandle file,
        ref uint size, [Out] byte[]? hash, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseContext(nint context, uint flags);
}
