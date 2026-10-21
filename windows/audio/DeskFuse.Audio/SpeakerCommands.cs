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
                Console.WriteLine("FocusWindow.Agent speakers taskbar-watch [--seconds 25]  (read-only; no sound injection)");
                Console.WriteLine("FocusWindow.Agent speakers audioroute --endpoint-id ID --peer-id HASH");
                Console.WriteLine("FocusWindow.Agent speakers audioroute --endpoint-id ID --peer-id HASH");
                Console.WriteLine("FocusWindow.Agent speakers endpoint create --peer-id HASH --label NAME");
                Console.WriteLine("FocusWindow.Agent speakers endpoint release --peer-id HASH");
                Console.WriteLine("Pair the phone in Windows Bluetooth settings first. The phone selects this PC in its normal audio-output picker.");
                return 0;
            }
            if (args[0] == "devices") return await ListAsync();
            if (args[0] == "endpoints") return await ListEndpointsAsync();
            if (args[0] == "taskbar-watch")
            {
                var value = Value(args, "--seconds") ?? "25";
                if (!int.TryParse(value, out var seconds))
                    throw new ArgumentException("--seconds must be an integer between 5 and 120.");
                return await TaskbarAudioWatch.RunAsync(seconds, stop.Token).ConfigureAwait(false);
            }
            if (args[0] == "endpoint") return await EndpointAsync(args[1..]);
            if (args[0] == "audioroute") return await AudioRouteAsync(args[1..], stop.Token);
            if (args[0] == "props") return await PropsAsync(args[1..]);
            if (args[0] == "open") return await OpenAsync(args[1..]);
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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
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
    private static async Task<int> AudioRouteAsync(string[] args, CancellationToken cancellationToken)
    {
        var endpointId = Value(args, "--endpoint-id")
            ?? throw new ArgumentException("audioroute requires --endpoint-id; no default output is selected.");
        var peerId = Value(args, "--peer-id");
        if (!PhoneEndpointContract.IsPeerId(peerId))
            throw new ArgumentException("audioroute requires --peer-id with the phone's 32 hexadecimal identity.");
        var expectedInstance = PhoneEndpointContract.InstancePath(peerId!);
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            throw new InvalidOperationException("The explicit endpoint is not an active playback endpoint.");
        var matchesInstance = false;
        var properties = device.Properties;
        for (var i = 0; i < properties.Count; i++)
        {
            string? value;
            try { value = properties.GetValue(i).Value as string; }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException) { continue; }
            if (value is null) continue;
            // The audio endpoint stores its adapter path as e.g. {1}.ROOT\MEDIA\... .
            // Compare the full per-phone path, never a display name or a partial identity.
            if (value.Equals(expectedInstance, StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("}." + expectedInstance, StringComparison.OrdinalIgnoreCase))
            {
                matchesInstance = true;
                break;
            }
        }
        if (!matchesInstance)
            throw new InvalidOperationException("Refusing playback: the endpoint properties do not identify " +
                "the requested per-phone DeskFuse device: " + expectedInstance);
        Console.WriteLine($"Endpoint: {device.FriendlyName}\n  {device.ID}\n  {expectedInstance}");
        return await LoopbackSignalProbe.RunAsync(device, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens one endpoint by id and reports what Windows says.</summary>
    private static Task<int> OpenAsync(string[] args)
    {
        var id = Value(args, "--endpoint-id");
        if (id is null) { Console.Error.WriteLine("--endpoint-id is required."); return Task.FromResult(1); }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            Console.WriteLine("opened: " + device.FriendlyName);
            try
            {
                var volume = device.AudioEndpointVolume;
                Console.WriteLine("volume: " + volume.MasterVolumeLevelScalar);
            }
            catch (Exception ex)
            {
                Console.WriteLine("volume failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine("open failed: " + ex.GetType().Name + ": " + ex.Message);
            return Task.FromResult(1);
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
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or NotImplementedException)
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
