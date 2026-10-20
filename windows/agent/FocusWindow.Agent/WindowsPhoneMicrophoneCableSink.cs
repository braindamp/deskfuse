using FocusWindow.Core.Session;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FocusWindow.Agent;

/// <summary>
/// TEMPORARY opt-in development adapter. Sends a phone's PCM mic to a Windows
/// RENDER endpoint selected by its exact MMDevice ID. If the endpoint belongs
/// to an external virtual audio cable, its matching CAPTURE endpoint may be
/// selected by Windows applications for dictation. This is not a DeskFuse PnP
/// microphone and does not create/remove a device on reconnect.
/// </summary>
internal sealed class WindowsPhoneMicrophoneCableSink : IPhoneMicrophoneSink
{
    // A single selected render endpoint must never mix multiple phones'
    // microphone data. The production driver will allocate per-device sinks.
    private static int _busy;
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly BufferedWaveProvider _buffer;
    private bool _disposed;

    public WindowsPhoneMicrophoneCableSink(string renderDeviceId)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("The microphone cable is already in use by another phone");
        MMDevice? device = null;
        WasapiOut? player = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(renderDeviceId);
            if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected Windows render endpoint is not active");
            _buffer = new BufferedWaveProvider(new WaveFormat(16_000, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(150),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            player = new WasapiOut(device, AudioClientShareMode.Shared, false, 40);
            player.Init(_buffer);
            player.Play();
            _device = device;
            _player = player;
        }
        catch
        {
            player?.Dispose();
            device?.Dispose();
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16Mono)
    {
        if (_disposed) return;
        if (pcm16Mono.IsEmpty || pcm16Mono.Length > 640 || (pcm16Mono.Length & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(pcm16Mono));
        // Feed directly from authenticated bounded packet. BufferedWaveProvider
        // maintains a short buffer; frames that exceed it are discarded.
        var bytes = pcm16Mono.ToArray();
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _player.Stop(); }
        finally
        {
            _player.Dispose();
            _device.Dispose();
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
