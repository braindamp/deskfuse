using System.Security.Cryptography;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Session;

/// <summary>
/// Explicit phone -> PC upload. Only an authenticated phone session can call this.
/// After explicit host approval, the user may choose any writable Windows folder.
/// Without full-file-access approval, the host forces the interim Incoming directory.
/// Windows ACLs remain authoritative. File names are always leaf names; no execution.
/// One bounded transfer per session, streamed directly to a temporary file, SHA-256 verified
/// before the atomic same-directory move. Partial files are removed on disconnect or failure.
/// </summary>
public sealed class FileUploadReceiver : IDisposable
{
    public const long MaxBytes = 32L * 1024 * 1024;
    public const int ChunkBytes = 24 * 1024;
    private readonly string _directory;
    private readonly bool _allowUserPaths;
    private FileStream? _stream;
    private IncrementalHash? _hash;
    private string? _activeId;
    private string? _tempPath;
    private string? _filename;
    private string? _activeDirectory;
    private bool _openOnPc;
    private string? _completedOpenPath;
    // The request must be explicit; it is never inferred from the file extension.
    // Block executable and script association routes instead of blindly ShellExecuting
    // an arbitrary filename supplied by a remote client.
    private static readonly HashSet<string> OpenableDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff",
      ".pdf", ".txt", ".md", ".csv", ".json", ".log",
      ".mp3", ".wav", ".mp4", ".mkv", ".docx", ".xlsx", ".pptx", ".odt", ".ods" };
    private long _expected;
    private long _written;

    public FileUploadReceiver(string directory, bool allowUserPaths = false)
    {
        _directory = directory;
        _allowUserPaths = allowUserPaths;
    }

    /// <returns>A safe, non-sensitive status message, or null while receiving chunks.</returns>
    public string? Handle(ClientFile request)
    {
        try
        {
            return request.Action switch
            {
                "begin" => Begin(request),
                "chunk" => Chunk(request),
                "end" => End(request),
                "cancel" => Cancel(request),
                _ => throw new InvalidDataException("Unknown transfer action"),
            };
        }
        catch
        {
            Reset();
            throw;
        }
    }

    // Only the completed, digest-verified upload may be given to ShellExecute.
    // Consuming the path prevents replaying an old successful action on a new upload.
    public string? TakeCompletedOpenPath() {
        var path = _completedOpenPath;
        _completedOpenPath = null;
        return path;
    }

    private string Begin(ClientFile r)
    {
        if (_activeId is not null) throw new InvalidDataException("An upload is already in progress");
        if (!Guid.TryParseExact(r.Id, "N", out _)) throw new InvalidDataException("Invalid upload ID");
        if (r.Size < -1 || r.Size > MaxBytes) throw new InvalidDataException("Upload exceeds 32 MiB");
        _completedOpenPath = null;
        _filename = SafeName(r.Name ?? "");
        if (r.OpenOnPc) {
            if (!_allowUserPaths) throw new InvalidDataException("PC approval required to open a remote file");
            if (!string.IsNullOrWhiteSpace(r.Path)) throw new InvalidDataException("Open-on-PC uses staged files, not arbitrary destinations");
            if (!OpenableDocumentExtensions.Contains(Path.GetExtension(_filename)))
                throw new InvalidDataException("File type cannot be opened remotely");
        }
        _openOnPc = r.OpenOnPc;
        var destination = _allowUserPaths && !string.IsNullOrWhiteSpace(r.Path)
            ? UserFileBrowser.FullPath(r.Path) : _directory;
        if (_allowUserPaths && !string.IsNullOrWhiteSpace(r.Path) && !Directory.Exists(destination))
            throw new DirectoryNotFoundException("Upload destination does not exist");
        Directory.CreateDirectory(destination);
        _activeDirectory = destination;
        _activeId = r.Id;
        _expected = r.Size;
        _written = 0;
        _tempPath = Path.Combine(_activeDirectory!, ".deskfuse-" + r.Id + ".part");
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _stream = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            32 * 1024, FileOptions.SequentialScan);
        return "Receiving " + _filename;
    }

    private string? Chunk(ClientFile r)
    {
        VerifyId(r.Id);
        if (string.IsNullOrEmpty(r.Base64) || r.Base64.Length > ((ChunkBytes + 2) / 3) * 4)
            throw new InvalidDataException("Invalid file chunk length");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(r.Base64); }
        catch (FormatException) { throw new InvalidDataException("Invalid file chunk encoding"); }
        if (bytes.Length is < 1 or > ChunkBytes || _written + bytes.Length > MaxBytes ||
            (_expected >= 0 && _written + bytes.Length > _expected))
            throw new InvalidDataException("Upload exceeded advertised or maximum size");
        _stream!.Write(bytes);
        _hash!.AppendData(bytes);
        _written += bytes.Length;
        return null;
    }

    private string End(ClientFile r)
    {
        VerifyId(r.Id);
        if (_expected >= 0 && _written != _expected) throw new InvalidDataException("Upload size mismatch");
        var digest = Convert.ToHexString(_hash!.GetHashAndReset());
        if (r.Sha256 is null || r.Sha256.Length != 64 ||
            !string.Equals(digest, r.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Upload SHA-256 mismatch");
        _stream!.Flush(true);
        _stream.Dispose();
        _stream = null;
        var completed = Path.Combine(_activeDirectory!, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") +
            Guid.NewGuid().ToString("N")[..8] + "-" + _filename);
        File.Move(_tempPath!, completed); // same volume; no incomplete file is exposed
        var summary = "Saved " + _filename + " (" + _written + " bytes) in " + _activeDirectory;
        var openRequested = _openOnPc;
        _tempPath = null;
        Reset();
        if (openRequested) _completedOpenPath = completed;
        return summary;
    }

    private string Cancel(ClientFile r)
    {
        VerifyId(r.Id);
        Reset();
        return "Upload cancelled";
    }

    private void VerifyId(string id)
    {
        if (_activeId is null || !string.Equals(_activeId, id, StringComparison.Ordinal))
            throw new InvalidDataException("No matching active upload");
    }

    private static string SafeName(string name)
    {
        // Accept simple leaf names only. This also prevents Windows reserved device names,
        // drive letters, ADS, absolute paths, separators and traversal from surviving.
        name = name.Trim();
        if (name.Length is < 1 or > 100 || name is "." or ".." ||
            name.StartsWith('.') || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '|', '?', '*', '"' }) >= 0 ||
            name.Any(ch => char.IsControl(ch)))
            throw new InvalidDataException("Invalid upload filename");
        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ');
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reserved.Contains(stem)) throw new InvalidDataException("Windows reserved filename");
        return name;
    }

    private void Reset()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        _stream = null;
        _hash?.Dispose();
        _hash = null;
        if (_tempPath is not null)
        {
            try { File.Delete(_tempPath); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _tempPath = null;
        _openOnPc = false;
        _activeId = null;
        _filename = null;
        _activeDirectory = null;
        _written = 0;
        _expected = -1;
    }
    public void Dispose() => Reset();
}
