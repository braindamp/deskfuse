using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskFuse.AudioBroker;

internal sealed record AudioCandidatePolicy(uint WarmMilliseconds, uint IdleMilliseconds, ushort QueuedPackets,
    uint MaxPackets, uint MaxBytes, ushort MaxControlMessages, byte PacketsPerTurn, byte ControlsPerTurn, uint CooldownMilliseconds)
{
    internal void Validate()
    {
        if (WarmMilliseconds == 0 || IdleMilliseconds == 0 || CooldownMilliseconds == 0 || QueuedPackets is < 1 or > 32 ||
            MaxPackets is < 1 or > 4096 || MaxBytes is < 1200 or > 4194304 || MaxControlMessages is < 1 or > 256 ||
            PacketsPerTurn < 1 || PacketsPerTurn > QueuedPackets || ControlsPerTurn is < 1 or > 8)
            throw new InvalidDataException("Invalid explicit audio candidate policy.");
    }
}
internal sealed class AudioListenerConfiguration
{
    private readonly byte[] bytes, sourceDigest;
    private AudioListenerConfiguration(AudioSourceDescriptor source, IPEndPoint bind, AudioCandidatePolicy policy, bool handover)
    { sourceDigest = source.Digest; bytes = AudioListenerProtocol.Encode(source.Bytes, bind, policy, handover); }
    internal static AudioListenerConfiguration Create(AudioSourceDescriptor source, IPEndPoint bind, AudioCandidatePolicy policy,
        AudioDeliveryMode mode = AudioDeliveryMode.Game, bool handover = false)
    {
        AudioDeliveryModes.RequireSupported(mode); AudioDeliveryModes.RequireSupported(source.Mode);
        return new(source, bind, policy, handover);
    }
    internal byte[] Bytes => bytes.ToArray();
    internal void RequireSource(AudioSourceDescriptor source)
    { if (!CryptographicOperations.FixedTimeEquals(sourceDigest, source.Digest)) throw new UnauthorizedAccessException("Foreign listener source."); }
    internal IPEndPoint VerifyAcknowledgement(byte[] reply) => AudioListenerProtocol.VerifyAcknowledgement(bytes, reply);
}
/// <summary>Local setup metadata only. The bound pipe supplies identity; these bytes never grant media.</summary>
internal static class AudioListenerProtocol
{
    internal static byte[] Encode(byte[] source, IPEndPoint bind, AudioCandidatePolicy policy, bool handover = false)
    {
        if (source.Length > AudioAuthorityProtocol.MaxDescriptor || !source.AsSpan().StartsWith("DeskFuse.Audio.Source.v1\0"u8)) throw Invalid();
        var ip = bind.Address;
        bind = new(ip.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(ip.GetAddressBytes(), ip.ScopeId) : new IPAddress(ip.GetAddressBytes()), bind.Port);
        policy.Validate(); ValidateAddress(bind.Address);
        using var stream = new MemoryStream(); stream.WriteByte(handover ? (byte)2 : (byte)1); stream.WriteByte(1); U16(stream, checked((ushort)source.Length)); stream.Write(source);
        Address(stream, bind); U32(stream, policy.WarmMilliseconds); U32(stream, policy.IdleMilliseconds); U16(stream, policy.QueuedPackets);
        U32(stream, policy.MaxPackets); U32(stream, policy.MaxBytes); U16(stream, policy.MaxControlMessages);
        stream.WriteByte(policy.PacketsPerTurn); stream.WriteByte(policy.ControlsPerTurn); U32(stream, policy.CooldownMilliseconds);
        return stream.ToArray();
    }
    internal static IPEndPoint VerifyAcknowledgement(byte[] configuration, byte[] reply)
    {
        if (configuration.Length < 4 || reply.Length < 34 || reply[0] != 1 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(configuration), reply.AsSpan(1, 32))) throw Invalid();
        var sourceLength = BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(2));
        var requested = new Reader(configuration.AsSpan(checked(4 + sourceLength))).Address();
        var reader = new Reader(reply.AsSpan(33)); var actual = reader.Address(); reader.End();
        if (actual.Port == 0 || !actual.Address.Equals(requested.Address) || requested.Port != 0 && actual.Port != requested.Port) throw Invalid();
        return actual;
    }
    private static void ValidateAddress(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast || ip.IsIPv4MappedToIPv6 ||
            ip.AddressFamily == AddressFamily.InterNetwork && b[0] is >= 224 and <= 239 ||
            ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.ScopeId > uint.MaxValue || ip.IsIPv6LinkLocal && ip.ScopeId == 0)) throw Invalid();
    }
    private static void Address(Stream s, IPEndPoint value)
    {
        ValidateAddress(value.Address); var ipv6 = value.AddressFamily == AddressFamily.InterNetworkV6;
        if (!ipv6 && value.AddressFamily != AddressFamily.InterNetwork) throw Invalid();
        s.WriteByte(ipv6 ? (byte)6 : (byte)4); s.Write(value.Address.GetAddressBytes()); U16(s, checked((ushort)value.Port));
        if (ipv6) U32(s, checked((uint)value.Address.ScopeId));
    }
    private static void U16(Stream s, ushort n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, n); s.Write(b); }
    private static void U32(Stream s, uint n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); s.Write(b); }
    private ref struct Reader(ReadOnlySpan<byte> b)
    {
        private ReadOnlySpan<byte> remaining = b;
        private ReadOnlySpan<byte> Take(int n) { if (n > remaining.Length) throw Invalid(); var v = remaining[..n]; remaining = remaining[n..]; return v; }
        internal IPEndPoint Address()
        {
            var family = Take(1)[0]; var bytes = Take(family == 4 ? 4 : family == 6 ? 16 : throw Invalid()).ToArray();
            var port = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
            var ip = family == 4 ? new IPAddress(bytes) : new IPAddress(bytes, BinaryPrimitives.ReadUInt32BigEndian(Take(4)));
            ValidateAddress(ip); return new(ip, port);
        }
        internal void End() { if (!remaining.IsEmpty) throw Invalid(); }
    }
    private static InvalidDataException Invalid() => new("Invalid local audio listener configuration.");
}
