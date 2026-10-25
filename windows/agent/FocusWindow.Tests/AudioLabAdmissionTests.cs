using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioLabAdmissionTests
{
    private static ClientHello Speaker() => new(3,"test",new PhoneProfile(1,"phone","Phone",
        1080,2400,3,0,0,0,0,1,60,170,"PORTRAIT","PHONE"),"accessory",[DeviceCapabilities.Speakers],2);

    [Fact]
    public void Only_one_enrolled_native_speaker_and_no_other_capabilities()
    {
        var hello=Speaker();
        Assert.True(AudioLabAdmission.Allows(hello,"phone",false));
        Assert.False(AudioLabAdmission.Allows(hello,null,false));
        Assert.False(AudioLabAdmission.Allows(hello,"another-phone",false));
        Assert.False(AudioLabAdmission.Allows(hello,"phone",true));
        Assert.False(AudioLabAdmission.Allows(hello with { Role="display" },"phone",false));
        Assert.False(AudioLabAdmission.Allows(hello with { NativeAudioVersion=1 },"phone",false));
        Assert.False(AudioLabAdmission.Allows(hello with { Capabilities=null },"phone",false));
        Assert.False(AudioLabAdmission.Allows(hello with { Capabilities=[DeviceCapabilities.Speakers,DeviceCapabilities.Microphone] },"phone",false));
    }
}
