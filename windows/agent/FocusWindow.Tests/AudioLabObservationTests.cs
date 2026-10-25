using System.Text.Json.Nodes;
using FocusWindow.Core.Session;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioLabObservationTests
{
    private sealed class Clock : TimeProvider {
        public long Now;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Now;
    }
    private static JsonObject Sample(long sequence=1,long owner=1,long playout=1)=>JsonNode.Parse(
        $$"""{"version":1,"owner_generation":{{owner}},"sample_seq":{{sequence}},"accepted_frames":240,"rendered_frames":120,"underrun_events":0,"reprime_events":0,"received_packets":1,"sample_rate":48000,"channels":2,"playout_generation":{{playout}},"xrun_count":null,"sample_mono_ns":10000000000000000} """)!.AsObject();

    [Fact]
    public void Window_starts_once_at_PCM_and_setup_is_bounded_independently_of_readiness() {
        var clock=new Clock();var window=new LabTrialWindow(clock);window.Arm();
        clock.Now=25000;Assert.False(window.HasPcm);Assert.Equal(25000,window.Snapshot().SetupMs);
        Assert.True(window.FirstPcm());Assert.False(window.FirstPcm());
        clock.Now=55000;window.End("window_complete");clock.Now=55800;
        var result=window.Snapshot();Assert.Equal(25000,result.SetupMs);Assert.Equal(30000,result.WindowMs);
        Assert.Equal(30800,result.SourceLifetimeMs);Assert.Equal("window_complete",result.Reason);
        window.End("cancelled");Assert.Equal("window_complete",window.Snapshot().Reason);
        window.End("native_failure",true);Assert.Equal("native_failure",window.Snapshot().Reason);
        var empty=new LabTrialWindow(clock);empty.Arm();clock.Now+=60000;empty.End("no_pcm_setup_timeout",true);
        Assert.Equal(60000,empty.Snapshot().SetupMs);Assert.Equal(0,empty.Snapshot().WindowMs);
        Assert.False(empty.FirstPcm());
    }
    [Fact]
    public void Observation_generation_replay_bounds_unknowns_and_final_copy_are_fenced() {
        var clock=new Clock();var fence=new NativeObservationFence(clock);
        Assert.True(fence.Accept(1,Sample(),false));
        Assert.False(fence.Accept(1,Sample(),true));
        clock.Now=500;
        Assert.False(fence.Accept(2,Sample(2,2),false));
        Assert.False(fence.Accept(2,Sample(),false));
        Assert.True(fence.Accept(2,Sample(),true)); // bounded end request can copy latest owner sample
        Assert.False(fence.Accept(3,Sample(2),false)); // rate bound
        clock.Now=1000;
        var bad=Sample(2);bad["underrun_events"]=-1;Assert.False(fence.Accept(3,bad,false));
        bad=Sample(2);bad["secret"]="not numeric";Assert.False(fence.Accept(3,bad,false));
        bad=Sample(2);bad["version"]="one";Assert.False(fence.Accept(3,bad,false));
        bad=Sample(2);bad["channels"]=1000;Assert.False(fence.Accept(3,bad,false));
        Assert.False(fence.Accept(3,Sample(2,1,0),false));
        Assert.True(fence.Accept(3,Sample(2),false));
    }
    [Fact]
    public void Output_policy_preserves_older_reference_clients_but_not_unsupported_tuning() {
        var reference=new NativeOutputPolicy();reference.Validate();Assert.True(reference.SupportedBy(0));
        var candidate=new NativeOutputPolicy(ReserveFrames:960);candidate.Validate();
        Assert.True(candidate.SupportedBy(1));Assert.False(candidate.SupportedBy(0));
        Assert.Throws<ArgumentException>(()=>new NativeOutputPolicy(ReserveFrames:900).Validate());
        Assert.Throws<ArgumentException>(()=>new NativeOutputPolicy(BufferBursts:1).Validate());
        Assert.Equal(1,candidate.ToJson()["version"]!.GetValue<int>());
    }
}
