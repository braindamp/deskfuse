using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

/// <summary>
/// Authorized accessory peer without a virtual monitor. In the future the
/// same device may contribute camera, microphone, speakers, touchpad, keyboard
/// or sensors concurrently while another device owns a display. No camera/audio
/// endpoints are claimed present until a real Windows provider is installed.
///
/// A phone that asks for the speakers capability gets a real Windows playback
/// endpoint of its own. The audio the user sends to that endpoint in Windows
/// is streamed to the phone over the encrypted transport, and the endpoint's
/// volume is mirrored on the phone.
/// </summary>
internal sealed class DevicePeerSession(
    HostOptions options,
    HostServices services,
    DevicePeerRegistry peers,
    ILog log)
{
    public async Task RunAsync(PhoneConnection conn, ClientHello hello, CancellationToken shutdown)
    {
        await using var _ = conn;
        conn.StartPumps();
        if (hello.ProtocolVersion != Wire.ProtocolVersion)
        {
            conn.SendControl(ServerMessages.Stage("version", "Phone and host protocol versions differ.",
                "Update DeskFuse on both devices.", fatal: true));
            return;
        }

        IReadOnlyList<string> requested;
        try
        {
            requested = DeviceCapabilities.Validate(hello.Capabilities, accessoryOnly: true);
        }
        catch (ArgumentException)
        {
            conn.SendControl(ServerMessages.Stage("device_bridge",
                "Unsupported accessory capability requested.", fatal: true));
            return;
        }
        conn.SendControl(ServerMessages.Hello(options.HostVersion, options.HostName, options.Channel,
            phoneMicrophoneBridgeAvailable: options.AllowPhoneMicrophone &&
                services.CreatePhoneMicrophoneSink is not null &&
                services.CanUsePhoneMicrophone?.Invoke(hello.Profile.DeviceId) == true &&
                requested.Contains(DeviceCapabilities.Microphone)));
        // Approval remains OS-user-scoped, in addition to per-phone encrypted
        // credentials validated by HostListener. This role cannot grant itself
        // filesystem, input, desktop, monitor or Windows capture permissions.
        var approved = await services.Approval.ApproveAsync(
            hello.Profile.DeviceId, hello.Profile.DeviceLabel,
            conn.VerificationCode, shutdown).ConfigureAwait(false);
        if (!approved)
        {
            conn.SendControl(ServerMessages.Stage("approval", "Accessory connection declined.",
                fatal: true));
            return;
        }

        if (requested.Contains(DeviceCapabilities.Speakers) && services.CreateNativeAudio is not null &&
            (hello.NativeAudioVersion != 2 || conn.AuthenticatedDeviceId != hello.Profile.DeviceId)) {
            conn.SendControl(ServerMessages.Stage("speakers", "Native audio requires a matching receiver and per-device enrollment.", fatal:true));
            return;
        }

        using var lease = await peers.AttachAsync(
            hello.Profile.DeviceId, hello.Profile.DeviceLabel, shutdown, requested).ConfigureAwait(false);
        log.Info("Authenticated accessory peer joined: " + lease.Peer.DeviceId);

        // The phone's Windows playback endpoint. It is provisioned only when
        // the phone asked for the speakers capability, and it stays active
        // for as long as this peer is connected.
        await using var speakers = new PhoneSpeakerRoute(conn, services, lease.Peer.Endpoints.Speakers,
            lease.Peer.Label, log);
        var active = await speakers.ProvisionAsync(requested, lease.Cancellation).ConfigureAwait(false);

        // A requested function is not the same thing as an active device.
        conn.SendControl(ServerMessages.BridgeState(
            lease.Peer.RequestedCapabilities, DeviceCapabilities.ActiveForAccessory(active)));
        conn.SendControl(ServerMessages.Stage("device_bridge",
            active.Contains(DeviceCapabilities.Speakers)
                ? "The phone speaker endpoint is ready. Select it as the Windows output to send sound to this phone."
                : "Accessory connection established. Requested features are not yet " +
                  "active Windows devices or remote input controls in this build."));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown, lease.Cancellation);
        using var mic = new PhoneMicrophoneRoute(
            conn, options, services, lease.Peer.DeviceId, log);
        var microphonePump = mic.PumpAsync(linked.Token);
        var keepAlive = KeepAliveAsync(conn, linked.Token);
        var control = ReadControlsAsync(conn, mic, speakers, requested, linked.Token);
        try
        {
            await Task.WhenAny(control, microphonePump, keepAlive, conn.Completion, speakers.NativeCompletion).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            await conn.DisposeAsync().ConfigureAwait(false);
            try { await Task.WhenAll(control, microphonePump, keepAlive).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            log.Info("Accessory peer detached: " + lease.Peer.DeviceId);
        }
    }

    private static async Task KeepAliveAsync(PhoneConnection conn, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) conn.SendPing();
    }

    private static async Task ReadControlsAsync(PhoneConnection conn,
        PhoneMicrophoneRoute microphone, PhoneSpeakerRoute speakers,
        IReadOnlyList<string> requested, CancellationToken ct)
    {
        await foreach (var msg in conn.Messages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (msg is ClientBye) return;
            if (msg is ClientNativeAudioVolume applied) {
                await speakers.ApplyNativeVolumeAsync(applied).ConfigureAwait(false);continue;
            }
            if (msg is ClientNativeAudio native)
            {
                await speakers.AuthorizeNativeAsync(native).ConfigureAwait(false);
                continue;
            }
            if (msg is ClientMicrophone mic &&
                requested.Contains(DeviceCapabilities.Microphone))
            {
                microphone.Handle(mic);
                continue;
            }
            if (msg is ClientAudioVolume volume)
            {
                speakers.HandleVolume(volume.Level);
                continue;
            }
            if (msg is ClientAudio audio &&
                audio.Action is "start_phone_speakers" or "stop_phone_speakers")
            {
                await speakers.HandleAsync(audio).ConfigureAwait(false);
                continue;
            }
            // An accessory peer MUST NOT dispatch input/file/window/monitor
            // actions through PhoneSession. Hardware commands will require
            // separate capability checks when real endpoints are introduced.
            if (msg is not ClientUnknown)
            {
                conn.SendControl(ServerMessages.Stage("device_bridge",
                    "That operation is not supported by a media-only connection."));
            }
        }
    }
}
