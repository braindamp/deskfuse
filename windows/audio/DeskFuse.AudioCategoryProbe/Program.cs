using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// This EXPLICIT opt-in lab probe is not linked into DeskFuse's host or Android
// player. It emits a quiet, known test waveform into a *chosen* Windows render
// endpoint using an IAudioClient2 category. It never changes default devices,
// endpoint volume, audio services, driver configuration, or any phone settings.
if (args.Length != 1 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: DeskFuse.AudioCategoryProbe <unique endpoint-name substring>");
    Console.WriteLine("Example: DeskFuse.AudioCategoryProbe \"DeskFuse Xiaomi\"");
    return args.Length == 1 ? 0 : 1;
}

using var devices = new MMDeviceEnumerator();
var candidates = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
    .Where(device => device.FriendlyName.Contains(args[0], StringComparison.OrdinalIgnoreCase))
    .ToArray();
if (candidates.Length != 1)
{
    Console.Error.WriteLine($"Need exactly one active endpoint matching '{args[0]}', found {candidates.Length}.");
    foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        Console.Error.WriteLine($"  {device.FriendlyName} [{device.ID}]");
    return 1;
}

var selected = candidates[0];
Console.WriteLine($"Selected: {selected.FriendlyName} [{selected.ID}]");
Console.WriteLine("The probe will play seven short, QUIET 900 Hz lab tones into this endpoint.");
Console.WriteLine("These are NOT Windows notification dings and are never used by DeskFuse itself.");
Console.WriteLine("Keep headphone/speaker volume comfortable. No device or sound setting will change.");
Console.Write("Type YES and press Enter to run: ");
if (Console.ReadLine() != "YES")
{
    Console.WriteLine("Cancelled; no sound emitted.");
    return 0;
}

using var capture = new WasapiLoopbackCapture(selected);
using var meter = new LoopbackMeter(capture.WaveFormat);
capture.DataAvailable += meter.OnAudio;
capture.StartRecording();
Thread.Sleep(500); // Capture running before first stimulus, not a measured stream-start delay.
try
{
    Console.WriteLine($"Loopback capture format: {capture.WaveFormat}");
    Console.WriteLine("Category | Init/start | Loopback callbacks (signal/nonzero/total) | max raw PCM peak");
    var tests = new (string Name, int Category, int Options)[]
    {
        ("Other", 0, 0),
        ("SoundEffects", 5, 0),
        ("Alerts", 4, 0),
        ("Media", 11, 0),
        ("Movie", 10, 0),
        ("GameEffects", 6, 0),
        ("RawOther", 0, 1)
    };
    foreach (var test in tests)
    {
        Thread.Sleep(500);
        meter.Reset();
        try
        {
            using var client = new CategoryRenderClient(selected.ID, test.Category, test.Options);
            var latency = client.Play(durationMs: 750);
            Thread.Sleep(400); // Include tail that reaches the WASAPI loopback.
            var result = meter.Snapshot();
            Console.WriteLine($"{test.Name,-14} OK {latency,5:F1} ms | " +
                $"{result.Signal}/{result.Nonzero}/{result.Total} | peak={result.Peak:F8}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{test.Name,-14} FAILED: {ex.Message}");
        }
    }
}
finally
{
    capture.StopRecording();
}

return 0;

internal sealed class LoopbackMeter : IDisposable
{
    private const float Threshold = 80f / 32768f;
    private readonly object gate = new();
    private readonly WaveFormat format;
    private long total;
    private long nonzero;
    private long signal;
    private float peak;

    internal LoopbackMeter(WaveFormat format) => this.format = format;

    internal void Reset()
    {
        lock (gate) { total = 0; nonzero = 0; signal = 0; peak = 0; }
    }

    internal (long Signal, long Nonzero, long Total, float Peak) Snapshot()
    {
        lock (gate) return (signal, nonzero, total, peak);
    }

    internal void OnAudio(object? _, WaveInEventArgs data)
    {
        var encoding = format is WaveFormatExtensible ext
            ? ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm
            : format.Encoding;
        var sampleBytes = format.BitsPerSample / 8;
        if (sampleBytes <= 0) return;
        var observed = 0f;
        for (var i = 0; i + sampleBytes <= data.BytesRecorded; i += sampleBytes)
        {
            float value;
            if (encoding == WaveFormatEncoding.IeeeFloat && sampleBytes == 4)
                value = Math.Abs(BitConverter.ToSingle(data.Buffer, i));
            else if (encoding == WaveFormatEncoding.Pcm && sampleBytes == 2)
                value = Math.Abs(BitConverter.ToInt16(data.Buffer, i) / 32768f);
            else continue;
            if (float.IsFinite(value)) observed = Math.Max(observed, value);
        }
        lock (gate)
        {
            total++;
            if (observed > 0f) nonzero++;
            if (observed >= Threshold) signal++;
            peak = Math.Max(peak, observed);
        }
    }

    public void Dispose() { }
}

internal sealed class CategoryRenderClient : IDisposable
{
    private static readonly Guid CLSID_Enumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_Enumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_Client2 = new("726778CD-F60A-4EDA-82DE-E47610CD78AA");
    private static readonly Guid IID_Render = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private IMMDeviceEnumerator? enumerator;
    private IMMDevice? endpoint;
    private IAudioClient2? audio;
    private IAudioRenderClient? renderer;
    private IntPtr mixFormat;
    private readonly int category;
    private readonly int options;

    internal CategoryRenderClient(string deviceId, int category, int options)
    {
        this.category = category;
        this.options = options;
        var clsid = CLSID_Enumerator;
        var iid = IID_Enumerator;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var en), "CoCreateInstance(MMDeviceEnumerator)");
        enumerator = en;
        Check(en.GetDevice(deviceId, out var device), "GetDevice");
        endpoint = device;
        iid = IID_Client2;
        Check(device.Activate(ref iid, 23, IntPtr.Zero, out var client), "Activate(IAudioClient2)");
        audio = client;
    }

    internal double Play(int durationMs)
    {
        if (audio is null) throw new InvalidOperationException("No audio client");
        var properties = new AudioClientProperties
        {
            Size = (uint)Marshal.SizeOf<AudioClientProperties>(),
            Offload = 0,
            Category = category,
            Options = options
        };
        Check(audio.SetClientProperties(ref properties), $"SetClientProperties(category={category}, options={options})");
        Check(audio.GetMixFormat(out mixFormat), "GetMixFormat");
        var tag = (ushort)Marshal.ReadInt16(mixFormat, 0);
        var channels = (ushort)Marshal.ReadInt16(mixFormat, 2);
        var sampleRate = Marshal.ReadInt32(mixFormat, 4);
        var blockAlign = (ushort)Marshal.ReadInt16(mixFormat, 12);
        var bits = (ushort)Marshal.ReadInt16(mixFormat, 14);
        if (channels is < 1 or > 8 || sampleRate is < 8000 or > 192000 || blockAlign != channels * bits / 8)
            throw new NotSupportedException($"Unexpected mix format channels={channels} rate={sampleRate} bits={bits} align={blockAlign}");

        var isFloat = tag == 3 ||
            (tag == 0xfffe && ReadSubFormat() == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        var isPcm = tag == 1 ||
            (tag == 0xfffe && ReadSubFormat() == new Guid("00000001-0000-0010-8000-00aa00389b71"));
        if (!(isFloat && bits == 32) && !(isPcm && bits == 16))
            throw new NotSupportedException($"Mix format unsupported for lab stimulus: tag=0x{tag:X4} bits={bits}");

        Check(audio.Initialize(0, 0, 2_000_000L, 0, mixFormat, IntPtr.Zero), "IAudioClient.Initialize");
        Check(audio.GetBufferSize(out var capacity), "GetBufferSize");
        Check(audio.GetStreamLatency(out var latencyHns), "GetStreamLatency");
        var iid = IID_Render;
        Check(audio.GetService(ref iid, out var render), "GetService(IAudioRenderClient)");
        renderer = render;

        // Fill the first available frames before Start, then pace subsequent
        // writes by GetCurrentPadding. This is an ordinary shared-mode stream;
        // no notification event or raw endpoint changes are requested.
        var totalFrames = (uint)((long)sampleRate * durationMs / 1000);
        var remaining = totalFrames;
        long frame = 0;
        Write(Math.Min(capacity, remaining));
        Check(audio.Start(), "Start");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (remaining > 0 && DateTime.UtcNow < until)
        {
            Check(audio.GetCurrentPadding(out var padding), "GetCurrentPadding");
            var available = capacity - padding;
            if (available != 0) Write(Math.Min(available, remaining));
            else Thread.Sleep(5);
        }
        if (remaining != 0) throw new TimeoutException($"Shared-mode writer stalled with {remaining} frames unsent");
        Thread.Sleep(200); // Drain; never write into the app's main PCM stream.
        Check(audio.Stop(), "Stop");
        return latencyHns / 10_000.0;

        Guid ReadSubFormat()
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(mixFormat, 24), bytes, 0, 16);
            return new Guid(bytes);
        }

        void Write(uint count)
        {
            if (count == 0) return;
            if (renderer is null) throw new InvalidOperationException("Render client unavailable");
            Check(renderer.GetBuffer(count, out var buffer), "IAudioRenderClient.GetBuffer");
            var data = new byte[(int)count * blockAlign];
            for (uint i = 0; i < count; i++, frame++)
            {
                var sample = 0.08 * Math.Sin(2 * Math.PI * 900 * frame / sampleRate);
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = (int)i * blockAlign + channel * (bits / 8);
                    if (isFloat)
                    {
                        var encoded = BitConverter.GetBytes((float)sample);
                        Buffer.BlockCopy(encoded, 0, data, offset, 4);
                    }
                    else
                    {
                        var encoded = BitConverter.GetBytes((short)Math.Round(sample * 32767));
                        Buffer.BlockCopy(encoded, 0, data, offset, 2);
                    }
                }
            }
            Marshal.Copy(data, 0, buffer, data.Length);
            Check(renderer.ReleaseBuffer(count, 0), "IAudioRenderClient.ReleaseBuffer");
            remaining -= count;
        }
    }

    public void Dispose()
    {
        if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
        if (renderer is not null) Marshal.ReleaseComObject(renderer);
        if (audio is not null) Marshal.ReleaseComObject(audio);
        if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
        if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
    }

    private static void Check(int hr, string op)
    {
        if (hr < 0) throw new COMException($"{op} returned HRESULT 0x{hr:X8}", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties
    {
        public uint Size;
        public int Offload;
        public int Category;
        public int Options;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator enumerator);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsctx, IntPtr activationParams,
            out IAudioClient2 client);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient2
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDurationHns,
            long periodicityHns, IntPtr waveFormat, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latencyHns);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultHns, out long minHns);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IAudioRenderClient render);
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minimum, out long maximum);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
