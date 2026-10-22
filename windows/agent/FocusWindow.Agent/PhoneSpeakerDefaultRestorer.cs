using System.Runtime.InteropServices;
using FocusWindow.Core.Session;
using NAudio.CoreAudioApi;

namespace FocusWindow.Agent;

/// <summary>
/// Remembers a phone that the Windows owner explicitly chose as the default
/// render output. Windows 10's supported endpoint preference algorithm has
/// first chance to restore it after PnP reappearance. A narrowly guarded
/// fallback is used only if the former output was default at departure and
/// NO other selection was observed while the phone was absent.
/// </summary>
internal sealed class PhoneSpeakerDefaultRestorer
{
    private sealed record Pending(PhoneSpeakerReturnPolicy Policy,
        CancellationTokenSource Stop);

    internal sealed record Lease(long Generation, PhoneSpeakerReturnPolicy? ReturnHint);

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private long _generation;

    public Lease Attached(string peerId)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            _active[peerId] = generation;
            PhoneSpeakerReturnPolicy? hint = null;
            if (_pending.Remove(peerId, out var pending))
            {
                hint = pending.Policy;
                pending.Stop.Cancel();
            }
            return new Lease(generation, hint);
        }
    }

    public PhoneSpeakerReturnPolicy? BeforeDeparture(string peerId, long generation,
        string endpointId)
    {
        lock (_gate)
            if (!_active.TryGetValue(peerId, out var current) || current != generation)
                return null; // A newer authenticated connection replaced this lease.

        var defaults = TryReadDefaults();
        if (defaults is null) return null;
        var policy = new PhoneSpeakerReturnPolicy(endpointId, defaults);
        return policy.WasSelected ? policy : null;
    }

    public void Departed(string peerId, long generation, PhoneSpeakerReturnPolicy? hint)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(peerId, out var current) || current != generation)
                return;
            _active.Remove(peerId);
        }

        if (hint is null) return;
        // The caller has awaited the broker's DIF_REMOVE and PnP departure,
        // so this should now be Windows' chosen fallback, not the phone.
        var fallback = TryReadDefaults();
        if (fallback is null) return;
        hint.RecordAutomaticFallback(fallback);

        var stop = new CancellationTokenSource();
        var entry = new Pending(hint, stop);
        lock (_gate)
        {
            if (_active.ContainsKey(peerId)) { stop.Dispose(); return; }
            if (_pending.Remove(peerId, out var old)) old.Stop.Cancel();
            _pending[peerId] = entry;
        }
        // No Android polling and no network probing. This observes LOCAL
        // Windows user choices only while the previously preferred phone
        // output is absent. The session stays fully disconnected.
        _ = ObserveAbsentAsync(peerId, entry);
    }

    private async Task ObserveAbsentAsync(string peerId, Pending entry)
    {
        try
        {
            // A one-hour cap is intentionally conservative. After this, rely
            // on Windows' own preferences and never force a possibly stale
            // restore when the phone eventually rejoins.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token);
            timeout.CancelAfter(TimeSpan.FromHours(1));
            while (true)
            {
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
                var current = TryReadDefaults();
                if (current is null) continue;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(peerId, out var actual) ||
                        !ReferenceEquals(actual, entry)) return;
                    entry.Policy.ObserveAbsent(current);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(peerId, out var current) &&
                    ReferenceEquals(current, entry))
                    _pending.Remove(peerId);
            }
            entry.Stop.Dispose();
        }
    }

    public async Task RestoreOnReturnAsync(string peerId, string returnedEndpointId,
        Lease lease, CancellationToken ct)
    {
        var hint = lease.ReturnHint;
        if (hint is null) return;

        // Windows itself may reselect the stable PnP instance and endpoint
        // through its preference history. Let that finish first.
        await Task.Delay(400, ct).ConfigureAwait(false);
        var current = TryReadDefaults();
        if (current is null) return;

        lock (_gate)
            if (!_active.TryGetValue(peerId, out var active) ||
                active != lease.Generation) return;

        var (console, multimedia) = hint.ShouldRestore(returnedEndpointId, current);
        if (!console && !multimedia) return;

        // The documented MMDevice API is read-only for default selection.
        // This optional fallback uses Windows' private audio policy COM API,
        // which is not guaranteed by Microsoft across Windows releases.
        // Never touch communications, an unrelated device or an explicit
        // change made while the phone was disconnected.
        if (console) TrySetDefault(returnedEndpointId, Role.Console);
        if (multimedia) TrySetDefault(returnedEndpointId, Role.Multimedia);
    }

    private static AudioRenderDefaults? TryReadDefaults()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var console = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            using var multimedia = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioRenderDefaults(console.ID, multimedia.ID);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // A missing endpoint or a CoreAudio restart is not permission
            // to overwrite the user's default output.
            return null;
        }
    }

    private static void TrySetDefault(string endpointId, Role role)
    {
        // The target MUST already exist and be an ACTIVE render endpoint.
        // This guards against policy writes to a stale ID during fast roaming.
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            using (var endpoint = enumerator.GetDevice(endpointId))
                if (endpoint.State != DeviceState.Active || endpoint.DataFlow != DataFlow.Render)
                    return;

            var instance = new WindowsPolicyConfigClient();
            try
            {
                var policy = (IWindowsPolicyConfig)instance;
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, (int)role));
                Console.WriteLine($"speaker-default-restored=True role={role} endpoint={endpointId}");
            }
            finally { Marshal.ReleaseComObject(instance); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or
                                   InvalidOperationException or ArgumentException or
                                   NotSupportedException)
        {
            Console.Error.WriteLine(
                $"speaker-default-restored=False role={role} private-policy-unavailable={ex.GetType().Name}");
        }
    }

    // Windows 10's private IPolicyConfig interface: vtable slots 3..12
    // are uncalled placeholders; SetDefaultEndpoint is slot 13.
    // Keep all private policy usage confined to this single adapter.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class WindowsPolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWindowsPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpointId, int role);
    }
}
