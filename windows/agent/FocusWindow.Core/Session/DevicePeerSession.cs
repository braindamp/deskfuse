using System.Threading.Channels;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

/// <summary>
/// Authorized peer without a virtual monitor. Allows a phone to provide its own
/// media endpoints while a different tablet owns the display. No camera/audio
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

        conn.SendControl(ServerMessages.Hello(options.HostVersion, options.HostName, options.Channel));
        // Approval remains OS-user-scoped, in addition to per-phone encrypted
        // credentials validated by HostListener. This role cannot grant itself
        // filesystem, input, desktop, monitor or Windows capture permissions.
        var approved = await services.Approval.ApproveAsync(
            hello.Profile.DeviceId, hello.Profile.DeviceLabel,
            conn.VerificationCode, shutdown).ConfigureAwait(false);
        if (!approved)
        {
            conn.SendControl(ServerMessages.Stage("approval", "Media-only device connection declined.",
                fatal: true));
            return;
        }

        using var lease = peers.Attach(
            hello.Profile.DeviceId, hello.Profile.DeviceLabel, shutdown);
        log.Info("Authenticated media-only peer joined: " + lease.Peer.DeviceId);
        conn.SendControl(ServerMessages.Stage("device_bridge",
            "Media-only connection ready; phone camera/microphone and speakers are not yet " +
            "available as Windows devices in this build."));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown, lease.Cancellation);
        var control = ReadControlsAsync(conn, linked.Token);
        try
        {
            await Task.WhenAny(control, conn.Completion).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            await conn.DisposeAsync().ConfigureAwait(false);
            try { await control.ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            log.Info("Media-only peer detached: " + lease.Peer.DeviceId);
        }
    }

    private static async Task ReadControlsAsync(PhoneConnection conn, CancellationToken ct)
    {
        await foreach (var msg in conn.Messages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (msg is ClientBye) return;
            // A media-only peer MUST NOT dispatch an input/file/window/monitor
            // action through PhoneSession. Hardware media commands will require
            // separate capability checks when real endpoints are introduced.
            if (msg is not ClientUnknown)
            {
                conn.SendControl(ServerMessages.Stage("device_bridge",
                    "That operation is not supported by a media-only connection."));
            }
        }
    }
}
