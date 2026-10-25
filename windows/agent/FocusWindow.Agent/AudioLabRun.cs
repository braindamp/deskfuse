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
    private int _launched,_ready,_androidSeen;
    private long _queued;
    private volatile bool _failed,_endSnapshot;
    private readonly LabTrialWindow _window=new();
    private Task? _exit;
    public NativeAudioLab Control {get;}
    public CancellationToken Token=>_stop.Token;
    public AudioLabRun(int seconds,CancellationToken shutdown,string runId,NativeOutputPolicy policy) {
        if(seconds is not (30 or 120))throw new ArgumentException("Lab duration must be 30 or 120 seconds.");
        _seconds=seconds;_stop=CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Control=new NativeAudioLab(runId,policy,value=>{
            Interlocked.Exchange(ref _androidSeen,1);
            Console.WriteLine("LAB_ANDROID "+value.ToJsonString());
        });
    }
    public async Task ArmAsync() {
        if(await Task.Run(Console.ReadLine,Token).WaitAsync(Token).ConfigureAwait(false)!="start")throw new IOException("Lab was not armed by its owner.");
        _window.Arm();_ = SetupDeadlineAsync();_ = WatchOwnerAsync();
    }
    private async Task WatchOwnerAsync() {
        try {await Task.Run(Console.ReadLine,Token).WaitAsync(Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return;}catch(IOException){}
        _window.End("owner_disconnected",true);_stop.Cancel();
    }
    private async Task SetupDeadlineAsync() {
        try {
            await Task.Delay(TimeSpan.FromSeconds(60),Token).ConfigureAwait(false);
            if(!_window.HasPcm){_failed=true;_window.End("no_pcm_setup_timeout",true);Console.WriteLine("LAB_BLOCKED no fresh native PCM within setup deadline");_stop.Cancel();}
        }catch(OperationCanceledException){}
    }
    private async Task StreamingWindowAsync() {
        try {
            await Task.Delay(TimeSpan.FromSeconds(_seconds),Token).ConfigureAwait(false);
            _window.End("window_complete");
            _endSnapshot=await Control.RequestFinalAsync(Token).ConfigureAwait(false);
            _stop.Cancel();
        }catch(OperationCanceledException){}
    }
    public async Task<INativeAudioProcess> StartAsync(string exe,string capture,string dll,string backend,string period,NativeAudioLaunch launch,CancellationToken ct) {
        if(Interlocked.Exchange(ref _launched,1)!=0)throw new InvalidOperationException("One owner per Lab run.");
        Console.WriteLine($"LAB_SOURCE backend={backend} engine_period={period} authenticated=1 qualified=0");
        var process=await NativeAudioProcess.StartAsync(exe,capture,dll,backend,period,launch,ct,Observe,MediaReady).ConfigureAwait(false);
        _exit=StopOnExitAsync(process.Completion);return process;
    }
    private async Task StopOnExitAsync(Task completion) {
        try {await completion.ConfigureAwait(false);if(!Token.IsCancellationRequested){_failed=true;_window.End("native_early_exit",true);}}
        catch(Exception error){_failed=true;_window.End("native_failure",true);Console.WriteLine("LAB_BLOCKED native owner: "+error.Message);}
        finally {if(!Token.IsCancellationRequested)_stop.Cancel();}
    }
    public void Observe(string line) {
        Console.WriteLine(line);
        if(line.StartsWith("NATIVE_PCM ",StringComparison.Ordinal)||line.StartsWith("DONE committed source ",StringComparison.Ordinal)||line.StartsWith("DONE Windows WASAPI source ",StringComparison.Ordinal)) {
            var m=Regex.Match(line,@"\b(?:queued|real_pcm_blocks_queued)=(\d+)\b");
            if(m.Success&&long.TryParse(m.Groups[1].Value,out var queued)) {
                Interlocked.Exchange(ref _queued,queued);
                if(queued>0&&line.StartsWith("NATIVE_PCM ",StringComparison.Ordinal)&&_window.FirstPcm()){
                    Console.WriteLine("LAB_STREAMING first_fresh_admission=1 audible_duration_measured=0");_ = StreamingWindowAsync();
                }
            }
        }
    }
    public void MediaReady(){if(Interlocked.Exchange(ref _ready,1)==0)Console.WriteLine("LAB_MEDIA_READY authenticated_volume_ack=1 qualified=0");}
    public async Task<int> CompleteAsync() {
        if(_exit is not null)await _exit.ConfigureAwait(false);
        _window.End("cancelled");var t=_window.Snapshot();
        var ok=Volatile.Read(ref _ready)!=0&&Interlocked.Read(ref _queued)>0&&!_failed&&t.Reason=="window_complete";
        Console.WriteLine("LAB_TRIAL_RESULT "+System.Text.Json.JsonSerializer.Serialize(new {
            version=1,run_id=Control.RunId,media_ready=Volatile.Read(ref _ready),queued=Interlocked.Read(ref _queued),
            setup_duration_ms=t.SetupMs,streaming_window_ms=t.WindowMs,source_lifetime_ms=t.SourceLifetimeMs,
            termination_reason=t.Reason,end_snapshot_available=_endSnapshot,android_observation_available=Volatile.Read(ref _androidSeen)!=0,
            audible_duration_ms=(double?)null,physical_qualified=false,exit_code=ok?0:2 }));
        Console.WriteLine($"LAB_RESULT media_ready={Volatile.Read(ref _ready)} queued={Interlocked.Read(ref _queued)} qualified=0 exit={(ok?0:2)}");return ok?0:2;
    }
    public void Dispose(){_stop.Cancel();}
}
