using FocusWindow.Core.Session;
using FocusWindow.Core.Media;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DeviceBridgeTests
{
    [Fact]
    public void Surround_negotiation_uses_receiver_channel_count_and_exact_speaker_masks()
    {
        Assert.Equal(PcmSpeakerLayout.FiveOneBack,
            PcmSpeakerLayout.Select(0x63f, ["5.1_back", "stereo"]));
        Assert.Equal(6, PcmSpeakerLayout.Select(0x63f, ["5.1_side"]).Channels);
        Assert.Equal(PcmSpeakerLayout.SevenOne, PcmSpeakerLayout.Select(0x63f, ["7.1_surround"]));
        Assert.Equal(PcmSpeakerLayout.Stereo, PcmSpeakerLayout.Select(0x3, ["7.1_surround", "stereo"]));
        Assert.Equal(PcmSpeakerLayout.Stereo, PcmSpeakerLayout.Select(0x3f, []));
    }
    [Fact]
    public void Multiple_accessories_compose_capabilities_while_display_admission_remains_exclusive()
    {
        var peers = new DevicePeerRegistry("host-test");
        var displays = new ExclusiveVirtualDisplayAdmission();
        using var display = displays.TryAcquire("tablet");
        Assert.NotNull(display);
        using var camera = peers.Attach("camera-phone", "Camera and microphone", default,
            [DeviceCapabilities.Camera, DeviceCapabilities.Microphone]);
        using var input = peers.Attach("input-phone", "Touchpad", default,
            [DeviceCapabilities.Touchpad, DeviceCapabilities.Keyboard]);
        Assert.Equal(2, peers.Online.Count);
        Assert.Null(displays.TryAcquire("second-tablet"));
        Assert.Equal(2, camera.Peer.RequestedCapabilities.Count);
        Assert.Equal(2, input.Peer.RequestedCapabilities.Count);
        Assert.Throws<ArgumentException>(() => peers.Attach("bad", "Bad", default,
            [DeviceCapabilities.Display]));
    }

    [Fact]
    public void Reconnect_cancels_only_the_previous_enrollment_lease()
    {
        var peers = new DevicePeerRegistry("host-test");
        using var old = peers.Attach("phone-a", "A", default, [DeviceCapabilities.Microphone]);
        using var other = peers.Attach("phone-b", "B", default, [DeviceCapabilities.Speakers]);
        using var current = peers.Attach("phone-a", "A again", default,
            [DeviceCapabilities.Microphone, DeviceCapabilities.Camera]);
        Assert.True(old.Cancellation.IsCancellationRequested);
        Assert.False(other.Cancellation.IsCancellationRequested);
        Assert.Equal(old.Peer.Endpoints, current.Peer.Endpoints);
        old.Dispose();
        Assert.Equal(2, peers.Online.Count);
        Assert.Contains(peers.Online, peer => peer.Label == "A again");
        Assert.NotEqual(current.Peer.Endpoints, other.Peer.Endpoints);
        Assert.NotEqual(current.Peer.Endpoints, new DevicePeerRegistry("other-host").Identities("phone-a"));
    }

    [Fact]
    public void Display_lease_is_reusable_and_double_disposal_cannot_release_a_new_owner()
    {
        var displays = new ExclusiveVirtualDisplayAdmission();
        var first = displays.TryAcquire("first")!;
        first.Dispose();
        using var second = displays.TryAcquire("second");
        Assert.NotNull(second);
        first.Dispose();
        Assert.Null(displays.TryAcquire("third"));
    }
}
