using System.Net;
using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;
public sealed class AudioListenerProtocolTests
{
    private static AudioCandidatePolicy Policy() => new(1500, 10000, 8, 128, 65536, 32, 8, 8, 500); // Synthetic values only.
    [Fact]
    public void Listener_configuration_and_actual_bind_ack_agree_with_native_vectors()
    {
        using var f = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-listener-v1.json")));
        using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-transport-v1.json")));
        var bytes = Convert.FromHexString(source.RootElement.GetProperty("sourceHex").GetString()!);
        foreach (var family in new[] { 4, 6 })
        {
            var config = Convert.FromHexString(f.RootElement.GetProperty($"configuration{family}Hex").GetString()!);
            var ack = Convert.FromHexString(f.RootElement.GetProperty($"acknowledgement{family}Hex").GetString()!);
            var ip = family == 4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            Assert.Equal(config, AudioListenerProtocol.Encode(bytes, new(ip, 0), Policy()));
            Assert.Equal(new IPEndPoint(ip, 44002), AudioListenerProtocol.VerifyAcknowledgement(config, ack));
            for (var n = 0; n < ack.Length; n++) Assert.Throws<InvalidDataException>(() => AudioListenerProtocol.VerifyAcknowledgement(config, ack[..n]));
            var wrong = ack.ToArray(); wrong[1] ^= 1;
            Assert.Throws<InvalidDataException>(() => AudioListenerProtocol.VerifyAcknowledgement(config, wrong));
            Assert.Throws<InvalidDataException>(() => AudioListenerProtocol.VerifyAcknowledgement(config, ack.Concat(new byte[] { 0 }).ToArray()));
        }
    }
    [Fact]
    public void Listener_policy_has_no_operating_defaults_or_ambiguous_bind_addresses()
    {
        var source = "DeskFuse.Audio.Source.v1\0"u8.ToArray();
        foreach (var policy in new[] { Policy() with { WarmMilliseconds = 0 }, Policy() with { CooldownMilliseconds = 0 },
            Policy() with { QueuedPackets = 33 }, Policy() with { MaxBytes = 4194305 }, Policy() with { ControlsPerTurn = 9 } })
            Assert.Throws<InvalidDataException>(() => AudioListenerProtocol.Encode(source, new(IPAddress.Loopback, 0), policy));
        foreach (var ip in new[] { IPAddress.Any, IPAddress.IPv6Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.1"), IPAddress.Parse("::ffff:127.0.0.1"), IPAddress.Parse("fe80::1") })
            Assert.Throws<InvalidDataException>(() => AudioListenerProtocol.Encode(source, new(ip, 0), Policy()));
    }
}
