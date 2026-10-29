using System.Runtime.Versioning;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

// Narrow seams for the concrete enrollment persistence owner, not a storage framework.
internal interface IEnrollmentRecordFile : IDisposable
{
    byte[] Read();
    void CompletePositive(byte[] validatedBytes);
    void CompleteDenial();
}
internal interface IEnrollmentRecordFiles : IDisposable
{
    Guid NamespaceId { get; }
    string[] EnumerateNames();
    IEnrollmentRecordFile Open(Guid id, EnrollmentPrivateRecordKind kind);
    IEnrollmentRecordFile Create(Guid id, EnrollmentPrivateRecordKind kind);
}

[SupportedOSPlatform("windows")]
internal sealed class EnrollmentRecordFiles : IEnrollmentRecordFiles
{
    private readonly SecurePrivateStorageRoot root;
    private readonly IDisposable lease, directory;
    public Guid NamespaceId => root.NamespaceId;
    internal EnrollmentRecordFiles(SecurePrivateStorageRoot root)
    {
        if (root.Profile != PrivateStorageProfile.BrokerAuthority) throw new InvalidDataException("Wrong enrollment root.");
        this.root = root;
        root.RequireEnrollmentPersistenceProfile(); // Reject unsupported storage even before the first enrollment exists.
        lease = root.AcquireBrokerOwnerLease();
        try { directory = root.OpenDirectory("enrollments"); }
        catch { lease.Dispose(); throw; }
    }
    public string[] EnumerateNames()
    {
        root.ValidateHeldIdentity();
        var names = Directory.EnumerateFileSystemEntries(Path.Combine(root.DirectoryPath, "enrollments"))
            .Take(16385).Select(Path.GetFileName).Cast<string>().ToArray();
        if (names.Length > 16384) throw new InvalidDataException("Enrollment artifacts exceed their admitted bound.");
        return names;
    }
    public IEnrollmentRecordFile Open(Guid id, EnrollmentPrivateRecordKind kind) => new Held(root.OpenEnrollmentRecord(id, kind));
    public IEnrollmentRecordFile Create(Guid id, EnrollmentPrivateRecordKind kind) => new Held(root.CreateEnrollmentRecord(id, kind));
    private sealed class Held(EnrollmentPrivateRecord record) : IEnrollmentRecordFile
    {
        public byte[] Read() => record.Read();
        public void CompletePositive(byte[] bytes) => record.CompletePositive(bytes);
        public void CompleteDenial() => record.CompleteDenial();
        public void Dispose() => record.Dispose();
    }
    public void Dispose() { try { directory.Dispose(); } finally { lease.Dispose(); } }
}
