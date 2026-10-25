using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Session;

public static class AudioLabAdmission
{
    public static bool Allows(ClientHello hello, string? authenticatedDeviceId, bool occupied) =>
        !occupied && hello.Role is ("media" or "accessory") && hello.NativeAudioVersion == 2 &&
        !string.IsNullOrEmpty(authenticatedDeviceId) && authenticatedDeviceId == hello.Profile.DeviceId &&
        hello.Capabilities is { Count: 1 } && hello.Capabilities[0] == DeviceCapabilities.Speakers;
}
