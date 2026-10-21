using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

/// <summary>Real session/crypto/TCP; simulated GPU, audio and display only. No decode/hardware claim.</summary>
public sealed class UnifiedSessionTests
{
    private sealed class SpeakerEndpoint : IPhoneSpeakerEndpoint
    {
        public string EndpointId => "phone-only-render-endpoint";
        public string FriendlyName => "Test phone speaker";
        public float Volume => 0.2f;
        public void SetVolume(float volume) { }
        public event Action<float>? VolumeChanged { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Speakers : IPhoneSpeakerProvider
    {
        public Task<(IPhoneSpeakerEndpoint? Endpoint, string? Error)> ProvisionAsync(
            string peerId, string label, CancellationToken ct) =>
            Task.FromResult<(IPhoneSpeakerEndpoint?, string?)>((new SpeakerEndpoint(), null));
        public Task ReleaseAsync(string peerId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class SpeakerCapture(byte marker) : IHostAudioCapture
    {
        public int SampleRate => 48000;
        public int Channels => 2;
        public bool Disposed;
        public void Start(Action<ReadOnlyMemory<byte>> onSamples) =>
            onSamples(Enumerable.Repeat(marker, 1920).ToArray());
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speaker_audio_reaches_the_wire_on_first_start_and_restart(bool stopBetween)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var credentials = new MemoryDeviceCredentialStore();
        var secret = RandomNumberGenerator.GetBytes(32);
        credentials.Save(new DeviceCredential("speaker-enrolled", secret));
        var captures = new List<SpeakerCapture>();
        var services = new HostServices(
            () => throw new InvalidOperationException("An accessory must not acquire a display"),
            () => new RecordingInputSink(), () => new StaticGuard(),
            new AutoApprove(), new MemoryPairingStore(), DeviceCredentials: credentials,
            PhoneSpeakers: new Speakers(), CreateEndpointAudioCapture: (endpointId, _, _) =>
            {
                Assert.Equal("phone-only-render-endpoint", endpointId);
                var capture = new SpeakerCapture((byte)(captures.Count + 1));
                captures.Add(capture);
                return capture;
            });
        SessionHost? host = null;
        for (var retry = 0; retry < 40; retry++)
        {
            host = new SessionHost(new HostOptions { Bind = "127.0.0.1",
                Port = RandomNumberGenerator.GetInt32(12000, 45000), CameraFreePairing = true },
                services, NullLog.Instance);
            try { await host.StartAsync(deadline.Token); break; }
            catch (System.Net.Sockets.SocketException) { host.Dispose(); host = null; }
        }
        using var hostScope = host ?? throw new InvalidOperationException("No test port trio");
        var running = host.RunSessionsAsync(deadline.Token);
        try
        {
            await using var phone = await ConnectedPeer.ConnectAsync(host.Offer!, secret, deadline.Token);
            var hello = Hello("speaker-enrolled", accessory: true);
            hello["capabilities"] = new JsonArray("speakers");
            await phone.ControlAsync(hello, deadline.Token);
            Assert.Contains("speakers", (await phone.UntilAsync("bridge_state", deadline.Token))
                ["activeCapabilities"]!.AsArray().Select(value => value!.GetValue<string>()));
            for (var start = 1; start <= 3; start++)
            {
                await phone.ControlAsync(new JsonObject { ["type"] = "audio",
                    ["action"] = "start_phone_speakers" }, deadline.Token);
                var state = await phone.UntilAsync("audio_state", deadline.Token);
                Assert.Equal("started", state["state"]!.GetValue<string>());
                var streamId = state["streamId"]!.GetValue<int>();
                Assert.Equal(start, streamId);
                while (true)
                {
                    var record = await phone.ReadAsync(deadline.Token);
                    if (record.Kind != Wire.KindAudio) continue;
                    Assert.Equal(streamId, BinaryPrimitives.ReadInt32BigEndian(record.Plain));
                    Assert.Equal(1924, record.Plain.Length);
                    Assert.All(record.Plain.Skip(4), value => Assert.Equal((byte)start, value));
                    break;
                }
                if (stopBetween)
                {
                    await phone.ControlAsync(new JsonObject { ["type"] = "audio",
                        ["action"] = "stop_phone_speakers" }, deadline.Token);
                    Assert.Equal("stopped", (await phone.UntilAsync("audio_state", deadline.Token))
                        ["state"]!.GetValue<string>());
                    Assert.True(captures[^1].Disposed);
                }
                if (start > 1) Assert.True(captures[start - 2].Disposed);
            }
            await phone.ControlAsync(new JsonObject { ["type"] = "bye" }, deadline.Token);
        }
        finally { deadline.Cancel(); await running; }
        Assert.All(captures, capture => Assert.True(capture.Disposed));
    }

    private sealed class Video : IVideoPipeline
    {
        public VideoPipelineInfo Info { get; private set; } = null!;
        public bool Disposed;
        public int IdrRequests;
        public bool TryPrepareInput(InputEventDto input) => true;
        public Task StartAsync(VideoPipelineSettings settings, CancellationToken ct)
        {
            Info = new VideoPipelineInfo(new RectI(settings.BackingMonitorBounds.X +
                (settings.BackingMonitorBounds.Width - settings.Width) / 2,
                settings.BackingMonitorBounds.Y, settings.Width, settings.Height),
                settings.RequiredDpi, settings.Width, settings.Height, "h264", "YUV420",
                "simulated GPU", "BT.709 full range", true, "Test app", "test-source", settings.Fps);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            // Deliberately start with an inter-frame to exercise initial resync.
            yield return new GpuVideoPacket([0, 0, 1, 0x41], false, (ulong)Stopwatch.GetTimestamp());
            yield return new GpuVideoPacket([0, 0, 1, 0x65], true, (ulong)Stopwatch.GetTimestamp());
            await Task.Delay(Timeout.Infinite, ct);
        }
        public void RequestKeyframe() => Interlocked.Increment(ref IdrRequests);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Clipboard : IClipboardBridge
    {
        public string Text = "";
        public Task<string?> ReadTextAsync(CancellationToken ct) => Task.FromResult<string?>(Text);
        public Task WriteTextAsync(string text, CancellationToken ct) { Text = text; return Task.CompletedTask; }
    }
    private sealed class Windows : IWindowWorkflow
    {
        public string? Activated;
        public IReadOnlyList<WindowItem> ListVisible() => [new("window-test", "Test document", "Test app")];
        public bool Activate(string id) { Activated = id; return true; }
    }
    private sealed class Audio : IHostAudioCapture
    {
        public int SampleRate => 48000;
        public int Channels => 6;
        public string Layout => "5.1_back";
        public bool Disposed;
        public void Start(Action<ReadOnlyMemory<byte>> onSamples) => onSamples(new byte[120]);
        public void Dispose() => Disposed = true;
    }
    private sealed class Microphone : IPhoneMicrophoneSink
    {
        public readonly Channel<byte[]> Samples = Channel.CreateUnbounded<byte[]>();
        public bool Disposed;
        public void Write(ReadOnlySpan<byte> samples) => Samples.Writer.TryWrite(samples.ToArray());
        public void Dispose() { Disposed = true; Samples.Writer.TryComplete(); }
    }
    private sealed class SignaledAdmission : IDisplaySessionAdmission
    {
        private readonly ExclusiveVirtualDisplayAdmission _inner = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? TryAcquire(string id)
        {
            var lease = _inner.TryAcquire(id);
            return lease is null ? null : new Release(lease, Released);
        }
        private sealed class Release(IDisposable inner, TaskCompletionSource released) : IDisposable
        {
            public void Dispose() { inner.Dispose(); released.TrySetResult(); }
        }
    }
    private static JsonObject Hello(string id, bool accessory = false) => new()
    {
        ["type"] = accessory ? "bridge_hello" : "hello", ["protocolVersion"] = 3, ["clientVersion"] = "test",
        ["profile"] = JsonSerializer.SerializeToNode(TestProfiles.Flagship() with { DeviceId = id },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        ["capabilities"] = accessory ? new JsonArray("microphone") : new JsonArray("display", "speakers"),
    };

    [Fact]
    public async Task Requested_cadence_reconnects_with_a_reconfigured_pipeline_for_the_same_enrollment()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var videos = new List<Video>();
        var admission = new SignaledAdmission();
        var services = new HostServices(() => new FakeDisplayBackend(2, 2) { FailOnFrameSource = true },
            () => new RecordingInputSink(), () => new StaticGuard(), new AutoApprove(), new MemoryPairingStore(),
            CreateVideoPipeline: () => { var video = new Video(); videos.Add(video); return video; },
            DisplayAdmission: admission);
        using var host = new SessionHost(new HostOptions { Bind = "127.0.0.1", Port = 0 }, services, NullLog.Instance);
        var offer = await host.StartAsync(deadline.Token);
        var running = host.RunSessionsAsync(deadline.Token);
        try
        {
            await using (var phone = await ConnectedPeer.ConnectAsync(offer, offer.Secret, deadline.Token))
            {
                await phone.ControlAsync(Hello("cadence-test"), deadline.Token);
                Assert.Equal(60, (await phone.UntilAsync("target", deadline.Token))["record"]!["refreshHz"]!.GetValue<int>());
                await phone.ControlAsync(new JsonObject { ["type"] = "refresh", ["hz"] = 30 }, deadline.Token);
                while (true)
                {
                    try { await phone.ReadAsync(deadline.Token); }
                    catch (EndOfStreamException) { break; }
                }
            }
            await admission.Released.Task.WaitAsync(deadline.Token);
            await using var reconnected = await ConnectedPeer.ConnectAsync(offer, offer.Secret, deadline.Token);
            await reconnected.ControlAsync(Hello("cadence-test"), deadline.Token);
            Assert.Equal(30, (await reconnected.UntilAsync("target", deadline.Token))["record"]!["refreshHz"]!.GetValue<int>());
            Assert.Equal(2, videos.Count);
            Assert.True(videos[0].Disposed);
            Assert.Equal(30, videos[1].Info.FramesPerSecond);
        }
        finally { deadline.Cancel(); await running; }
    }

    [Fact]
    public async Task Gpu_session_retains_audio_clipboard_window_input_and_cleanup_without_cpu_capture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var backend = new FakeDisplayBackend(2, 2) { FailOnFrameSource = true };
        var input = new RecordingInputSink();
        var video = new Video();
        var clipboard = new Clipboard();
        var windows = new Windows();
        var audio = new Audio();
        var services = new HostServices(() => backend, () => input, () => new StaticGuard(),
            new AutoApprove(), new MemoryPairingStore(), Clipboard: clipboard, Windows: windows,
            CreateVideoPipeline: () => video, CreateAudioCapture: (_, _) => audio,
            CreatePhoneMicrophoneSink: _ => throw new InvalidOperationException("legacy QR must not open a mic sink"));
        using var host = new SessionHost(new HostOptions { Bind = "127.0.0.1", Port = 0,
            SingleSession = true, AllowPcAudio = true, AllowPhoneMicrophone = true }, services, NullLog.Instance);
        var offer = await host.StartAsync(deadline.Token);
        var running = host.RunSessionsAsync(deadline.Token);
        try
        {
            await using var phone = await ConnectedPeer.ConnectAsync(offer, offer.Secret, deadline.Token);
            await phone.ControlAsync(Hello("display-test"), deadline.Token);
            var target = await phone.UntilAsync("target", deadline.Token);
            Assert.Equal("Test app", target["record"]!["appLabel"]!.GetValue<string>());
            Assert.Equal("BT.709 full range", target["record"]!["colorRange"]!.GetValue<string>());
            Assert.Equal("test-source", target["record"]!["sourceId"]!.GetValue<string>());
            var frame = await phone.ReadAsync(deadline.Token);
            Assert.Equal(Wire.KindFrame, frame.Kind);
            Assert.Equal(Wire.FlagKeyframe, BinaryPrimitives.ReadUInt16BigEndian(frame.Plain.AsSpan(2)));
            Assert.Equal(1, video.IdrRequests);
            await phone.ControlAsync(new JsonObject { ["type"] = "input", ["event"] = new JsonObject
                { ["type"] = "pointer", ["action"] = "down", ["x"] = 50, ["y"] = 80 } }, deadline.Token);
            await phone.ControlAsync(new JsonObject { ["type"] = "clipboard", ["mime"] = "text/plain", ["text"] = "round trip" }, deadline.Token);
            await phone.ControlAsync(new JsonObject { ["type"] = "clipboard", ["mime"] = "text/plain" }, deadline.Token);
            Assert.Equal("round trip", (await phone.UntilAsync("clipboard", deadline.Token))["text"]!.GetValue<string>());
            await phone.ControlAsync(new JsonObject { ["type"] = "windows", ["action"] = "list" }, deadline.Token);
            Assert.Single((await phone.UntilAsync("window_list", deadline.Token))["windows"]!.AsArray());
            await phone.ControlAsync(new JsonObject { ["type"] = "windows", ["action"] = "focus", ["id"] = "window-test" }, deadline.Token);
            await phone.ControlAsync(new JsonObject { ["type"] = "audio", ["action"] = "start_pc",
                ["layouts"] = new JsonArray("5.1_back", "stereo"), ["maxPcmBytes"] = 32768 }, deadline.Token);
            var sound = await phone.UntilAsync("audio_state", deadline.Token);
            Assert.Equal("started", sound["state"]!.GetValue<string>());
            Assert.Equal(6, sound["channels"]!.GetValue<int>());
            Assert.Equal(Wire.KindAudio, (await phone.ReadAsync(deadline.Token)).Kind);
            await phone.ControlAsync(new JsonObject { ["type"] = "microphone", ["action"] = "start" }, deadline.Token);
            Assert.Equal("unavailable", (await phone.UntilAsync("microphone_state", deadline.Token))["state"]!.GetValue<string>());
            await phone.ControlAsync(new JsonObject { ["type"] = "bye", ["reason"] = "test complete" }, deadline.Token);
            await running.WaitAsync(deadline.Token);
            Assert.True(video.Disposed);
            Assert.True(audio.Disposed);
            Assert.Equal("window-test", windows.Activated);
            Assert.Single(input.Events);
            Assert.True(input.Released > 0);
            Assert.True(backend.RemoveCalls > 0);
        }
        finally { deadline.Cancel(); await running; }
    }

    [Fact]
    public async Task Authenticated_microphone_accessory_coexists_with_display_and_cannot_invoke_input_files_or_windows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var backend = new FakeDisplayBackend(2, 2) { FailOnFrameSource = true };
        var video = new Video();
        var mic = new Microphone();
        var input = new RecordingInputSink();
        var credentials = new MemoryDeviceCredentialStore();
        var displaySecret = RandomNumberGenerator.GetBytes(32);
        var micSecret = RandomNumberGenerator.GetBytes(32);
        credentials.Save(new DeviceCredential("display-enrolled", displaySecret));
        credentials.Save(new DeviceCredential("mic-enrolled", micSecret));
        var backendCalls = 0;
        var log = new ListLog();
        var services = new HostServices(() => { Interlocked.Increment(ref backendCalls); return backend; },
            () => input, () => new StaticGuard(), new AutoApprove(), new MemoryPairingStore(),
            DeviceCredentials: credentials, CreateVideoPipeline: () => video,
            CreatePhoneMicrophoneSink: id => id == "mic-enrolled" ? mic : null,
            CanUsePhoneMicrophone: id => id == "mic-enrolled");
        SessionHost? host = null;
        for (var retry = 0; retry < 40; retry++)
        {
            host = new SessionHost(new HostOptions { Bind = "127.0.0.1",
                Port = RandomNumberGenerator.GetInt32(12000, 45000), CameraFreePairing = true,
                AllowPhoneMicrophone = true }, services, log);
            try { await host.StartAsync(deadline.Token); break; }
            catch (System.Net.Sockets.SocketException) { host.Dispose(); host = null; }
        }
        using var hostScope = host ?? throw new InvalidOperationException("No test port trio");
        var running = host.RunSessionsAsync(deadline.Token);
        Exception? failure = null;
        try
        {
            await using var display = await ConnectedPeer.ConnectAsync(host.Offer!, displaySecret, deadline.Token);
            await display.ControlAsync(Hello("display-enrolled"), deadline.Token);
            await display.UntilAsync("target", deadline.Token);
            await using var accessory = await ConnectedPeer.ConnectAsync(host.Offer!, micSecret, deadline.Token);
            await accessory.ControlAsync(Hello("mic-enrolled", accessory: true), deadline.Token);
            await accessory.UntilAsync("bridge_state", deadline.Token);
            await accessory.UntilAsync("stage", deadline.Token); // initial accessory status
            foreach (var denied in new[] {
                new JsonObject { ["type"] = "input", ["event"] = new JsonObject { ["type"] = "text", ["text"] = "forbidden" } },
                new JsonObject { ["type"] = "windows", ["action"] = "focus", ["id"] = "physical-window" },
                new JsonObject { ["type"] = "files", ["action"] = "list" },
            })
            {
                await accessory.ControlAsync(denied, deadline.Token);
                var deniedStage = await accessory.UntilAsync("stage", deadline.Token);
                Assert.Equal("device_bridge", deniedStage["stage"]!.GetValue<string>());
            }
            await accessory.ControlAsync(new JsonObject { ["type"] = "microphone", ["action"] = "start" }, deadline.Token);
            var state = await accessory.UntilAsync("microphone_state", deadline.Token);
            Assert.Equal("started", state["state"]!.GetValue<string>());
            var pcm = new byte[644];
            BinaryPrimitives.WriteInt32BigEndian(pcm, state["streamId"]!.GetValue<int>());
            pcm[4] = 99;
            await accessory.WriteAsync(Wire.KindPhoneMic, pcm, deadline.Token);
            var samples = await mic.Samples.Reader.ReadAsync(deadline.Token);
            Assert.Equal(99, samples[0]);
            Assert.Equal(1, backendCalls);
            Assert.Empty(input.Events);
            Assert.False(video.Disposed);
            await accessory.ControlAsync(new JsonObject { ["type"] = "bye" }, deadline.Token);
            await display.ControlAsync(new JsonObject { ["type"] = "bye" }, deadline.Token);
        }
        catch (Exception ex) { failure = ex; }
        finally { deadline.Cancel(); await running; }
        if (failure is not null) throw new InvalidOperationException(string.Join(Environment.NewLine, log.Lines), failure);
        Assert.True(mic.Disposed);
        Assert.True(video.Disposed);
    }
}
