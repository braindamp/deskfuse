using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskFuse.Audio;

/// <summary>A short, quiet stimulus whose frequency and stop are checked, not merely nonzero bytes.</summary>
internal static class LoopbackSignalProbe
{
    internal static async Task<int> RunAsync(MMDevice device, CancellationToken cancellationToken)
    {
        using var capture = new WasapiLoopbackCapture(device);
        var format = capture.WaveFormat;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            encoding = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.Pcm : WaveFormatEncoding.Unknown;
        }
        if (!((encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample is 32 or 64) ||
              (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32)))
            throw new NotSupportedException($"Cannot verify capture format {format}.");

        var samples = new[] { new List<float>(), new List<float>(), new List<float>() };
        var gate = new object();
        var phase = 0;
        Exception? captureError = null;
        capture.RecordingStopped += (_, e) => captureError = e.Exception;
        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                if (phase < 0) return;
                var destination = samples[phase];
                // One channel suffices: the identical stimulus is rendered into every channel.
                for (var offset = 0; offset + format.BlockAlign <= e.BytesRecorded &&
                     destination.Count < format.SampleRate * 2; offset += format.BlockAlign)
                    destination.Add(ReadSample(e.Buffer.AsSpan(offset), encoding, format.BitsPerSample));
            }
        };

        var rate = format.SampleRate;
        var channels = format.Channels;
        var pcm = new byte[rate * channels * sizeof(short) * 2];
        for (var frame = 0; frame < rate * 2; frame++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 1000 * frame / rate) * (short.MaxValue * 0.01));
            for (var channel = 0; channel < channels; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2), value);
        }
        using var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(rate, 16, channels));
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        output.Init(source);
        capture.StartRecording();
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            lock (gate) phase = -1;
            output.Play();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            lock (gate) phase = 1;
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            lock (gate) phase = -1;
            output.Stop();
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            lock (gate) phase = 2;
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) phase = -1;
            output.Stop();
            capture.StopRecording();
        }
        if (captureError is not null)
            throw new InvalidOperationException("Loopback capture failed.", captureError);

        Metrics before, active, after;
        lock (gate)
        {
            before = Analyze(samples[0], rate);
            active = Analyze(samples[1], rate);
            after = Analyze(samples[2], rate);
        }
        Console.WriteLine($"Capture format: {format}; decoded as {encoding} {format.BitsPerSample}-bit.");
        Console.WriteLine($"Baseline: {before.Count} frames, RMS {before.Rms:G4}; " +
            $"1 kHz burst: {active.Count} frames, RMS {active.Rms:G4}, peak {active.Peak:G4}, " +
            $"1 kHz energy {active.OneKhzFraction:P1}, 3 kHz energy {active.ThreeKhzFraction:P1}.");
        Console.WriteLine($"After Stop + 500 ms drain: {after.Count} frames, RMS {after.Rms:G4} " +
            "(no loopback packets after Stop also represents silence).");
        var quietLimit = Math.Max(0.00001, active.Rms * 0.03);
        if (active.Count < rate / 3 || !double.IsFinite(active.Rms) || active.Rms < 0.00001 ||
            active.OneKhzFraction < 0.75 || active.ThreeKhzFraction > 0.05 ||
            !double.IsFinite(before.Rms) || !double.IsFinite(after.Rms) ||
            before.Rms > quietLimit || after.Rms > quietLimit)
        {
            Console.Error.WriteLine("FAIL: loopback did not reproduce the 1 kHz application burst and return to silence. " +
                "Nonzero capture, including the SysVad constant 3 kHz sample tone, does not pass.");
            return 1;
        }
        Console.WriteLine("PASS: the explicit endpoint reproduced the application's 1 kHz burst and returned to silence.");
        return 0;
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, WaveFormatEncoding encoding, int bits) =>
        encoding == WaveFormatEncoding.IeeeFloat
            ? bits == 32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                : (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
            : bits switch
            {
                8 => (bytes[0] - 128) / 128f,
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                24 => (((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8) >> 8) / 8388608f,
                32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0),
                _ => throw new NotSupportedException()
            };

    private readonly record struct Metrics(int Count, double Rms, double Peak,
        double OneKhzFraction, double ThreeKhzFraction);

    private static Metrics Analyze(List<float> samples, int rate)
    {
        double squares = 0, peak = 0, oneKhzPower = 0, threeKhzPower = 0;
        foreach (var value in samples) { squares += value * (double)value; peak = Math.Max(peak, Math.Abs(value)); }
        // Separate 100 ms blocks tolerate clock drift without requiring a phase-aligned recording.
        var block = rate / 10;
        var analyzed = 0;
        for (var start = 0; start + block <= samples.Count; start += block)
        {
            double oneReal = 0, oneImag = 0, threeReal = 0, threeImag = 0;
            for (var i = 0; i < block; i++)
            {
                var angle = 2 * Math.PI * 1000 * i / rate;
                var value = samples[start + i];
                oneReal += value * Math.Cos(angle); oneImag += value * Math.Sin(angle);
                threeReal += value * Math.Cos(angle * 3); threeImag += value * Math.Sin(angle * 3);
            }
            oneKhzPower += 2 * (oneReal * oneReal + oneImag * oneImag) / block;
            threeKhzPower += 2 * (threeReal * threeReal + threeImag * threeImag) / block;
            analyzed += block;
        }
        var rms = samples.Count == 0 ? 0 : Math.Sqrt(squares / samples.Count);
        var analyzedPower = rms * rms * analyzed;
        return new(samples.Count, rms, peak, analyzedPower > 0 ? oneKhzPower / analyzedPower : 0,
            analyzedPower > 0 ? threeKhzPower / analyzedPower : 0);
    }
}
