using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

internal sealed record HostIdentityRecord(int Version, Guid AuthorityNamespace, Guid DeploymentId,
    string Provider, string KeyName, byte[] SigningSpki, byte[] HostSha256, int PolicyVersion);

/// <summary>
/// Cold, SYSTEM-only host identity. Creation belongs to trusted installation, never service startup.
/// CNG private-at-creation behavior requires installed-SYSTEM qualification before production admission.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerHostIdentity : IDisposable, IEnrollmentHostSigner, IEnrollmentCommitSigner
{
    private const string RecordPath = "bootstrap/host-identity.json", IntentPath = "bootstrap/host-key-intent.json";
    private const string ProviderName = "Microsoft Software Key Storage Provider";
    private const string KeyDescriptor = "O:SYG:SYD:P(A;;GA;;;SY)";
    // NCrypt[Get/Set]Property Security Descr flags: OWNER | GROUP | DACL_SECURITY_INFORMATION.
    private const CngPropertyOptions SecurityInformation = (CngPropertyOptions)7;
    private readonly CngKey key;
    private readonly ECDsaCng signer;
    private readonly object gate = new();
    private readonly HostIdentityRecord record;
    private bool disposed;
    internal Guid NamespaceId => record.AuthorityNamespace;
    internal Guid DeploymentId => record.DeploymentId;
    internal byte[] PublicSpki => record.SigningSpki.ToArray();
    internal byte[] HostSha256 => record.HostSha256.ToArray();
    private static string KeyName(Guid ns) => $"DeskFuse.Authority.{ns:N}.Host.v1";

    private BrokerHostIdentity(CngKey key, HostIdentityRecord record)
    { this.key = key; this.record = record; signer = new ECDsaCng(key); }

    internal static BrokerHostIdentity CreateDuringTrustedInstallation(SecurePrivateStorageRoot root, Guid deploymentId)
    {
        RequireRoot(root); if (deploymentId == Guid.Empty) throw new InvalidDataException("Missing approved deployment.");
        // CreateNew intent precedes key creation. Crashes/collisions leave an explicit repair condition.
        var name = KeyName(root.NamespaceId);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, authorityNamespace = root.NamespaceId, deploymentId, provider = ProviderName, keyName = name });
        PersistNew(root, IntentPath, intent);
        var descriptor = new RawSecurityDescriptor(KeyDescriptor);
        var security = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(security, 0);
        var creation = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.None,
        };
        // CngKey.Create applies creation parameters before NCryptFinalizeKey. No create-then-tighten fallback.
        // https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptsetproperty
        creation.Parameters.Add(new CngProperty("Security Descr", security, SecurityInformation));
        CngKey? key = null;
        try
        {
            key = CngKey.Create(CngAlgorithm.ECDsaP256, name, creation); // No overwrite flag.
            ValidateKey(key, name);
            using var publicKey = new ECDsaCng(key);
            var spki = publicKey.ExportSubjectPublicKeyInfo(); EnrollmentProtocol.ValidateSpki(spki);
            var record = new HostIdentityRecord(1, root.NamespaceId, deploymentId, ProviderName, name, spki, SHA256.HashData(spki), 1);
            PersistNew(root, RecordPath, JsonSerializer.SerializeToUtf8Bytes(record, ProtectedBrokerEnrollments.Json));
            return new(key, record);
        }
        catch { key?.Dispose(); throw; } // Never delete/adopt a possibly persisted orphan automatically.
    }

    internal static BrokerHostIdentity OpenExisting(SecurePrivateStorageRoot root, Guid expectedDeployment)
    {
        RequireRoot(root);
        using var file = root.OpenProtectedRead(RecordPath);
        var bytes = SecurePrivateStorageRoot.ReadBounded(file, 4096);
        var record = JsonSerializer.Deserialize<HostIdentityRecord>(bytes, ProtectedBrokerEnrollments.Json)
            ?? throw new InvalidDataException("Missing host identity.");
        if (!JsonSerializer.SerializeToUtf8Bytes(record, ProtectedBrokerEnrollments.Json).AsSpan().SequenceEqual(bytes) ||
            record.Version != 1 || record.PolicyVersion != 1 || record.AuthorityNamespace != root.NamespaceId ||
            expectedDeployment == Guid.Empty || record.DeploymentId != expectedDeployment || record.Provider != ProviderName ||
            record.KeyName != KeyName(root.NamespaceId)) throw new InvalidDataException("Host identity configuration mismatch.");
        EnrollmentProtocol.ValidateSpki(record.SigningSpki);
        if (record.HostSha256 is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(record.SigningSpki), record.HostSha256))
            throw new InvalidDataException("Host identity digest mismatch.");
        var key = CngKey.Open(record.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey | CngKeyOpenOptions.Silent);
        try
        {
            ValidateKey(key, record.KeyName);
            using var publicKey = new ECDsaCng(key);
            if (!CryptographicOperations.FixedTimeEquals(publicKey.ExportSubjectPublicKeyInfo(), record.SigningSpki))
                throw new InvalidDataException("The installed host signing key changed.");
            return new(key, record);
        }
        catch { key.Dispose(); throw; }
    }

    private static void ValidateKey(CngKey key, string expectedName)
    {
        if (!key.IsMachineKey || key.IsEphemeral || key.KeyName != expectedName || key.Provider?.Provider != ProviderName ||
            key.Algorithm != CngAlgorithm.ECDsaP256 || key.KeySize != 256 || key.KeyUsage != CngKeyUsages.Signing || key.ExportPolicy != CngExportPolicies.None)
            throw new InvalidDataException("Unexpected host key provider or policy.");
        var security = key.GetProperty("Security Descr", SecurityInformation).GetValue()
            ?? throw new InvalidDataException("Host key security descriptor is missing.");
        if (security.Length is < 20 or > 65536) throw new InvalidDataException("Invalid host key descriptor size.");
        var descriptor = new RawSecurityDescriptor(security, 0);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (descriptor.Owner != system || descriptor.Group != system ||
            (descriptor.ControlFlags & (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected)) !=
                (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is not { Count: 1 } acl || acl[0] is not CommonAce ace ||
            ace.AceType != AceType.AccessAllowed || ace.AceFlags != AceFlags.None || ace.IsCallback || ace.SecurityIdentifier != system ||
            ace.AccessMask is not (unchecked((int)0x10000000)) and not 0x001f019b)
            throw new UnauthorizedAccessException("Host signing key is not SYSTEM-only.");
    }

    // These entrypoints are internal to the coordinator, which constructs messages from its own state.
    internal byte[] SignOffer(EnrollmentOffer offer)
    {
        if (offer.AuthorityNamespace != NamespaceId || !CryptographicOperations.FixedTimeEquals(offer.HostSpki, record.SigningSpki))
            throw new InvalidDataException("Foreign host offer.");
        return Sign(EnrollmentProtocol.Offer(offer));
    }
    internal byte[] SignFrozen(EnrollmentTranscript transcript)
    {
        if (transcript.Offer.AuthorityNamespace != NamespaceId || !CryptographicOperations.FixedTimeEquals(transcript.Offer.HostSpki, record.SigningSpki))
            throw new InvalidDataException("Foreign enrollment transcript.");
        return Sign(EnrollmentProtocol.Frozen(SHA256.HashData(EnrollmentProtocol.Transcript(transcript))));
    }
    Guid IEnrollmentHostSigner.NamespaceId => NamespaceId;
    byte[] IEnrollmentHostSigner.PublicSpki => PublicSpki;
    byte[] IEnrollmentHostSigner.SignOffer(EnrollmentOffer offer) => SignOffer(offer);
    byte[] IEnrollmentHostSigner.SignFrozen(EnrollmentTranscript transcript) => SignFrozen(transcript);

    internal byte[] SignCommittedReceipt(TrustedBrokerEnrollmentStore.Committed committed)
    {
        var bytes = committed.ReceiptBytes;
        var receipt = EnrollmentProtocol.ParseReceipt(bytes);
        RequireOwnBinding(receipt.Binding);
        return Sign(bytes);
    }
    byte[] IEnrollmentCommitSigner.SignCommittedReceipt(TrustedBrokerEnrollmentStore.Committed committed) => SignCommittedReceipt(committed);
    byte[] IEnrollmentCommitSigner.SignStatus(EnrollmentStatus status) => SignStatus(status);
    internal byte[] SignIssuedChallenge(IssuedBrokerChallenge challenge)
    {
        ReadOnlySpan<byte> domain = "DeskFuse.BrokerProof.v2\0"u8;
        var transcript = challenge.Transcript;
        var offset = domain.Length + 32;
        if (transcript.Length is > 384 || transcript.Length < offset + 64 || !transcript.AsSpan(0, domain.Length).SequenceEqual(domain) ||
            !CryptographicOperations.FixedTimeEquals(transcript.AsSpan(offset, 32), record.HostSha256) ||
            new Guid(transcript.AsSpan(offset + 32, 16), true) != NamespaceId || new Guid(transcript.AsSpan(offset + 48, 16), true) == Guid.Empty)
            throw new UnauthorizedAccessException("Challenge is outside the installed v2 authority.");
        ReadOnlySpan<byte> context = "DeskFuse.BrokerChallenge.v1\0"u8;
        var bytes = new byte[context.Length + 32]; context.CopyTo(bytes); SHA256.HashData(transcript).CopyTo(bytes, context.Length);
        return Sign(bytes);
    }
    internal byte[] SignStatus(EnrollmentStatus status)
    { RequireOwnBinding(status.Binding); return Sign(EnrollmentProtocol.Status(status)); }
    private void RequireOwnBinding(EnrollmentBinding binding)
    {
        if (binding.AuthorityNamespace != NamespaceId || !CryptographicOperations.FixedTimeEquals(binding.HostSha256, record.HostSha256))
            throw new UnauthorizedAccessException("Foreign authority binding.");
    }

    private byte[] Sign(byte[] bytes)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return signer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
    }
    private static void RequireRoot(SecurePrivateStorageRoot root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true || root.Profile != PrivateStorageProfile.BrokerAuthority)
            throw new UnauthorizedAccessException("Host authority requires its SYSTEM-owned private namespace.");
        root.ValidateHeldIdentity();
    }
    private static void PersistNew(SecurePrivateStorageRoot root, string path, byte[] bytes)
    { using var held = root.CopyProtectedFile(path, new MemoryStream(bytes, false), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 4096); }
    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; signer.Dispose(); key.Dispose(); } }
}
