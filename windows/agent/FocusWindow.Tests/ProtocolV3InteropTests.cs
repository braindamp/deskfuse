using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

/// <summary>
/// P2.1: independently generated, TEST-ONLY P-256/QR/HKDF/AES-GCM vectors shared
/// with tools/protocol3/ProtocolV3KotlinCheck.kt (the real Android SecureChannel).
/// Run tools/run-protocol3-tests.sh to run both and the live C# ↔ Kotlin bridge.
/// </summary>
public sealed class ProtocolV3InteropTests
{
    private sealed class Fixture
    {
        private readonly Dictionary<string, string> _fields;
        public Fixture()
        {
            var file = Path.Combine(AppContext.BaseDirectory, "protocol-v3-vectors.properties");
            _fields = File.ReadAllLines(file).Where(s => s.Length > 0 && !s.StartsWith('#'))
                .Select(s => s.Split('=', 2))
                .ToDictionary(s => s[0], s => s[1], StringComparer.Ordinal);
        }
        public string Get(string key) => _fields[key];
        public byte[] Hex(string key) => Convert.FromHexString(Get(key));
    }
    private static byte[] Encrypt(byte[] key, long counter, byte[] plain)
    {
        var packet = new byte[8 + plain.Length + 16];
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(packet, counter);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, packet.AsSpan(8, plain.Length), packet.AsSpan(8 + plain.Length, 16));
        return packet;
    }
    private static byte[] Decrypt(byte[] key, byte[] packet)
    {
        if (packet.Length <= 24) throw new CryptographicException("short packet");
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), BinaryPrimitives.ReadInt64BigEndian(packet));
        var plain = new byte[packet.Length - 24];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, packet.AsSpan(8, plain.Length), packet.AsSpan(8 + plain.Length, 16), plain);
        return plain;
    }

    [Fact]
    public void Shared_vectors_agree_on_QR_key_identity_raw_ECDH_key_schedule_and_AES_GCM_wire_bytes()
    {
        var f = new Fixture();
        Assert.Equal("3", f.Get("protocolVersion"));
        using var host = ECDiffieHellman.Create();
        using var client = ECDiffieHellman.Create();
        host.ImportPkcs8PrivateKey(f.Hex("hostPrivatePkcs8Hex"), out _);
        client.ImportPkcs8PrivateKey(f.Hex("clientPrivatePkcs8Hex"), out _);
        Assert.Equal(f.Hex("hostPublicSpkiHex"), host.ExportSubjectPublicKeyInfo());
        Assert.Equal(f.Hex("clientPublicSpkiHex"), client.ExportSubjectPublicKeyInfo());
        var shared = client.DeriveRawSecretAgreement(host.PublicKey);
        Assert.Equal(f.Hex("rawSharedSecretHex"), shared);
        Assert.Equal(f.Get("hostFingerprint"), Convert.ToHexString(SHA256.HashData(host.ExportSubjectPublicKeyInfo())[..4]));

        var pairingSecret = f.Hex("pairingSecretHex");
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared, pairingSecret);
        static byte[] Expand(byte[] prk, string info, int len) =>
            HKDF.Expand(HashAlgorithmName.SHA256, prk, len, Encoding.UTF8.GetBytes(info));
        var c2s = Expand(prk, "focuswindow:c2s:v3", 32);
        var s2c = Expand(prk, "focuswindow:s2c:v3", 32);
        Assert.Equal(f.Hex("c2sKeyHex"), c2s);
        Assert.Equal(f.Hex("s2cKeyHex"), s2c);
        var confirmation = Expand(prk, "focuswindow:confirm:v3", 4);
        var code = (BinaryPrimitives.ReadInt32BigEndian(confirmation) & 0x7FFFFFFF) % 1_000_000;
        Assert.Equal(f.Get("confirmationCode"), code.ToString("D6"));

        using var offer = Pairing.BuildOffer(
            new PairingState(f.Hex("hostPrivatePkcs8Hex"), pairingSecret, DateTime.UnixEpoch),
            "192.168.1.24", 47810, "Desk PC");
        Assert.Equal(f.Get("qrUri"), offer.Uri);
        Assert.Equal(f.Hex("hostPublicSpkiHex"), offer.PublicSpki);
        Assert.Equal(f.Hex("clientPacket0Hex"), Encrypt(c2s, 0, f.Hex("clientPlain0Hex")));
        Assert.Equal(f.Hex("clientPacket1Hex"), Encrypt(c2s, 1, f.Hex("clientPlain1Hex")));
        Assert.Equal(f.Hex("hostPacket0Hex"), Encrypt(s2c, 0, f.Hex("hostPlain0Hex")));
        Assert.Equal(f.Hex("hostPacket1Hex"), Encrypt(s2c, 1, f.Hex("hostPlain1Hex")));
        Assert.Equal(f.Hex("clientPlain0Hex"), Decrypt(c2s, f.Hex("clientPacket0Hex")));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(c2s, f.Hex("corruptedClientPacket1Hex")));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(c2s, f.Hex("forgedHighCounterPacket1Hex")));
        var framed = f.Hex("clientControlWire0Hex");
        Assert.Equal((byte)1, framed[0]);
        Assert.Equal(f.Hex("clientPacket0Hex").Length, BinaryPrimitives.ReadInt32BigEndian(framed.AsSpan(1, 4)));
        Assert.Equal(f.Hex("clientPacket0Hex"), framed[5..]);
    }

    /// <summary>
    /// A REAL Kotlin/JCA call chain speaks to the REAL C# SecureChannel over TCP.
    /// No mocked key schedule and no Node translation layer. Enabled when the Kotlin
    /// JDK-only jar is supplied by tools/run-protocol3-tests.sh.
    /// </summary>
    [Fact]
    public async Task Real_Kotlin_SecureChannel_and_CSharp_SecureChannel_exchange_authenticated_packets()
    {
        var jar = Environment.GetEnvironmentVariable("DESKFUSE_KOTLIN_INTEROP_JAR");
        if (string.IsNullOrWhiteSpace(jar)) return; // separate C# fixture test still runs in CI
        Assert.True(File.Exists(jar), "Kotlin interop jar not found: " + jar);
        var f = new Fixture();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("java")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-cp", jar, "com.focuswindow.protocol3.ProtocolV3KotlinCheck", "bridge", port.ToString(),
                                 f.Get("hostPublicSpkiHex"), f.Get("pairingSecretHex") },
            },
        };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = tcp.GetStream();
            using var hostKey = ECDiffieHellman.Create();
            hostKey.ImportPkcs8PrivateKey(f.Hex("hostPrivatePkcs8Hex"), out _);
            using var host = await SecureChannel.AcceptAsync(stream, hostKey, f.Hex("pairingSecretHex"), timeout.Token);
            async Task<string> Read()
            {
                var kind = new byte[1];
                await stream.ReadExactlyAsync(kind, timeout.Token);
                Assert.Equal((byte)1, kind[0]);
                var length = new byte[4];
                await stream.ReadExactlyAsync(length, timeout.Token);
                var count = BinaryPrimitives.ReadInt32BigEndian(length);
                Assert.InRange(count, 25, 4096);
                var packet = new byte[count];
                await stream.ReadExactlyAsync(packet, timeout.Token);
                return Encoding.UTF8.GetString(host.Open(packet));
            }
            Assert.Equal("hello from real Kotlin SecureChannel", await Read());
            Assert.Matches("^[0-9]{6}$", host.VerificationCode);
            var reply = host.Seal(Encoding.UTF8.GetBytes("welcome from CSharp SecureChannel"));
            var wire = new byte[5 + reply.Length];
            wire[0] = 1;
            BinaryPrimitives.WriteInt32BigEndian(wire.AsSpan(1, 4), reply.Length);
            reply.CopyTo(wire, 5);
            await stream.WriteAsync(wire, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            Assert.Equal("confirmed by Kotlin", await Read());
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            Assert.True(process.ExitCode == 0 && stdout.Contains("KOTLIN_CSHARP_BRIDGE_PASS"),
                $"Kotlin bridge exit={process.ExitCode}: {stdout}\n{stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            listener.Stop();
        }
    }
}
