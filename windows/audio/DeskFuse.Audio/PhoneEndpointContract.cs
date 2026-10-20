namespace DeskFuse.Audio;

/// <summary>
/// Identity and wire contract for the per-phone Windows render endpoint.
///
/// A phone's Windows playback endpoint is a real audio device: a software
/// device instance of the DeskFuse Audio driver, created for one enrolled
/// phone and removed when that phone's connection ends. There is exactly one
/// endpoint per phone, never a shared or singleton endpoint.
///
/// The identity is derived from the authenticated enrollment, not from the
/// network: <see cref="DevicePeerRegistry.Identities"/> already hashes host
/// scope + enrollment + capability, and that hash is the peer id used here.
/// </summary>
public static class PhoneEndpointContract
{
    /// <summary>Named pipe the elevated broker listens on.</summary>
    public const string PipeName = "DeskFuse.AudioBroker.v1";

    /// <summary>Hardware id of the DeskFuse Audio driver, as modelled by its INF.</summary>
    public const string HardwareId = @"Root\DeskFuseAudio";

    /// <summary>Service name of the DeskFuse Audio driver.</summary>
    public const string ServiceName = "DeskFuseAudio";

    /// <summary>Prefix of the per-phone software device instance id.</summary>
    public const string InstancePrefix = "deskfuse-speakers-";

    /// <summary>Length of the peer identity hash (hex encoded SHA-256 prefix).</summary>
    public const int PeerIdLength = 32;

    /// <summary>Longest accepted phone label.</summary>
    public const int MaxLabelLength = 48;

    /// <summary>The stable instance segment for one enrolled phone.</summary>
    public static string InstanceId(string peerId)
    {
        if (!IsPeerId(peerId))
            throw new ArgumentException("Peer identity must be 32 hexadecimal characters.", nameof(peerId));
        return InstancePrefix + peerId.ToLowerInvariant();
    }

    /// <summary>
    /// The full device instance path of one enrolled phone, for example
    /// Root\DeskFuseAudio\deskfuse-speakers-9f3c.... The path is the
    /// phone's stable identity in the Windows device tree.
    /// </summary>
    public static string InstancePath(string peerId) =>
        InstanceRoot + "\\" + InstanceId(peerId);

    /// <summary>The enumerator path the DeskFuse Audio driver is installed under.</summary>
    public const string InstanceRoot = @"Root\DeskFuseAudio";

    /// <summary>True when the value is a well formed peer identity hash.</summary>
    public static bool IsPeerId(string? value) =>
        value is { Length: PeerIdLength } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// A phone label is only ever used as a display name. It is bounded,
    /// stripped of control characters and never interpreted.
    /// </summary>
    public static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Phone";
        var cleaned = new string(label.Trim()
            .Where(c => !char.IsControl(c))
            .ToArray());
        if (cleaned.Length == 0) return "Phone";
        return cleaned.Length <= MaxLabelLength ? cleaned : cleaned[..MaxLabelLength];
    }

    /// <summary>
    /// The device name Windows shows for this phone's endpoint. It carries
    /// the product name so a DeskFuse endpoint is never mistaken for a
    /// physical speaker or a Bluetooth device.
    /// </summary>
    public static string DeviceFriendlyName(string label) => "DeskFuse " + NormalizeLabel(label);
}

/// <summary>One request to the endpoint broker.</summary>
public sealed record BrokerRequest(string Op, string? PeerId = null, string? Label = null);

/// <summary>One reply from the endpoint broker.</summary>
public sealed record BrokerResponse(
    bool Ok,
    string? Error = null,
    string? PeerId = null,
    string? EndpointId = null,
    string? DeviceFriendlyName = null,
    string? DeviceInstanceId = null,
    IReadOnlyList<BrokerResponse>? Endpoints = null);

/// <summary>The broker operations the host may ask for.</summary>
public static class BrokerOps
{
    /// <summary>Create the endpoint for one enrolled phone.</summary>
    public const string Create = "create";

    /// <summary>Remove the endpoint of one enrolled phone.</summary>
    public const string Release = "release";

    /// <summary>List the endpoints this broker currently owns.</summary>
    public const string List = "list";

    /// <summary>Liveness check.</summary>
    public const string Ping = "ping";
}
