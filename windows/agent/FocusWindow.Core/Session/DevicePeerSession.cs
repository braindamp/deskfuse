using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

/// <summary>
/// Authorized accessory peer without a virtual monitor. In the future the
/// same device may contribute camera, microphone, speakers, touchpad, keyboard
/// or sensors concurrently while another device owns a display. No camera/audio
/// endpoints are claimed present until a real Windows provider is installed.
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

        using var lease = peers.Attach(
            hello.Profile.DeviceId, hello.Profile.DeviceLabel, shutdown, requested);
        log.Info("Authenticated accessory peer joined: " + lease.Peer.DeviceId);
        // Distinguish a requested function from an actual, provisioned Windows
        // endpoint. No accessory provider has been installed in this build.
        conn.SendControl(ServerMessages.BridgeState(
            lease.Peer.RequestedCapabilities, DeviceCapabilities.ActiveForAccessory));
        conn.SendControl(ServerMessages.Stage("device_bridge",
            "Accessory connection established. Requested features are not yet " +
            "active Windows devices or remote input controls in this build."));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown, lease.Cancellation);
        using var mic = new PhoneMicrophoneRoute(
            conn, options, services, lease.Peer.DeviceId, log);
        var microphonePump = mic.PumpAsync(linked.Token);
        var control = ReadControlsAsync(conn, mic, requested, linked.Token);
        try
        {
            await Task.WhenAny(control, conn.Completion).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            await conn.DisposeAsync().ConfigureAwait(false);
            try { await Task.WhenAll(control, microphonePump).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            log.Info("Accessory peer detached: " + lease.Peer.DeviceId);
        }
    }

    private static async Task ReadControlsAsync(PhoneConnection conn,
        PhoneMicrophoneRoute microphone, IReadOnlyList<string> requested, CancellationToken ct)
    {
        await foreach (var msg in conn.Messages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (msg is ClientBye) return;
            if (msg is ClientMicrophone mic &&
                requested.Contains(DeviceCapabilities.Microphone))
            {
                microphone.Handle(mic);
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
