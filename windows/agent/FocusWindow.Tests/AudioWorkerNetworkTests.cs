using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioWorkerNetworkTests
{
    [Fact]
    public void Pipe_RPC_matches_Rust_vectors_and_rejects_truncation_substitution_and_overflow()
    {
        using var f = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-worker-network-v1.json")));
        byte[] Bytes(string k) => Convert.FromHexString(f.RootElement.GetProperty(k).GetString()!);
        var observation = AudioWorkerNetworkProtocol.Observation(Bytes("observationHex"));
        Assert.Equal(Enumerable.Repeat((byte)19, 16), observation.Connection.ToByteArray(bigEndian: true));
        Assert.Equal(Enumerable.Repeat((byte)15, 32), observation.Native);
        foreach (var key in new[] { "infoHex", "prepareHex", "issueHex", "replyHex" })
        {
            var bytes = Bytes(key); var reply = key == "replyHex";
            var value = AudioWorkerNetworkProtocol.Parse(bytes, reply);
            Assert.Equal(bytes, AudioWorkerNetworkProtocol.Encode(value, reply));
            for (var i = 0; i < bytes.Length; i++)
            {
                // Error response bodies are opaque; a header alone is valid.
                if (reply && i >= AudioWorkerNetworkProtocol.HeaderBytes) continue;
                Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Parse(bytes[..i], reply));
            }
        }
        var issue = AudioWorkerNetworkProtocol.Parse(Bytes("issueHex"));
        var noTarget = issue.Body.ToArray(); noTarget.AsSpan(37).Clear();
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Encode(issue with { Body = noTarget }));
        var noSequence = issue.Body.ToArray(); noSequence.AsSpan(1, 8).Clear();
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Encode(issue with { Body = noSequence }));
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Parse(Bytes("infoHex").Concat(new byte[] { 2 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Encode(issue with { Request = 0 }));
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Encode(issue with { Connection = Guid.Empty }));
        var maximum = issue with { Kind = 255, Body = new byte[AudioWorkerNetworkProtocol.MaxBody] };
        _ = AudioWorkerNetworkProtocol.Encode(maximum, reply: true);
        Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.Encode(maximum with { Body = new byte[AudioWorkerNetworkProtocol.MaxBody + 1] }, reply: true));
    }
    [Fact]
    public void Negative_and_read_only_queries_share_the_existing_strict_codecs()
    {
        using var f = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-source-stop-v1.json")));
        foreach (var (kind, key) in new[] { (8, "fastStopHex"), (9, "signedStopHex"), (10, "statusQueryHex"), (11, "appliedResultHex") })
        {
            var bytes = Convert.FromHexString(f.RootElement.GetProperty(key).GetString()!);
            AudioWorkerNetworkProtocol.ValidateRequest((byte)kind, bytes);
            for (var n = 0; n < bytes.Length; n++) Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.ValidateRequest((byte)kind, bytes[..n]));
            Assert.Throws<InvalidDataException>(() => AudioWorkerNetworkProtocol.ValidateRequest((byte)kind, bytes.Concat(new byte[] { 0 }).ToArray()));
        }
    }
}
