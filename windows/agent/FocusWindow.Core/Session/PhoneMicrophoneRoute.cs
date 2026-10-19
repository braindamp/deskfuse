using System.Security.Cryptography;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

/// <summary>
/// Authenticated, explicitly enabled phone microphone route for one enrolled
/// Android device. This code deliberately does NOT create a Windows audio PnP
/// endpoint: its sink must be a real provider or an explicitly configured
/// external virtual-cable adapter. No sink => no microphone capture request.
/// </summary>
internal sealed class PhoneMicrophoneRoute(
    PhoneConnection connection, HostOptions options, HostServices services,
    string enrolledDeviceId, ILog log) : IDisposable
{
    private readonly object _gate = new();
    private IPhoneMicrophoneSink? _sink;
    private int _streamId;
    private long _lastAudioTicks;
    private int _disposed;

    public void Handle(ClientMicrophone request)
    {
        if (request.Action == "stop")
        {
            Stop();
            connection.SendControl(ServerMessages.MicrophoneState("stopped"));
            return;
        }
        if (request.Action != "start" || !options.AllowPhoneMicrophone ||
            !options.CameraFreePairing || services.DeviceCredentials is null ||
            services.CreatePhoneMicrophoneSink is null ||
            services.CanUsePhoneMicrophone?.Invoke(enrolledDeviceId) != true)
        {
            connection.SendControl(ServerMessages.MicrophoneState("unavailable",
                detail: "Phone microphone requires an enrolled device and an authorized Windows microphone destination."));
            return;
        }
        Stop();
        try
        {
            var next = services.CreatePhoneMicrophoneSink(enrolledDeviceId);
            if (next is null)
            {
                connection.SendControl(ServerMessages.MicrophoneState("unavailable",
                    detail: "No Windows microphone destination is configured for this phone."));
                return;
            }
            int id;
            lock (_gate)
            {
                if (_disposed != 0) { next.Dispose(); return; }
                _sink = next;
                id = _streamId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                _lastAudioTicks = 0;
            }
            // Android starts AudioRecord ONLY after acknowledging this control
            // message and checking its current Android permission.
            connection.SendControl(ServerMessages.MicrophoneState("started", id));
        }
        catch (Exception ex)
        {
            log.Warn("phone microphone destination unavailable: " + ex.GetType().Name);
            Stop();
            connection.SendControl(ServerMessages.MicrophoneState("unavailable",
                detail: "Windows microphone destination could not be opened."));
        }
    }

    public async Task PumpAsync(CancellationToken ct)
    {
        await foreach (var packet in connection.MicrophonePackets.ReadAllAsync(ct).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_disposed != 0 || _sink is null || packet.StreamId != _streamId)
                    continue;
                // Limit clients to 100 short PCM packets/s per enrolled device.
                // This permits network jitter/bursts but bounds abuse.
                // Fixed 16 kHz/mono and <=20ms is validated by PhoneConnection.
                var now = Environment.TickCount64;
                if (now < _lastAudioTicks) _lastAudioTicks = now;
                if (now - _lastAudioTicks < 6) continue;
                _lastAudioTicks = now;
                try { _sink.Write(packet.Pcm16Mono); }
                catch (Exception ex)
                {
                    log.Warn("phone microphone route failed: " + ex.GetType().Name);
                    StopLocked();
                    connection.SendControl(ServerMessages.MicrophoneState("unavailable",
                        detail: "Windows microphone destination was disconnected."));
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        var current = _sink;
        _sink = null;
        _streamId = 0;
        if (current is null) return;
        try { current.Dispose(); }
        catch (Exception ex) { log.Warn("closing phone microphone: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            StopLocked();
        }
    }
}
