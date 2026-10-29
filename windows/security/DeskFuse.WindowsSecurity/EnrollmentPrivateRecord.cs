using System.Runtime.Versioning;

namespace DeskFuse.WindowsSecurity;

public enum EnrollmentPrivateRecordKind { Evidence, Commit, Receipt, Revocation }

/// <summary>
/// Cold enrollment-only write custody. No stream escapes; successful completion
/// belongs to this exact held object and owner, never to a readable pathname.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnrollmentPrivateRecord : IDisposable
{
    private readonly FileStream file;
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    private readonly ProtectedDirectoryInspection parent;
    private readonly Action validate;
    private readonly int maximum;
    private readonly bool created;
    private readonly bool negative;
    private byte[]? completed;
    private bool denialCompleted, failed, disposed;

    internal EnrollmentPrivateRecord(FileStream file, ProtectedDirectoryInspection parent,
        Action validate, EnrollmentPrivateRecordKind kind, bool created)
    {
        this.file = file; handle = file.SafeFileHandle; this.parent = parent; this.validate = validate; this.created = created;
        negative = kind == EnrollmentPrivateRecordKind.Revocation;
        maximum = kind == EnrollmentPrivateRecordKind.Evidence ? 16384 : 4096;
    }

    public byte[] Read()
    {
        RequireUsable();
        return completed?.ToArray() ?? SecurePrivateStorageRoot.ReadBounded(file, maximum);
    }

    public void CompletePositive(byte[] validatedBytes)
    {
        ArgumentNullException.ThrowIfNull(validatedBytes);
        RequireUsable();
        if (negative || validatedBytes.Length is < 1 || validatedBytes.Length > maximum)
            throw new InvalidDataException("Invalid positive enrollment record completion.");
        if (completed is not null)
        {
            if (!completed.AsSpan().SequenceEqual(validatedBytes)) throw new InvalidDataException("Immutable record changed.");
            return;
        }
        // The caller has already performed full per-kind semantic verification.
        // Existing bytes may be reasserted exactly, never repaired or resized.
        var snapshot = validatedBytes.ToArray();
        validate();
        if (!(created && file.Length == 0) && !Read().AsSpan().SequenceEqual(snapshot))
            throw new InvalidDataException("Existing immutable enrollment bytes differ.");
        try
        {
            file.Position = 0;
            file.Write(snapshot); // FileStream completes the requested span or throws.
            file.Flush();
            if (!SecurePrivateStorageRoot.ReadBounded(file, maximum).AsSpan().SequenceEqual(snapshot))
                throw new IOException("Enrollment record write verification failed.");
            PrivateStorageNative.FlushRecord(file.SafeFileHandle);
            validate();
            if (file.Length != snapshot.Length) throw new IOException("Enrollment record length changed.");
            completed = snapshot;
        }
        catch { failed = true; throw; } // A later successful flush cannot erase a reported error.
    }

    public void CompleteDenial()
    {
        RequireUsable();
        if (!negative) throw new InvalidOperationException("Positive records cannot use presence-only completion.");
        if (denialCompleted) return;
        try
        {
            validate();
            if (file.Length > maximum) throw new InvalidDataException("Oversized enrollment tombstone.");
            file.Flush();
            PrivateStorageNative.FlushRecord(file.SafeFileHandle);
            validate();
            denialCompleted = true; // Empty/torn contents are still durable denial.
        }
        catch { failed = true; throw; }
    }

    private void RequireUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failed) throw new IOException("Enrollment record is quarantined after an I/O failure.");
    }
    public void Dispose()
    {
        if (disposed) return;
        try { file.Dispose(); }
        finally
        {
            // All operations are synchronous and the cold owner has joined them.
            // Even a final managed-buffer flush failure cannot strand this handle.
            handle.Dispose(); disposed = true; parent.Dispose();
        }
    }
}
