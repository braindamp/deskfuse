namespace FocusWindow.Core.Session;

/// <summary>
/// The wording a person sees.
///
/// The host knows a great deal about how it is built: driver names, device
/// instance ids, Win32 error text, the names of our own components. None of
/// that is the user's business and none of it is on their phone. What the
/// user is told is the *effect* of the problem and what they can do about
/// it; the implementation detail stays in the host log and, on the phone,
/// behind a details tap.
///
/// A stage is a stable code. The sentence for it lives here, so the phone
/// can never be shown a raw implementation string by accident.
/// </summary>
public static class UserFacing
{
    /// <summary>The sentence for a failure stage.</summary>
    public static string For(string stage) => stage switch
    {
        "display" or "display-owned" or "display-remove" or "density" or "profile"
            or "preflight" or "probe-window" or "source" or "driver" =>
            "Your PC could not open a window for this phone.",
        "capture" or "gpu" or "gpu-abi" =>
            "Your PC could not prepare the picture for this phone.",
        "physical-display-guard" =>
            "Your PC's screen arrangement changed, so the session stopped.",
        "version" =>
            "This phone and the PC are running different versions of DeskFuse.",
        "approval" =>
            "The person at the PC declined this connection.",
        "speakers" =>
            "Your PC could not set up a speaker for this phone.",
        _ =>
            "Something went wrong on your PC. Try again.",
    };

    /// <summary>Progress messages, in the user's terms.</summary>
    public static string Preparing => "Getting your PC ready to show a window on this phone.";

    public static string Starting => "Opening the app you chose on your phone.";
}
