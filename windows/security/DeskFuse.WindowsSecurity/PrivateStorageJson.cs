using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskFuse.WindowsSecurity;

// Kept private to the storage bootstrap. No dependency on a tool contract or
// caller-mutable serializer options may change the durable bootstrap encoding.
internal static class PrivateStorageJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static T Read<T>(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Missing JSON value.");
    internal static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
