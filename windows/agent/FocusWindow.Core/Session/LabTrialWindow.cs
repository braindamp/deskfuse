namespace FocusWindow.Core.Session;

// Monotonic experiment accounting, independent of audio/device clocks.
public sealed class LabTrialWindow(TimeProvider? provider=null)
{
    private readonly TimeProvider _clock=provider??TimeProvider.System;
    private readonly object _gate=new();
    private long? _started,_first,_ended;
    private string? _reason;
    public void Arm(){lock(_gate)_started??=_clock.GetTimestamp();}
    public bool HasPcm {get {lock(_gate)return _first.HasValue;}}
    public bool FirstPcm(){lock(_gate){if(_first.HasValue||_ended.HasValue)return false;_first=_clock.GetTimestamp();return true;}}
    public void End(string reason,bool failure=false){lock(_gate){_ended??=_clock.GetTimestamp();if(_reason is null||failure)_reason=reason;}}
    public (double? SetupMs,double WindowMs,double SourceLifetimeMs,string Reason) Snapshot(){lock(_gate){
        var now=_clock.GetTimestamp();
        return (_started.HasValue?_clock.GetElapsedTime(_started.Value,_first??_ended??now).TotalMilliseconds:null,
            _first.HasValue?_clock.GetElapsedTime(_first.Value,_ended??now).TotalMilliseconds:0,
            _first.HasValue?_clock.GetElapsedTime(_first.Value,now).TotalMilliseconds:0,_reason??"running");
    }}
}
