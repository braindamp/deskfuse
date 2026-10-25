using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FocusWindow.Core.Session;

namespace FocusWindow.Agent;

internal sealed class NativeAudioProcess : INativeAudioProcess
{
    private readonly Process _process;
    private readonly string _directory;
    private Task? _output;
    private Task? _errors;
    public int Port { get; private set; }
    public Task Completion => _process.WaitForExitAsync();
    private NativeAudioProcess(Process process, string directory) { _process=process; _directory=directory; }
    public static async Task<INativeAudioProcess> StartAsync(string executable, string capture,string sourceDll,string backend,string period,
        NativeAudioLaunch launch, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(capture))
            throw new ArgumentException("Native executables require explicit absolute paths.");
        if (backend is not ("committed" or "loopback") || period is not ("reference" or "minimum") ||
            !Path.IsPathFullyQualified(sourceDll)) throw new ArgumentException("Explicit valid native source policy required.");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var directory = Path.Combine(Path.GetTempPath(), "DeskFuse-audio-"+Guid.NewGuid().ToString("N"));
        new DirectoryInfo(directory).Create(security);
        var process = new Process { StartInfo = new ProcessStartInfo(executable) {
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
            RedirectStandardOutput=true, RedirectStandardError=true } };
        process.StartInfo.ArgumentList.Add("session-stdin");
        var owner = new NativeAudioProcess(process,directory);
        try
        {
            var ca=Path.Combine(directory,"ca.pem"); var cert=Path.Combine(directory,"server.pem");
            var key=Path.Combine(directory,"server.key"); var peer=Path.Combine(directory,"phone.der");
            await File.WriteAllTextAsync(ca,launch.CaPem,ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(cert,launch.ServerPem,ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(key,launch.ServerKeyPem,ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(peer,launch.ClientDer,ct).ConfigureAwait(false);
            process.Start();
            // Only this child inherits the anonymous control pipe. No secret
            // key, cert authorization, or peer identity appears in arguments.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {
                version=1, role="server", bind="0.0.0.0:0", remote=launch.RemoteIp,
                ca, cert, key, peerDer=peer, capture, peerId=launch.PeerId,
                seconds=1800, backend, sourceDll, enginePeriod=period })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            owner._errors=DrainAsync(process.StandardError); // no output on real-time paths
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var line=await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("Native owner exited before binding its socket.");
                if (!line.StartsWith("NATIVE_LISTEN ",StringComparison.Ordinal)) continue;
                if (!int.TryParse(line[14..],out var port) || port is <1 or >65535)
                    throw new IOException("Invalid native listener identity.");
                owner.Port=port; break;
            }
            owner._output=DrainAsync(process.StandardOutput);
            return owner;
        }
        catch { await owner.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static async Task DrainAsync(StreamReader reader)
    { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process.Id != 0 && !_process.HasExited)
            {
                _process.StandardInput.Close(); // native shutdown readiness, orderly join
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) {
                    _process.Kill(); // broker job confirms capture death before endpoint removal
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException) { /* launch failed before a process existed */ }
        finally
        {
            if (_output is not null) await _output.ConfigureAwait(false);
            if (_errors is not null) await _errors.ConfigureAwait(false);
            _process.Dispose();
            var resolved=Path.GetFullPath(_directory);
            var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent,StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("DeskFuse-audio-",StringComparison.Ordinal) ||
                (File.GetAttributes(resolved)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Refusing unexpected session directory cleanup.");
            Directory.Delete(resolved,recursive:true);
        }
    }
}
