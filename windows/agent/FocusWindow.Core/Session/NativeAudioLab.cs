using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FocusWindow.Core.Session;

public sealed record NativeOutputPolicy(int BufferBursts=0,int SharingPolicy=0,int UsagePolicy=0,
    int ReserveFrames=1440,string Modes="f1c1r1")
{
    public bool SupportedBy(int labVersion)=>labVersion==1||IsReference;
    public bool IsReference => this==new NativeOutputPolicy();
    public void Validate() {
        if (BufferBursts is not (0 or 2 or 3) || SharingPolicy is <0 or >1 || UsagePolicy is <0 or >1 ||
            ReserveFrames is not (1440 or 1200 or 960 or 720 or 480) || !Regex.IsMatch(Modes,"^f[01]c[01]r[01]$"))
            throw new ArgumentException("Unsupported native output policy");
    }
    public JsonObject ToJson() {Validate();return new() { ["version"]=1,["buffer_bursts"]=BufferBursts,
        ["sharing_policy"]=SharingPolicy,["usage_policy"]=UsagePolicy,["reserve_frames"]=ReserveFrames,["modes"]=Modes };}
}

// Control-plane hook only. Immutable policy; a route owns its final-observation
// request until disposal, with no effect on endpoint or PCM ownership.
public sealed class NativeAudioLab(string runId,NativeOutputPolicy policy,Action<JsonObject> observe)
{
    public string RunId {get;}=Guid.TryParseExact(runId,"N",out _)?runId:throw new ArgumentException("Invalid run ID");
    public NativeOutputPolicy Policy {get;}=Checked(policy);
    private static NativeOutputPolicy Checked(NativeOutputPolicy p) {p.Validate();return p;}
    private readonly object _gate=new();
    private Func<CancellationToken,Task<bool>>? _final;
    public void Report(JsonObject value)=>observe((JsonObject)value.DeepClone());
    public void Bind(Func<CancellationToken,Task<bool>> request) {lock(_gate)_final=request;}
    public void Unbind(Func<CancellationToken,Task<bool>> request) {lock(_gate)if(_final==request)_final=null;}
    public Task<bool> RequestFinalAsync(CancellationToken ct) {
        Func<CancellationToken,Task<bool>>? request;lock(_gate)request=_final;
        return request?.Invoke(ct)??Task.FromResult(false);
    }
}

public sealed class NativeObservationFence(TimeProvider? clock=null)
{
    private readonly TimeProvider _clock=clock??TimeProvider.System;
    private long? _lastAt;
    private static readonly HashSet<string> Signed=new(("stream_error drift_ppm clock_result timestamp_result format").Split(' '),StringComparer.Ordinal);
    private long _sequence,_generation,_sampleSequence,_playout;
    private static readonly HashSet<string> Fields=new(("owner_generation sample_seq sample_mono_ns accepted_frames rendered_frames silence_frames callback_count overflow_packets queued_frames underrun_events reprime_events stream_error sample_rate channels drift_ppm xrun_count clipped_samples queued_playable_frames primed playout_generation current_source_frame source_valid last_callback_frames requested_recovery applied_recovery received_packets source_gaps late_packets recoveries source_missing_frames output_generation clock_result timestamp_result timestamp_frame timestamp_ns written_before written_after buffer_frames capacity_frames device sharing performance frames_per_burst actual_usage format reserve_frames render_epoch render_stream render_connection callback_seq discarded_frames concealed_frames").Split(' '),StringComparer.Ordinal);
    public bool Accept(long sequence,JsonObject snapshot,bool requested) {
        if (sequence<=_sequence || snapshot.Count>60 || (!requested&&_lastAt.HasValue&&_clock.GetElapsedTime(_lastAt.Value).TotalMilliseconds<250)) return false;
        try {
            if(snapshot["version"]?.GetValue<int>()!=1)return false;
            foreach(var pair in snapshot) {
                if (pair.Key=="version")continue;
                if (pair.Key=="complete") {if(pair.Value is not JsonValue v || !v.TryGetValue<bool>(out _))return false;continue;}
                if (!Fields.Contains(pair.Key))return false;
                if (pair.Value is not null) {
                    if(pair.Value is not JsonValue n || !n.TryGetValue<long>(out var number) ||
                        number < (Signed.Contains(pair.Key)?int.MinValue:0) ||
                        (number>9_007_199_254_740_991 && !pair.Key.EndsWith("_ns",StringComparison.Ordinal)))return false;
                    if(Signed.Contains(pair.Key)&&number>int.MaxValue)return false;
                }
            }
            var generation=snapshot["owner_generation"]!.GetValue<long>();var sample=snapshot["sample_seq"]!.GetValue<long>();
            var playout=snapshot["playout_generation"]?.GetValue<long>()??_playout;
            if (generation<=0 || sample<=0 || (_generation!=0 && generation!=_generation) ||
                sample<_sampleSequence || (!requested&&sample==_sampleSequence) || playout<_playout) return false;
            foreach(var key in new[]{"accepted_frames","rendered_frames","underrun_events","reprime_events","received_packets"})
                if (snapshot[key]?.GetValue<long>() is not >=0) return false;
            if (snapshot["sample_rate"]?.GetValue<long>() is not (>=0 and <=384000) || snapshot["channels"]?.GetValue<long>() is not (>=0 and <=32))return false;
            foreach(var key in new[]{"primed","source_valid"}) if(snapshot[key]?.GetValue<long>() is long flag && flag is not (0 or 1))return false;
            foreach(var key in new[]{"buffer_frames","capacity_frames","frames_per_burst","last_callback_frames"})
                if(snapshot[key]?.GetValue<long>() is long frames && frames is <0 or >10_000_000)return false;
            _lastAt=_clock.GetTimestamp();_sequence=sequence;_generation=generation;_sampleSequence=sample;_playout=playout;return true;
        }catch(Exception e)when(e is InvalidOperationException or FormatException or NullReferenceException or OverflowException){return false;}
    }
}
