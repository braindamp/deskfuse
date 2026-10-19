using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusWindow.Core.Session;
namespace FocusWindow.Core.Protocol;
public static class Wire
{
    /// <summary>Must equal PROTOCOL_VERSION in the Android client (Protocol.kt).</summary>
    public const int ProtocolVersion = 3;
    // Frame kinds on the wire (LanTransport.kt: KIND_*).
    public const byte KindControl = 1;
    public const byte KindFrame = 2;
    public const byte KindPing = 3;
    public const byte KindPong = 4;
    // Host -> phone PCM blocks, only after explicit opt-in from compatible client.
    public const byte KindAudio = 5;
    public const int MaxControlBytes = 1 << 20;
    public const int MaxFrameBytes = 1 << 24;
    public const int FrameMagic = 0x4657; // 'F' 'W'
    public const int FrameHeaderBytes = 20;
    public const int FlagKeyframe = 1 << 0;
    public const int FlagConfig = 1 << 1;
}
/// <summary>Client -> host messages (ClientMessage in Protocol.kt).</summary>
public abstract record ClientMessage;
public sealed record ClientHello(int ProtocolVersion, string ClientVersion, PhoneProfile Profile,
    string Role = "display", IReadOnlyList<string>? Capabilities = null) : ClientMessage;
public sealed record ClientProfile(PhoneProfile Profile) : ClientMessage;
public sealed record ClientDensity(string Preset) : ClientMessage;           // logical | native | one_to_one
public sealed record ClientRefresh(int Hz) : ClientMessage;
public sealed record ClientOccupant(string Want) : ClientMessage;            // canvas | clone | window | session
public sealed record ClientInput(InputEventDto Event) : ClientMessage;
public sealed record ClientClipboard(string Mime, string? Text = null) : ClientMessage;
// Explicit, user-initiated, encrypted Android -> PC file upload. Chunks are base64 in protocol-v3
// control messages, so older clients/hosts can ignore them without changing record framing.
public sealed record ClientFile(string Action, string Id, string? Name = null, long Size = -1,
    string? Base64 = null, string? Sha256 = null, string? Path = null,
    bool OpenOnPc = false) : ClientMessage;
// Explicit full-user-scope operations are available only after interactive host approval.
// The Android client never supplies a security principal: Windows enforces current-user ACLs.
public sealed record ClientFiles(string Action, string? Path = null, string? Id = null, int Offset = 0) : ClientMessage;
public sealed record ClientKeyframeRequest : ClientMessage;
public sealed record ClientWindows(string Action, string? Id) : ClientMessage;
public sealed record ClientAudio(string Action) : ClientMessage;
public sealed record ClientBye(string Reason) : ClientMessage;
public sealed record ClientUnknown(string Type) : ClientMessage;
public static class ClientMessageParser
{
    /// <summary>
    /// Parses one decrypted control payload. Throws <see cref="FormatException"/> for a
    /// structurally invalid message (missing required field) so the session can log and
    /// ignore it; unknown <c>type</c> values are returned as <see cref="ClientUnknown"/>.
    /// </summary>
    public static ClientMessage Parse(ReadOnlySpan<byte> utf8)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(utf8)?.AsObject() ?? throw new FormatException("not a JSON object");
        }
        catch (JsonException ex)
        {
            throw new FormatException("invalid JSON: " + ex.Message, ex);
        }
        var type = root["type"]?.GetValue<string>() ?? throw new FormatException("missing 'type'");
        try
        {
            return type switch
            {
                "hello" => new ClientHello(
                    root["protocolVersion"]?.GetValue<int>() ?? 0,
                    root["clientVersion"]?.GetValue<string>() ?? "?",
                    PhoneProfile.Parse(root["profile"]!.AsObject()), "display",
                    ParseCapabilities(root)),
                // A distinct first-message type ensures pre-bridge hosts reject
                // accessory sessions instead of creating a virtual display.
                "bridge_hello" => new ClientHello(
                    root["protocolVersion"]?.GetValue<int>() ?? 0,
                    root["clientVersion"]?.GetValue<string>() ?? "?",
                    PhoneProfile.Parse(root["profile"]!.AsObject()), "accessory",
                    ParseCapabilities(root)),
                "profile" => new ClientProfile(PhoneProfile.Parse(root["profile"]!.AsObject())),
                "density" => new ClientDensity(root["preset"]!.GetValue<string>()),
                "refresh" => new ClientRefresh(root["hz"]!.GetValue<int>()),
                "occupant" => new ClientOccupant(root["want"]!.GetValue<string>()),
                "input" => new ClientInput(InputEventDto.Parse(root["event"]!.AsObject())),
                "clipboard" => new ClientClipboard(root["mime"]?.GetValue<string>() ?? "", root["text"]?.GetValue<string>()),
                "keyframe" => new ClientKeyframeRequest(),
                "windows" => new ClientWindows(root["action"]?.GetValue<string>() ?? "", root["id"]?.GetValue<string>()),
                "audio" => new ClientAudio(root["action"]?.GetValue<string>() ?? ""),
                "files" => new ClientFiles(root["action"]?.GetValue<string>() ?? "",
                    root["path"]?.GetValue<string>(), root["id"]?.GetValue<string>(),
                    root["offset"]?.GetValue<int>() ?? 0),
                "file" => new ClientFile(root["action"]?.GetValue<string>() ?? "",
                    root["id"]?.GetValue<string>() ?? "", root["name"]?.GetValue<string>(),
                    root["size"]?.GetValue<long>() ?? -1,
                    root["base64"]?.GetValue<string>(), root["sha256"]?.GetValue<string>(),
                    root["path"]?.GetValue<string>(), root["openOnPc"]?.GetValue<bool>() ?? false),
                "bye" => new ClientBye(root["reason"]?.GetValue<string>() ?? ""),
                _ => new ClientUnknown(type),
            };
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new FormatException($"malformed '{type}' message: {ex.Message}", ex);
        }
    }

    // An old bridge_hello without capabilities remains valid but requests no
    // actual hardware role. The client cannot grant itself a Windows endpoint.
    private static IReadOnlyList<string> ParseCapabilities(JsonObject root)
    {
        if (root["capabilities"] is null) return Array.Empty<string>();
        if (root["capabilities"] is not JsonArray array || array.Count > 12)
            throw new FormatException("invalid device capability list");
        return array.Select(value => value?.GetValue<string>()
            ?? throw new FormatException("missing device capability")).ToArray();
    }
}
/// <summary>Host -> client message builders (ServerMessage in Protocol.kt).</summary>
public static class ServerMessages
{
    public static byte[] Serialize(JsonObject o) =>
        Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    public static JsonObject Hello(string hostVersion, string hostName, string channel,
        bool pcAudioAvailable = false) => new()
    {
        ["type"] = "hello",
        ["protocolVersion"] = Wire.ProtocolVersion,
        ["hostVersion"] = hostVersion,
        ["hostName"] = hostName,
        ["channel"] = channel,
        ["pcAudioAvailable"] = pcAudioAvailable,
    };
    // Text only. Never mirror the clipboard without an explicit user request.
    public static JsonObject WindowList(IReadOnlyList<WindowItem> windows)
    {
        var array = new JsonArray();
        foreach (var window in windows)
            array.Add(new JsonObject { ["id"] = window.Id, ["title"] = window.Title, ["appLabel"] = window.AppLabel });
        return new JsonObject { ["type"] = "window_list", ["windows"] = array };
    }
    // Status is about a peer connection, not proof of OS camera/mic availability.
    public static JsonObject BridgeState(IReadOnlyList<string> requested, IReadOnlyList<string> active)
    {
        var wants = new JsonArray();
        foreach (var capability in requested) wants.Add(capability);
        var live = new JsonArray();
        foreach (var capability in active) live.Add(capability);
        return new JsonObject {
            ["type"] = "bridge_state",
            ["requestedCapabilities"] = wants,
            ["activeCapabilities"] = live,
        };
    }

    public static JsonObject AudioState(string state, int streamId, int sampleRate = 0, int channels = 0,
        string? detail = null) => new()
    {
        ["type"] = "audio_state",
        ["state"] = state,
        ["streamId"] = streamId,
        ["sampleRate"] = sampleRate,
        ["channels"] = channels,
        ["format"] = "pcm_s16le",
        ["detail"] = detail,
    };
    public static JsonObject FileList(FileListing listing)
    {
        var entries = new JsonArray();
        foreach (var e in listing.Entries)
            entries.Add(new JsonObject
            {
                ["path"] = e.Path, ["name"] = e.Name,
                ["directory"] = e.IsDirectory, ["size"] = e.Size,
            });
        return new JsonObject {
            ["type"] = "file_list", ["path"] = listing.Path,
            ["parent"] = listing.Parent, ["truncated"] = listing.Truncated,
            ["offset"] = listing.Offset, ["nextOffset"] = listing.NextOffset,
            ["entries"] = entries,
        };
    }
    public static JsonObject FileTransfer(string action, string id, string? name = null,
        long? size = null, string? base64 = null, string? sha256 = null, string? error = null) => new()
    {
        ["type"] = "file_data", ["action"] = action, ["id"] = id,
        ["name"] = name, ["size"] = size, ["base64"] = base64,
        ["sha256"] = sha256, ["error"] = error,
    };
    public static JsonObject ClipboardText(string text) => new()
    {
        ["type"] = "clipboard",
        ["mime"] = "text/plain",
        ["text"] = text,
    };
    public static JsonObject Target(TargetRecord record) => new()
    {
        ["type"] = "target",
        ["record"] = record.ToJson(),
    };
    /// <summary>FR-OPS-03: every non-streaming state is named. Never a blank surface.</summary>
    public static JsonObject Stage(string stage, string detail, string? remediation = null, bool fatal = false) => new()
    {
        ["type"] = "stage",
        ["stage"] = stage,
        ["detail"] = detail,
        ["remediation"] = remediation,
        ["fatal"] = fatal,
    };
    public static JsonObject Quality(string codec, string pixelFormat, int bitrateKbps, int refreshHz, string reason) => new()
    {
        ["type"] = "quality",
        ["codec"] = codec,
        ["pixelFormat"] = pixelFormat,
        ["bitrateKbps"] = bitrateKbps,
        ["refreshHz"] = refreshHz,
        ["reason"] = reason,
    };
    public static JsonObject Invariant(bool clean, IEnumerable<string> drift)
    {
        var arr = new JsonArray();
        foreach (var d in drift) arr.Add(d);
        return new JsonObject { ["type"] = "invariant", ["clean"] = clean, ["drift"] = arr };
    }
}
