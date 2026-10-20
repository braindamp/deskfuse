using NAudio.CoreAudioApi;
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
                Console.WriteLine("Pair the phone in Windows Bluetooth settings first. The phone selects this PC in its normal audio-output picker.");
                return 0;
            }
            if (args[0] == "devices") return await ListAsync();
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
