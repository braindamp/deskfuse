using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
public sealed record HostOptions
{
    public int Port { get; init; } = 47810;
    public string Bind { get; init; } = "0.0.0.0";
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 20_000;
    public int MaxRefreshHz { get; init; } = 120;
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string HostName { get; init; } = Environment.MachineName;
    public string HostVersion { get; init; } = "0.3.0-dev";
    public string Channel { get; init; } = "dev";
    public bool DrawRulerProbe { get; init; }
    public bool NewPairing { get; init; }
    // --auto-approve must never imply full filesystem access.
    public bool AllowFullUserFiles { get; init; } = true;
    public bool SingleSession { get; init; }
    public bool CameraFreePairing { get; init; }
    /// <summary>Opt-in per-phone exact-DPR virtual backing display calibration.
    /// Remains experimental until real-device viewport/window UX is qualified.</summary>
    public bool AdaptiveDpiViewport { get; init; }
    // Separate PC-side authorization: a paired device cannot capture mixed PC
    // output without an explicit host launch flag. Default deny.
    public bool AllowPcAudio { get; init; }
    // Separate consent gate for forwarding Android microphone input.
    public bool AllowPhoneMicrophone { get; init; }
}
/// <summary>Decides whether a phone that just authenticated may use this PC (default: ask the user).</summary>
public interface IApprovalPolicy
{
    Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct);
}
public sealed class AutoApprove : IApprovalPolicy
{
    public Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct) =>
        Task.FromResult(true);
}
/// <summary>Explicit, text-only clipboard operations; no background clipboard monitoring.</summary>
public interface IClipboardBridge
{
    Task<string?> ReadTextAsync(CancellationToken ct);
    Task WriteTextAsync(string text, CancellationToken ct);
}
public sealed record WindowItem(string Id, string Title, string AppLabel);
/// <summary>
/// One explicitly requested loopback of the user's default Windows output.
/// Callbacks are synchronous; the sender must copy the transient audio memory.
/// </summary>
public interface IHostAudioCapture : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>How the capture is arriving, for diagnostics.</summary>
    string? Health => null;
    // Explicitly identified Windows speaker positions; not just channel count.
    string Layout => "stereo";
    void Start(Action<ReadOnlyMemory<byte>> onSamples);
}
/// <summary>
/// One phone's Windows playback endpoint: a real render device that Windows
/// lists as a selectable output, owned by one enrolled phone for the length
/// of its connection.
/// </summary>
public interface IPhoneSpeakerEndpoint : IAsyncDisposable
{
    /// <summary>The WASAPI endpoint id of the render device.</summary>
    string EndpointId { get; }

    /// <summary>The name Windows shows for this endpoint.</summary>
    string FriendlyName { get; }

    /// <summary>
    /// The endpoint's playback volume, 0..1, and a notification when the
    /// user changes it with the Windows volume control.
    /// </summary>
    float Volume { get; }

    /// <summary>Sets the endpoint's own volume, 0..1, as the user moved the
    /// phone's slider. The phone and the PC are then the same control.</summary>
    void SetVolume(float volume);

    event Action<float>? VolumeChanged;
}

/// <summary>
/// Provides the Windows playback endpoint of an enrolled phone. The
/// implementation owns the privileged part: creating a device is a
/// privileged operation, so it lives in a separate local broker.
/// </summary>
public interface IPhoneSpeakerProvider
{
    /// <summary>
    /// Provisions the endpoint of one enrolled phone, or returns null with
    /// a reason when it cannot be provided. The identity is the enrollment
    /// hash of the phone, never its network address. The host never
    /// substitutes another device.
    /// </summary>
    Task<(IPhoneSpeakerEndpoint? Endpoint, string? Error)> ProvisionAsync(
        string peerId, string label, CancellationToken ct);

    /// <summary>Removes the endpoint of one enrolled phone.</summary>
    Task ReleaseAsync(string peerId, CancellationToken ct);
}

/// <summary>Explicit window picker: list and focus only; never move/resize physical PC windows.</summary>
public interface IWindowWorkflow
{
    IReadOnlyList<WindowItem> ListVisible();
    bool Activate(string id);
}
/// <summary>
/// Destination for PCM16 mono, 16 kHz phone microphone data. An implementation
/// must route to a *real* Windows capture endpoint (or an explicitly selected
/// external virtual-cable adapter). This interface itself creates NO microphone.
/// </summary>
public interface IPhoneMicrophoneSink : IDisposable
{
    void Write(ReadOnlySpan<byte> pcm16Mono);
}

public sealed record HostServices(
    Func<IDisplayBackend> CreateBackend,
    Func<IInputSink> CreateInput,
    Func<IPhysicalDisplayGuard> CreateGuard,
    IApprovalPolicy Approval,
    IPairingStore PairingStore,
    Action<NegotiationOutcome>? OnNegotiated = null,
    IClipboardBridge? Clipboard = null,
    IWindowWorkflow? Windows = null,
    IDeviceCredentialStore? DeviceCredentials = null,
    Func<IVideoPipeline>? CreateVideoPipeline = null,
    Func<IReadOnlyList<string>, int, IHostAudioCapture>? CreateAudioCapture = null,
    IDisplaySessionAdmission? DisplayAdmission = null,
    Func<string, IPhoneMicrophoneSink?>? CreatePhoneMicrophoneSink = null,
    Func<string, bool>? CanUsePhoneMicrophone = null,
    IPhoneSpeakerProvider? PhoneSpeakers = null,
    Func<string, IReadOnlyList<string>, int, IHostAudioCapture?>? CreateEndpointAudioCapture = null,
    Func<NativeAudioLaunch, CancellationToken, Task<INativeAudioProcess>>? CreateNativeAudio = null);
