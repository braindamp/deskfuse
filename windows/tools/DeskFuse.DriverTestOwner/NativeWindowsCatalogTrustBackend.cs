using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using static DeskFuse.DriverTestOwner.WindowsTrustInterop;

namespace DeskFuse.DriverTestOwner;

/// <summary>Cache-only Windows Authenticode verification; no store mutations or catalog discovery.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsCatalogTrustBackend : IWindowsCatalogTrustBackend
{
    public void RequireSystemContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Final package verification requires the SYSTEM security context.");
    }

    public void RequireExpectedCatalog(FileStream inf, string catalogName)
    {
        var selected = WindowsInfCatalogSelection.SelectedCatalog(inf);
        if (!string.Equals(selected, catalogName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SetupAPI selected a different INF catalog.");
    }

    public VerifiedSigner VerifyCatalog(FileStream catalog, DeskFuse.DriverTestOwner.Core.PackageProfile profile)
    {
        using var memory = new NativeAllocations();
        var file = new WindowsTrustInterop.FileInfo { Size = (uint)Marshal.SizeOf<WindowsTrustInterop.FileInfo>(),
            FilePath = memory.String(catalog.Name), File = catalog.SafeFileHandle.DangerousGetHandle() };
        return Verify(memory.Structure(file), ChoiceFile, profile);
    }

    public (VerifiedSigner Signer, string CatalogHash) VerifyMember(FileStream catalog, FileStream member,
        DeskFuse.DriverTestOwner.Core.PackageProfile profile)
    {
        var subsystem = DriverSubsystem;
        if (!CryptCATAdminAcquireContext2(out var admin, ref subsystem, "SHA256", nint.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to acquire catalog hashing context.");
        try
        {
            member.Position = 0;
            uint count = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, member.SafeFileHandle, ref count, null, 0) || count != 32)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size SHA-256 catalog member hash.");
            member.Position = 0;
            var hash = new byte[count];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, member.SafeFileHandle, ref count, hash, 0) || count != 32)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to calculate SHA-256 catalog member hash.");
            member.Position = 0;
            using var memory = new NativeAllocations();
            var info = new CatalogInfo
            {
                Size = (uint)Marshal.SizeOf<CatalogInfo>(), CatalogPath = memory.String(catalog.Name),
                MemberTag = memory.String(Convert.ToHexString(hash)), MemberPath = memory.String(member.Name),
                MemberFile = member.SafeFileHandle.DangerousGetHandle(), CalculatedHash = memory.Bytes(hash),
                CalculatedHashSize = count, CatalogAdmin = admin
            };
            return (Verify(memory.Structure(info), ChoiceCatalog, profile), Convert.ToHexString(hash).ToLowerInvariant());
        }
        finally
        {
            try { member.Position = 0; }
            finally
            {
                if (!CryptCATAdminReleaseContext(admin, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to release catalog hashing context.");
            }
        }
    }

    public bool IsLocalMachineTrustedPublisher(byte[] certificateDer)
    {
        return IsInMachineStore(StoreName.TrustedPublisher, certificateDer);
    }

    private static bool IsInMachineStore(StoreName name, byte[] certificateDer)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try { return certificates.Cast<X509Certificate2>().Any(cert => cert.RawData.AsSpan().SequenceEqual(certificateDer)); }
        finally { foreach (var cert in certificates) cert.Dispose(); }
    }

    private static VerifiedSigner Verify(nint info, uint choice, DeskFuse.DriverTestOwner.Core.PackageProfile profile)
    {
        if (profile is not (DeskFuse.DriverTestOwner.Core.PackageProfile.LocalTest or DeskFuse.DriverTestOwner.Core.PackageProfile.Release))
            throw new InvalidDataException("Build-only is not a trust verification profile.");
        using var memory = new NativeAllocations();
        var settings = new SignatureSettings { Size = (uint)Marshal.SizeOf<SignatureSettings>(), Flags = GetSecondarySignatureCount };
        var settingsPointer = memory.Structure(settings);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = UiNone, UnionChoice = choice, Info = info,
            StateAction = StateVerify, ProviderFlags = UseLocalMachineCerts | CacheOnlyUrlRetrieval | RevocationChainExcludeRoot,
            SignatureSettings = settingsPointer
        };
        var action = AuthenticodeAction;
        try
        {
            // WinVerifyTrust returns a LONG, not an HRESULT. Only exactly zero is success.
            var status = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (status != 0) throw new WindowsTrustException(status, "Windows rejected catalog trust or membership.");
            settings = Marshal.PtrToStructure<SignatureSettings>(settingsPointer);
            if (settings.SecondarySignatureCount != 0 || settings.VerifiedSignatureIndex != 0)
                throw new InvalidDataException("Multiple or ambiguous signatures are not admissible.");
            var provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == nint.Zero) throw new InvalidDataException("Missing Windows trust provider state.");
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == nint.Zero || WTHelperGetProvSignerFromChain(provider, 1, false, 0) != nint.Zero)
                throw new InvalidDataException("Missing or ambiguous Windows-verified signer.");
            ValidateMachineChain(signer);
            var certPointer = WTHelperGetProvCertFromChain(signer, 0);
            if (certPointer == nint.Zero) throw new InvalidDataException("Missing verified leaf certificate.");
            var cert = Marshal.PtrToStructure<ProviderCertificatePrefix>(certPointer);
            if (cert.CertContext == nint.Zero) throw new InvalidDataException("Missing verified certificate context.");
            return new VerifiedSigner(CertificateBytes(cert.CertContext));
        }
        finally
        {
            // Must close on success and failure. Do not use provider pointers afterwards.
            data.StateAction = StateClose;
            _ = WinVerifyTrust(new nint(-1), ref action, ref data);
        }
    }

    private static byte[] CertificateBytes(nint pointer)
    {
        var context = Marshal.PtrToStructure<CertContextPrefix>(pointer);
        if (context.Encoded == nint.Zero || context.EncodedLength is 0 or > 1048576)
            throw new InvalidDataException("Invalid verified certificate bytes.");
        var der = new byte[context.EncodedLength];
        Marshal.Copy(context.Encoded, der, 0, der.Length);
        return der;
    }

    private static void ValidateMachineChain(nint signerPointer)
    {
        var rootDer = ValidateChainRevocation(signerPointer);
        if (!IsInMachineStore(StoreName.Root, rootDer))
            throw new InvalidDataException("The validated signer chain does not end at a LocalMachine trusted root.");
    }

    internal static byte[] ValidateChainRevocation(nint signerPointer)
    {
        // The successful provider state is authoritative for timestamp/usage.
        // Explicitly reject unknown/offline revocation for every non-root
        // element, and require the final root in the machine root store.
        const uint revocationUnresolved = 0x00000040 | 0x01000000;
        const uint selfSigned = 0x00000008;
        var signer = Marshal.PtrToStructure<ProviderSignerPrefix>(signerPointer);
        if (signer.Size < Marshal.SizeOf<ProviderSignerPrefix>() || signer.ChainContext == nint.Zero)
            throw new InvalidDataException("Windows supplied no inspectable validated signer chain.");
        var chain = Marshal.PtrToStructure<ChainContextPrefix>(signer.ChainContext);
        if (chain.Size < Marshal.SizeOf<ChainContextPrefix>() || chain.ChainCount is 0 or > 16 || chain.Chains == nint.Zero)
            throw new InvalidDataException("Invalid Windows signer chain.");
        ChainElementPrefix root = default;
        for (var i = 0; i < chain.ChainCount; i++)
        {
            var simplePointer = Marshal.ReadIntPtr(chain.Chains, checked((int)i * nint.Size));
            if (simplePointer == nint.Zero) throw new InvalidDataException("Missing Windows simple chain.");
            var simple = Marshal.PtrToStructure<SimpleChainPrefix>(simplePointer);
            if (simple.Size < Marshal.SizeOf<SimpleChainPrefix>() || simple.ElementCount is 0 or > 64 || simple.Elements == nint.Zero)
                throw new InvalidDataException("Invalid Windows simple chain.");
            for (var j = 0; j < simple.ElementCount; j++)
            {
                var elementPointer = Marshal.ReadIntPtr(simple.Elements, checked((int)j * nint.Size));
                if (elementPointer == nint.Zero) throw new InvalidDataException("Missing Windows chain element.");
                var element = Marshal.PtrToStructure<ChainElementPrefix>(elementPointer);
                if (element.Size < Marshal.SizeOf<ChainElementPrefix>() || element.CertContext == nint.Zero)
                    throw new InvalidDataException("Invalid Windows chain element.");
                var finalRoot = i == chain.ChainCount - 1 && j == simple.ElementCount - 1;
                if (!finalRoot)
                {
                    if ((element.ErrorStatus & revocationUnresolved) != 0 || element.RevocationInfo == nint.Zero)
                        throw new InvalidDataException("Cached revocation evidence is unavailable or indeterminate.");
                    var revocation = Marshal.PtrToStructure<RevocationInfoPrefix>(element.RevocationInfo);
                    if (revocation.Size < Marshal.SizeOf<RevocationInfoPrefix>() || revocation.Result != 0)
                        throw new InvalidDataException("Windows did not confirm non-revocation for a chain element.");
                }
                if (finalRoot) root = element;
            }
        }
        if ((root.InfoStatus & selfSigned) == 0)
            throw new InvalidDataException("The validated signer chain has no self-signed root.");
        return CertificateBytes(root.CertContext);
    }
}

internal sealed class WindowsTrustException(int status, string message) : IOException($"{message} Status: 0x{unchecked((uint)status):X8}.")
{
    public int TrustStatus { get; } = status;
}

internal sealed class NativeAllocations : IDisposable
{
    private readonly List<nint> allocations = [];
    public nint String(string value) { var pointer = Marshal.StringToHGlobalUni(value); allocations.Add(pointer); return pointer; }
    public nint Bytes(byte[] bytes) { var pointer = Marshal.AllocHGlobal(bytes.Length); allocations.Add(pointer); Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer; }
    public nint Structure<T>(T value) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); allocations.Add(pointer);
        Marshal.StructureToPtr(value, pointer, false); return pointer;
    }
    public void Dispose() { foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer); allocations.Clear(); }
}
