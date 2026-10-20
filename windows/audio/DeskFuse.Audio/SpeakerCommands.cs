using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Audio;

namespace DeskFuse.Audio;

public static class SpeakerCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("FocusWindow.Agent speakers devices");
                Console.WriteLine("FocusWindow.Agent speakers receive --source-id BLUETOOTH_ID [--output-name MM-1]");
                Console.WriteLine("FocusWindow.Agent speakers endpoints");
                Console.WriteLine("FocusWindow.Agent speakers endpoint create --peer-id HASH --label NAME");
                Console.WriteLine("FocusWindow.Agent speakers endpoint release --peer-id HASH");
                Console.WriteLine("Pair the phone in Windows Bluetooth settings first. The phone selects this PC in its normal audio-output picker.");
                return 0;
            }
            if (args[0] == "devices") return await ListAsync();
            if (args[0] == "endpoints") return await ListEndpointsAsync();
            if (args[0] == "endpoint") return await EndpointAsync(args[1..]);
            if (args[0] == "audioroute") return await AudioRouteAsync(args[1..]);
            if (args[0] == "props") return await PropsAsync(args[1..]);
            if (args[0] != "receive") throw new ArgumentException("Unknown audio command.");
            var source = Value(args, "--source-id") ?? throw new ArgumentException("Choose a paired source ID from 'devices'.");
            using var outputs = new MMDeviceEnumerator();
            using var output = outputs.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var expected = Value(args, "--output-name");
            if (expected is not null && !output.FriendlyName.Contains(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Windows currently plays through '{output.FriendlyName}'. Select '{expected}' in the Windows output picker, then retry. DeskFuse does not change the PC default silently.");
            var sources = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            var selected = sources.FirstOrDefault(d => d.Id == source)
                ?? throw new InvalidOperationException("That paired Bluetooth audio source is not available. Run 'devices' again.");
            using var outputWatch = new OutputWatch(outputs, output.ID);
            using var connection = AudioPlaybackConnection.TryCreateFromId(selected.Id)
                ?? throw new InvalidOperationException("Windows could not enable this Bluetooth audio receiver.");
            void OnStateChanged(AudioPlaybackConnection sender, object _) =>
                Console.WriteLine($"Bluetooth receiver: {sender.State}");
            connection.StateChanged += OnStateChanged;
            try
            {
                await connection.StartAsync().AsTask(stop.Token);
                var result = await connection.OpenAsync().AsTask(stop.Token);
                if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                    throw new InvalidOperationException($"Bluetooth receiver did not open: {result.Status}; {result.ExtendedError?.Message}");
                Console.WriteLine($"Receiving {selected.Name} through Windows output '{output.FriendlyName}'. Ctrl+C disconnects.");
                var cancelled = Task.Delay(Timeout.Infinite, stop.Token);
                await Task.WhenAny(cancelled, outputWatch.Changed);
                if (outputWatch.Changed.IsCompleted)
                    throw new InvalidOperationException("The Windows output changed or disconnected. Receiver stopped to avoid forwarding phone audio to another endpoint or back to the phone.");
                await cancelled;
            }
            finally
            {
                stop.Cancel();
                connection.StateChanged -= OnStateChanged;
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--")
            ? args[i + 1] : null;
    }

    /// <summary>
    /// Proves that a Windows playback endpoint really accepts application
    /// audio and that its loopback returns those same samples. This is the
    /// route the phone speaker endpoint uses: Windows renders the
    /// application into the endpoint, the endpoint is sampled, and the
    /// samples travel to the phone.
    /// </summary>
    private static async Task<int> AudioRouteAsync(string[] args)
    {
        var endpointId = Value(args, "--endpoint-id");
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            if (endpointId is not null)
            {
                device = enumerator.GetDevice(endpointId);
            }
            else
            {
                foreach (var candidate in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (candidate)
                    {
                        if (candidate.FriendlyName.Contains("DeskFuse", StringComparison.OrdinalIgnoreCase))
                        {
                            device = candidate;
                            break;
                        }
                    }
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Console.Error.WriteLine("That endpoint is not available: " + ex.Message);
            return 1;
        }
        if (device is null)
        {
            Console.Error.WriteLine("No phone speaker endpoint was found. Pass --endpoint-id.");
            return 1;
        }
        using (device)
        {
            Console.WriteLine($"Endpoint: {device.FriendlyName}\n  {device.ID}");

            var captured = new List<byte>();
            using var loopback = new WasapiLoopbackCapture(device);
            loopback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var buffer = new byte[e.BytesRecorded];
                e.Buffer.AsSpan(0, e.BytesRecorded).CopyTo(buffer);
                lock (captured) captured.AddRange(buffer);
            };
            loopback.StartRecording();

            var mix = device.AudioClient.MixFormat;
            var rate = mix.SampleRate;
            var channels = Math.Max(1, mix.Channels);
            var frames = rate * 2;
            var pcm = new byte[frames * channels * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    var value = (short)(Math.Sin(2 * Math.PI * 1000 * frame / rate) * 12000);
                    var index = (frame * channels + ch) * 2;
                    pcm[index] = (byte)(value & 0xff);
                    pcm[index + 1] = (byte)((value >> 8) & 0xff);
                }
            }
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            output.Init(new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(rate, 16, channels)));
            output.Play();
            await Task.Delay(1500).ConfigureAwait(false);
            output.Stop();
            await Task.Delay(400).ConfigureAwait(false);
            loopback.StopRecording();

            byte[] samples;
            lock (captured) samples = captured.ToArray();
            var peak = 0;
            var sumSquares = 0.0;
            var count = samples.Length / 2;
            for (var i = 0; i < count; i++)
            {
                var value = (short)(samples[i * 2] | (samples[i * 2 + 1] << 8));
                peak = Math.Max(peak, Math.Abs((int)value));
                sumSquares += value * (double)value;
            }
            var rms = count == 0 ? 0 : Math.Sqrt(sumSquares / count);
            Console.WriteLine($"Played 1 kHz into the endpoint; loopback returned {samples.Length} bytes.");
            Console.WriteLine($"  peak {peak}, rms {rms:0.0}");
            if (count == 0 || peak < 100)
            {
                Console.Error.WriteLine("The endpoint accepted no real application audio.");
                return 1;
            }
            Console.WriteLine("The endpoint accepted real application audio and returned it on the loopback.");
            return 0;
        }
    }

    /// <summary>
    /// Prints the property store of one endpoint. The names Windows shows
    /// come from these properties, so this is how the per-phone name is
    /// verified.
    /// </summary>
    private static Task<int> PropsAsync(string[] args)
    {
        var wanted = Value(args, "--endpoint-id");
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (wanted is not null && device.ID != wanted) continue;
                Console.WriteLine(device.FriendlyName);
                Console.WriteLine("  " + device.ID);
                try
                {
                    var store = device.Properties;
                    for (var i = 0; i < store.Count; i++)
                    {
                        Console.WriteLine("  " + store.GetValue(i).Value);
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    Console.WriteLine("  (property store unavailable)");
                }
            }
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// Lists the phone speaker endpoints the local broker owns. These are
    /// real Windows playback devices, one per enrolled phone.
    /// </summary>
    private static async Task<int> ListEndpointsAsync()
    {
        await using var client = new PhoneEndpointBrokerClient();
        if (!await client.IsAvailableAsync())
        {
            Console.Error.WriteLine("The DeskFuse audio endpoint broker is not running.");
            Console.Error.WriteLine("Start DeskFuse.AudioBroker elevated so it can create phone speaker devices.");
            return 1;
        }
        var endpoints = await client.ListAsync();
        if (endpoints.Count == 0)
        {
            Console.WriteLine("No phone speaker endpoints are provisioned.");
            return 0;
        }
        foreach (var endpoint in endpoints)
            Console.WriteLine($"{endpoint.DeviceFriendlyName}\n  peer {endpoint.PeerId}\n  {endpoint.EndpointId}");
        return 0;
    }

    /// <summary>
    /// Creates or removes one phone speaker endpoint by hand. The host does
    /// this automatically for a phone that asks for the speakers capability;
    /// this command exists to inspect and verify that path.
    /// </summary>
    private static async Task<int> EndpointAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("FocusWindow.Agent speakers endpoint create --peer-id HASH --label NAME");
            Console.WriteLine("FocusWindow.Agent speakers endpoint release --peer-id HASH");
            return 0;
        }
        var peerId = Value(args, "--peer-id");
        if (peerId is null || !PhoneEndpointContract.IsPeerId(peerId))
        {
            Console.Error.WriteLine("--peer-id must be the 32 character enrollment hash of the phone.");
            return 1;
        }
        await using var client = new PhoneEndpointBrokerClient();
        if (args[0] == "release")
        {
            await client.ReleaseAsync(peerId);
            Console.WriteLine("Released the phone speaker endpoint.");
            return 0;
        }
        if (args[0] != "create")
        {
            Console.Error.WriteLine("Unknown endpoint command.");
            return 1;
        }
        var (endpointId, error) = await client.ProvisionAsync(peerId, Value(args, "--label") ?? "Phone");
        if (endpointId is null)
        {
            Console.Error.WriteLine(error ?? "The phone speaker endpoint could not be created.");
            return 1;
        }
        Console.WriteLine($"Phone speaker endpoint ready: {endpointId}");
        return 0;
    }

    private static async Task<int> ListAsync()
    {
        using var audio = new MMDeviceEnumerator();
        using var current = audio.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        foreach (var device in audio.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try { Console.WriteLine($"Output{(device.ID == current.ID ? " [current]" : "")}: {device.FriendlyName}\n  {device.ID}"); }
                catch (System.Runtime.InteropServices.COMException) { /* a device can depart during enumeration */ }
            }
        }
        var radios = (await Radio.GetRadiosAsync()).Where(r => r.Kind == RadioKind.Bluetooth).ToArray();
        foreach (var radio in radios) Console.WriteLine($"Bluetooth radio: {radio.Name} / {radio.State}");
        if (radios.Length == 0) Console.WriteLine("No Bluetooth radio is available to Windows. A native Android output route requires Bluetooth hardware on the PC.");
        var sources = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
        foreach (var source in sources) Console.WriteLine($"Phone audio source: {source.Name}\n  {source.Id}");
        if (sources.Count == 0) Console.WriteLine("No paired Bluetooth audio source. Pair the phone in Windows Bluetooth settings first.");
        return 0;
    }

    // A future phone render endpoint may itself become the Windows default.
    // Until the Bluetooth receiver has a supported independently pinned output,
    // stop on rerouting instead of creating a phone -> PC -> phone audio loop.
    private sealed class OutputWatch : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _devices;
        private readonly string _id;
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Changed => _changed.Task;
        public OutputWatch(MMDeviceEnumerator devices, string id)
        {
            _devices = devices;
            _id = id;
            _devices.RegisterEndpointNotificationCallback(this);
        }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia && id != _id)
                _changed.TrySetResult();
        }
        public void OnDeviceStateChanged(string id, DeviceState state)
        {
            if (id == _id && state != DeviceState.Active) _changed.TrySetResult();
        }
        public void OnDeviceRemoved(string id) { if (id == _id) _changed.TrySetResult(); }
        public void OnDeviceAdded(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
        public void Dispose() => _devices.UnregisterEndpointNotificationCallback(this);
    }
}
