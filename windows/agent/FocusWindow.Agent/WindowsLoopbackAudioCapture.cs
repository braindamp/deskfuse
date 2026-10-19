using FocusWindow.Core.Session;
using NAudio.Wave;

namespace FocusWindow.Agent;

/// <summary>
/// Captures the current default playback endpoint, not a particular application's
/// audio. No PC speaker mute or default-device change. User explicitly starts it.
/// Fails if the endpoint cannot provide the requested 48kHz/stereo/PCM16 format.
/// </summary>
internal sealed class WindowsLoopbackAudioCapture : IHostAudioCapture
{
    private readonly WasapiLoopbackCapture _capture;
    private bool _started;
    private bool _disposed;

    public int SampleRate => 48_000;
    public int Channels => 2;

    public WindowsLoopbackAudioCapture()
    {
        _capture = new WasapiLoopbackCapture
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
        };
    }

    public void Start(Action<ReadOnlyMemory<byte>> onSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("Audio capture already started");
        _capture.DataAvailable += (_, e) =>
        {
            // WASAPI owns e.Buffer only during this callback. The connection
            // clones each accepted segment before it enters the write queue.
            var length = e.BytesRecorded - e.BytesRecorded % (Channels * 2);
            const int maxChunk = 3840; // 20ms at 48kHz stereo PCM16
            for (var position = 0; position < length; position += maxChunk)
            {
                var take = Math.Min(maxChunk, length - position);
                onSamples(e.Buffer.AsMemory(position, take));
            }
        };
        _capture.StartRecording();
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_started) _capture.StopRecording(); }
        finally { _capture.Dispose(); }
    }
}
