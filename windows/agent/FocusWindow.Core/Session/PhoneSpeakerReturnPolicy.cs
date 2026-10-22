namespace FocusWindow.Core.Session;

/// <summary>
/// A one-use restoration hint for a Windows render device which was the user's
/// selected output when its authenticated phone left. Values are opaque CoreAudio
/// endpoint IDs; an endpoint's display label, phone IP and Bluetooth name are
/// NEVER used to infer a user preference.
///
/// The ordinary Windows default-device policy gets first chance to restore
/// the device. A host may attempt a conservative fallback only when this
/// ticket has not seen a different user selection during the absence.
/// </summary>
public sealed class PhoneSpeakerReturnPolicy
{
    private static readonly StringComparer Same = StringComparer.OrdinalIgnoreCase;

    private readonly string _departedEndpointId;
    private readonly bool _restoreConsole;
    private readonly bool _restoreMultimedia;
    private AudioRenderDefaults? _fallback;
    private bool _consoleOverridden;
    private bool _multimediaOverridden;

    public PhoneSpeakerReturnPolicy(string departedEndpointId, AudioRenderDefaults beforeDeparture)
    {
        if (string.IsNullOrWhiteSpace(departedEndpointId))
            throw new ArgumentException("A real endpoint ID is required.", nameof(departedEndpointId));
        _departedEndpointId = departedEndpointId;
        _restoreConsole = Same.Equals(beforeDeparture.Console, departedEndpointId);
        _restoreMultimedia = Same.Equals(beforeDeparture.Multimedia, departedEndpointId);
    }

    public bool WasSelected => _restoreConsole || _restoreMultimedia;

    /// <summary>Call only AFTER Windows removed the phone's PnP instance.</summary>
    public void RecordAutomaticFallback(AudioRenderDefaults fallback) => _fallback = fallback;

    /// <summary>
    /// Observations between removal and reappearance. Any departure from
    /// Windows' fallback means someone or something selected a new output.
    /// The veto is sticky even if that output is later changed back.
    /// </summary>
    public void ObserveAbsent(AudioRenderDefaults current)
    {
        if (_fallback is not { } fallback) return;
        if (_restoreConsole && !Same.Equals(fallback.Console, current.Console))
            _consoleOverridden = true;
        if (_restoreMultimedia && !Same.Equals(fallback.Multimedia, current.Multimedia))
            _multimediaOverridden = true;
    }

    /// <summary>
    /// Never select an unrelated role or override another output, and do not
    /// reselect if Windows already restored the phone on its own.
    /// </summary>
    public (bool Console, bool Multimedia) ShouldRestore(
        string returnedEndpointId, AudioRenderDefaults current)
    {
        if (string.IsNullOrWhiteSpace(returnedEndpointId) || _fallback is not { } fallback)
            return (false, false);
        return (
            _restoreConsole && !_consoleOverridden &&
                !Same.Equals(current.Console, returnedEndpointId) &&
                Same.Equals(current.Console, fallback.Console),
            _restoreMultimedia && !_multimediaOverridden &&
                !Same.Equals(current.Multimedia, returnedEndpointId) &&
                Same.Equals(current.Multimedia, fallback.Multimedia)
        );
    }
}

public sealed record AudioRenderDefaults(string? Console, string? Multimedia);
