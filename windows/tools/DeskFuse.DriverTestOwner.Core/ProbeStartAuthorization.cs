using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskFuse.DriverTestOwner.Core;

/// <summary>One-shot permission for the exact parked probe incarnation, never a device grant.</summary>
public sealed record ProbeStartAuthorization(int Version, Guid RunId, Guid ProbeNonce, int Pid, long CreationFileTime)
{
    public const string FileName = "probe-start.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public void Require(Guid runId, Guid probeNonce, int pid, long creationFileTime)
    {
        if (Version != 1 || RunId == Guid.Empty || ProbeNonce == Guid.Empty || RunId != runId ||
            ProbeNonce != probeNonce || Pid != pid || CreationFileTime != creationFileTime)
            throw new InvalidDataException("Probe start permission belongs to another process incarnation.");
    }

    public static async Task WaitForCurrentProcessAsync(string path, Guid runId, Guid probeNonce, CancellationToken cancellation)
    {
        using var current = Process.GetCurrentProcess();
        var created = current.StartTime.ToUniversalTime().ToFileTimeUtc();
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid probe start permission length.");
                var bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
                var permission = JsonSerializer.Deserialize<ProbeStartAuthorization>(bytes, Json)
                    ?? throw new InvalidDataException("Missing probe start permission.");
                permission.Require(runId, probeNonce, current.Id, created);
                return;
            }
            catch (FileNotFoundException) { }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException("The independent owner did not authorize this parked probe. No display operations were started.");
    }
}
