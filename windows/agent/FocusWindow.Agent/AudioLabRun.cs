using System.Text.RegularExpressions;
using FocusWindow.Core;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;

namespace FocusWindow.Agent;

// Uses the product enrollment store and approval. No laboratory CA, peer hash,
// enrollment prompts, desktop sessions or input/file capabilities are admitted.
internal sealed class AudioLabApproval(ILog log) : IApprovalPolicy, IEnrollmentApprovalPolicy
{
    private readonly ConsoleApproval _existing = new(false, log);
    public Task<bool> ApproveAsync(string id, string label, string code, CancellationToken ct) =>
        Task.FromResult(_existing.WasApproved(id));
    public Task<bool> ApproveNewPairingAsync(string id, string label, string code, CancellationToken ct) =>
        Task.FromResult(false);
}

internal sealed class AudioLabRun : IDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly int _seconds;
    private int _launched, _ready;
    private long _queued;
    private volatile bool _timedOut, _failed;
    public CancellationToken Token => _stop.Token;
    private Task? _exit;
    public async Task<int> CompleteAsync() {
            if (_exit is not null) await _exit.ConfigureAwait(false);
            var ok=Volatile.Read(ref _ready)!=0 && Interlocked.Read(ref _queued)>0 && !_timedOut && !_failed;
            Console.WriteLine($"LAB_RESULT media_ready={Volatile.Read(ref _ready)} queued={Interlocked.Read(ref _queued)} qualified=0 exit={(ok?0:2)}");
            return ok?0:2;
    }
    public AudioLabRun(int seconds, CancellationToken shutdown)
    {
        if (seconds is not (30 or 120)) throw new ArgumentException("Lab duration must be 30 or 120 seconds.");
        _seconds=seconds;
        _stop=CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        // Includes phone reconnect, authorization, endpoint setup and volume ACK.
        _ = DeadlineAsync();
    }
    public async Task ArmAsync()
    {
        // Parent puts this process in a kill-on-close job before authorizing start.
        // EOF also requests orderly source/transport/lease retirement.
        if (await Task.Run(Console.ReadLine,Token).WaitAsync(Token).ConfigureAwait(false)!="start")
            throw new IOException("Audio Lab launch was not armed by its owner.");
        _ = WatchOwnerAsync();
    }
    private async Task WatchOwnerAsync()
    {
        try {await Task.Run(Console.ReadLine,Token).WaitAsync(Token).ConfigureAwait(false);}
        catch (OperationCanceledException) {return;}
        catch (IOException) { /* owner pipe failed; still retire the session */ }
        _stop.Cancel();
    }
    private async Task DeadlineAsync()
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(60),Token).ConfigureAwait(false);
            if (Volatile.Read(ref _ready)==0) {_timedOut=true;Console.WriteLine("LAB_BLOCKED authenticated source/media readiness timeout");_stop.Cancel();}
        } catch (OperationCanceledException) { }
    }
    public async Task<INativeAudioProcess> StartAsync(string exe,string capture,string dll,string backend,string period,
        NativeAudioLaunch launch,CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _launched,1)!=0) throw new InvalidOperationException("One authenticated owner per Lab run; start another run after cleanup.");
        Console.WriteLine($"LAB_SOURCE backend={backend} engine_period={period} authenticated=1 qualified=0");
        var process=await NativeAudioProcess.StartAsync(exe,capture,dll,backend,period,launch,ct,Observe,MediaReady).ConfigureAwait(false);
        _exit = StopOnExitAsync(process.Completion);
        return process;
    }
    private async Task StopOnExitAsync(Task completion)
    {
        try {await completion.ConfigureAwait(false); if (!Token.IsCancellationRequested) _failed=true;}
        catch (Exception error) {_failed=true;Console.WriteLine("LAB_BLOCKED native owner: "+error.Message);}
        finally {if (!Token.IsCancellationRequested) _stop.Cancel();}
    }
    public void Observe(string line)
    {
        Console.WriteLine(line);
        if (line.StartsWith("NATIVE_PCM ",StringComparison.Ordinal) || line.StartsWith("DONE committed source ",StringComparison.Ordinal) || line.StartsWith("DONE Windows WASAPI source ",StringComparison.Ordinal)) {
            var match=Regex.Match(line,@"\b(?:queued|real_pcm_blocks_queued)=(\d+)\b");
            if (match.Success && long.TryParse(match.Groups[1].Value,out var queued)) Interlocked.Exchange(ref _queued,queued);
        }
    }
    public void MediaReady()
    {
        if (Interlocked.Exchange(ref _ready,1)!=0) return;
        Console.WriteLine("LAB_MEDIA_READY authenticated_volume_ack=1 qualified=0");
        _stop.CancelAfter(TimeSpan.FromSeconds(_seconds));
    }
    public void Dispose() { _stop.Cancel(); /* callbacks may still be unwinding; no token handle disposal */ }
}
