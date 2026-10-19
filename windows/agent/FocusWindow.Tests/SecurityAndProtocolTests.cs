using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FocusWindow.Core.Display;
using FocusWindow.Core.Input;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;
using Xunit;
namespace FocusWindow.Tests;
public class SecureChannelTests
{
    private static async Task<(Stream Host, Stream Client, IDisposable Cleanup)> Pair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientTcp = new TcpClient { NoDelay = true };
        var connect = clientTcp.ConnectAsync(IPAddress.Loopback, port);
        var hostTcp = await listener.AcceptTcpClientAsync();
        await connect;
        hostTcp.NoDelay = true;
        return (hostTcp.GetStream(), clientTcp.GetStream(), new Disposer(listener, hostTcp, clientTcp));
    }
    private sealed class Disposer(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) { try { i.Dispose(); } catch { } } }
    }
    [Fact]
    public async Task Host_and_client_derive_the_same_keys_and_the_same_six_digit_code()
    {
        var (hostStream, clientStream, cleanup) = await Pair();
        using var _ = cleanup;
        using var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = RandomNumberGenerator.GetBytes(32);
        var spki = hostKey.ExportSubjectPublicKeyInfo();
        var hostTask = SecureChannel.AcceptAsync(hostStream, hostKey, secret, CancellationToken.None);
        var clientTask = SecureChannel.ConnectAsync(clientStream, spki, secret, CancellationToken.None);
        using var host = await hostTask;
        using var client = await clientTask;
        Assert.Equal(client.VerificationCode, host.VerificationCode);
        Assert.Matches(@"^\d{6}$", host.VerificationCode);
        var up = client.Seal(Encoding.UTF8.GetBytes("phone -> pc"));
        Assert.Equal("phone -> pc", Encoding.UTF8.GetString(host.Open(up)));
        var down = host.Seal(Encoding.UTF8.GetBytes("pc -> phone"));
        Assert.Equal("pc -> phone", Encoding.UTF8.GetString(client.Open(down)));
    }
    [Fact]
    public async Task A_peer_without_the_pairing_secret_cannot_authenticate()
    {
        var (hostStream, clientStream, cleanup) = await Pair();
        using var _ = cleanup;
        using var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = hostKey.ExportSubjectPublicKeyInfo();
        var hostTask = SecureChannel.AcceptAsync(hostStream, hostKey, RandomNumberGenerator.GetBytes(32), CancellationToken.None);
        var clientTask = SecureChannel.ConnectAsync(clientStream, spki, RandomNumberGenerator.GetBytes(32), CancellationToken.None);
        using var host = await hostTask;
        using var attacker = await clientTask;
        Assert.NotEqual(attacker.VerificationCode, host.VerificationCode);
        Assert.ThrowsAny<CryptographicException>(() => host.Open(attacker.Seal(Encoding.UTF8.GetBytes("let me in"))));
    }
    [Fact]
    public async Task A_client_rejects_a_host_presenting_a_different_key_than_the_qr()
    {
        var (hostStream, clientStream, cleanup) = await Pair();
        using var _ = cleanup;
        using var realHost = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var qrKey = realHost.ExportSubjectPublicKeyInfo();
        var hostTask = SecureChannel.AcceptAsync(hostStream, impostor, RandomNumberGenerator.GetBytes(32), CancellationToken.None);
        await Assert.ThrowsAsync<CryptographicException>(
            () => SecureChannel.ConnectAsync(clientStream, qrKey, RandomNumberGenerator.GetBytes(32), CancellationToken.None));
        try { using var _h = await hostTask; } catch { /* the client hung up on it */ }
    }
    [Fact]
    public async Task Replays_reordering_and_tampering_are_all_rejected()
    {
        var (hostStream, clientStream, cleanup) = await Pair();
        using var _ = cleanup;
        using var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = RandomNumberGenerator.GetBytes(32);
        var ht = SecureChannel.AcceptAsync(hostStream, hostKey, secret, CancellationToken.None);
        var ct = SecureChannel.ConnectAsync(clientStream, hostKey.ExportSubjectPublicKeyInfo(), secret, CancellationToken.None);
        using var host = await ht;
        using var client = await ct;
        var first = client.Seal(new byte[] { 1 });
        var second = client.Seal(new byte[] { 2 });
        host.Open(first);
        host.Open(second);
        Assert.ThrowsAny<CryptographicException>(() => host.Open(first));      // replay
        var third = client.Seal(new byte[] { 3 });
        var tampered = (byte[])third.Clone();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => host.Open(tampered));   // bit flip in the tag
        Assert.Equal(new byte[] { 3 }, host.Open(third));                      // the genuine packet still works
    }
    /// <summary>
    /// Some Java providers return the ECDH secret with leading zero bytes stripped. That happens in
    /// ~1 of 256 handshakes, so the test FORCES it: it searches for a client key whose shared secret
    /// starts with 0x00, then derives keys the way such a Java client would, from the trimmed bytes.
    /// The host must still authenticate that client's first packet.
    /// </summary>
    [Fact]
    public async Task A_java_client_that_strips_a_leading_zero_from_the_ecdh_secret_still_interoperates()
    {
        using var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = RandomNumberGenerator.GetBytes(32);
        ECDiffieHellman? clientKey = null;
        byte[]? raw = null;
        for (var i = 0; i < 20000; i++)
        {
            var candidate = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var s = candidate.DeriveRawSecretAgreement(hostKey.PublicKey);
            if (s[0] == 0) { clientKey = candidate; raw = s; break; }
            candidate.Dispose();
        }
        Assert.NotNull(clientKey);
        using var _ck = clientKey;
        var (hostStream, clientStream, cleanup) = await Pair();
        using var _ = cleanup;
        var hostTask = SecureChannel.AcceptAsync(hostStream, hostKey, secret, CancellationToken.None);
        // --- the "Java" client: send our key, read the host's, derive from the TRIMMED secret
        var mine = clientKey!.ExportSubjectPublicKeyInfo();
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, mine.Length);
        await clientStream.WriteAsync(lenBuf);
        await clientStream.WriteAsync(mine);
        await clientStream.FlushAsync();
        await clientStream.ReadExactlyAsync(lenBuf);
        var hostSpki = new byte[BinaryPrimitives.ReadInt32BigEndian(lenBuf)];
        await clientStream.ReadExactlyAsync(hostSpki);
        using var host = await hostTask;
        var trimmed = raw!.SkipWhile(b => b == 0).ToArray();
        Assert.True(trimmed.Length < raw.Length);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, trimmed, secret);
        var c2s = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.UTF8.GetBytes("focuswindow:c2s:v3"));
        var s2c = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.UTF8.GetBytes("focuswindow:s2c:v3"));
        byte[] ClientSeal(long counter, byte[] plain)
        {
            var nonce = new byte[12];
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);
            var packet = new byte[8 + plain.Length + 16];
            BinaryPrimitives.WriteInt64BigEndian(packet, counter);
            using var gcm = new AesGcm(c2s, 16);
            gcm.Encrypt(nonce, plain, packet.AsSpan(8, plain.Length), packet.AsSpan(8 + plain.Length, 16));
            return packet;
        }
        Assert.Equal("hello", Encoding.UTF8.GetString(host.Open(ClientSeal(0, Encoding.UTF8.GetBytes("hello")))));
        // ...and the host's reply must be decryptable with the trimmed-secret s2c key.
        var reply = host.Seal(Encoding.UTF8.GetBytes("welcome"));
        var rnonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(rnonce.AsSpan(4), BinaryPrimitives.ReadInt64BigEndian(reply));
        var rplain = new byte[reply.Length - 8 - 16];
        using var rgcm = new AesGcm(s2c, 16);
        rgcm.Decrypt(rnonce, reply.AsSpan(8, rplain.Length), reply.AsSpan(8 + rplain.Length, 16), rplain);
        Assert.Equal("welcome", Encoding.UTF8.GetString(rplain));
    }
}
public class PairingTests
{
    [Fact]
    public void The_qr_uri_has_exactly_the_shape_the_android_parser_requires()
    {
        var state = Pairing.NewState();
        using var offer = Pairing.BuildOffer(state, "192.168.1.24", 47810, "Desk PC");
        var uri = new Uri(offer.Uri);
        Assert.Equal("focuswindow", uri.Scheme);
        Assert.Equal("pair", uri.Host);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("192.168.1.24", q["h"]);
        Assert.Equal("47810", q["p"]);
        Assert.Equal("Desk PC", q["n"]);
        // Android decodes with Base64.URL_SAFE: '+' and '/' would break it.
        foreach (var key in new[] { "k", "s" })
        {
            Assert.DoesNotContain('+', q[key]!);
            Assert.DoesNotContain('/', q[key]!);
        }
        byte[] Decode(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/'));
        Assert.True(Decode(q["s"]!).Length >= 16, "salt must be >= 16 bytes or the client rejects it");
        Assert.Equal(offer.PublicSpki, Decode(q["k"]!)); // the key the host will present on the wire
        Assert.Equal(91, offer.PublicSpki.Length);       // an X.509 SPKI for P-256 (what X509EncodedKeySpec expects)
    }
    [Fact]
    public void A_stored_pairing_is_reused_so_the_phone_can_reconnect_without_a_new_scan()
    {
        var store = new MemoryPairingStore();
        var a = Pairing.LoadOrCreate(store, forceNew: false);
        var b = Pairing.LoadOrCreate(store, forceNew: false);
        var c = Pairing.LoadOrCreate(store, forceNew: true);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        using var o1 = Pairing.BuildOffer(a, "10.0.0.2", 1, "pc");
        using var o2 = Pairing.BuildOffer(b, "10.0.0.2", 1, "pc");
        Assert.Equal(o1.PublicSpki, o2.PublicSpki);
    }
}
public class ProtocolTests
{
    // JSON in the exact shape kotlinx.serialization produces for the Kotlin classes in Protocol.kt.
    private const string ProfileJson =
        "{\"version\":1,\"deviceId\":\"abc\",\"deviceLabel\":\"Google Pixel\",\"physicalW\":1440,\"physicalH\":3120," +
        "\"dpr\":3.5,\"safeTop\":0,\"safeBottom\":0,\"safeLeft\":0,\"safeRight\":0,\"fontScale\":1.0,\"maxRefreshHz\":120," +
        "\"diagonalMm\":170,\"orientation\":\"PORTRAIT\",\"formFactor\":\"PHONE\"}";
    private static ClientMessage Parse(string json) => ClientMessageParser.Parse(Encoding.UTF8.GetBytes(json));
    [Fact]
    public void Hello_carries_the_profile_and_derives_the_density_numbers()
    {
        var hello = Assert.IsType<ClientHello>(Parse("{\"type\":\"hello\",\"protocolVersion\":3,\"clientVersion\":\"1.0.0\",\"profile\":" + ProfileJson + "}"));
        Assert.Equal(3, hello.ProtocolVersion);
        var p = hello.Profile;
        Assert.Equal(1440, p.PhysicalW);
        Assert.Equal(3.5f, p.Dpr);
        Assert.Equal(350, p.RequestedScalePercent);
        Assert.Equal(336, p.ExpectedDpi);
        Assert.Equal(411.4, p.CanvasDpW, 1);
        Assert.Equal("", p.Validate());
    }
    [Fact]
    public void Every_client_message_type_parses()
    {
        Assert.IsType<ClientProfile>(Parse("{\"type\":\"profile\",\"profile\":" + ProfileJson + "}"));
        Assert.Equal("logical", Assert.IsType<ClientDensity>(Parse("{\"type\":\"density\",\"preset\":\"logical\"}")).Preset);
        Assert.Equal(120, Assert.IsType<ClientRefresh>(Parse("{\"type\":\"refresh\",\"hz\":120}")).Hz);
        Assert.Equal("clone", Assert.IsType<ClientOccupant>(Parse("{\"type\":\"occupant\",\"want\":\"clone\"}")).Want);
        Assert.IsType<ClientKeyframeRequest>(Parse("{\"type\":\"keyframe\"}"));
        Assert.Equal("done", Assert.IsType<ClientBye>(Parse("{\"type\":\"bye\",\"reason\":\"done\"}")).Reason);
        Assert.IsType<ClientClipboard>(Parse("{\"type\":\"clipboard\",\"mime\":\"text/plain\",\"text\":\"x\"}"));
        Assert.Equal("future", Assert.IsType<ClientUnknown>(Parse("{\"type\":\"future\"}")).Type);
    }
    [Fact]
    public void Input_events_parse_with_kotlin_defaults_and_stylus_fields()
    {
        var pointer = Assert.IsType<ClientInput>(Parse(
            "{\"type\":\"input\",\"event\":{\"type\":\"pointer\",\"x\":100,\"y\":200,\"action\":\"down\",\"button\":\"right\",\"pressure\":0.6,\"tiltX\":12.5,\"tiltY\":-3.0,\"isStylus\":true}}")).Event;
        Assert.Equal(("pointer", 100, 200, "down", "right", true), (pointer.Kind, pointer.X, pointer.Y, pointer.Action, pointer.Button, pointer.IsStylus));
        Assert.Equal(0.6f, pointer.Pressure, 3);
        Assert.Equal(-3.0f, pointer.TiltY, 3);
        var key = Assert.IsType<ClientInput>(Parse(
            "{\"type\":\"input\",\"event\":{\"type\":\"key\",\"scancode\":30,\"keyCode\":29,\"down\":true,\"modifiers\":3,\"repeat\":0}}")).Event;
        Assert.Equal((30, 29, true, Modifiers.Ctrl | Modifiers.Alt), (key.Scancode, key.AndroidKeyCode, key.Down, key.Modifiers));
        var text = Assert.IsType<ClientInput>(Parse("{\"type\":\"input\",\"event\":{\"type\":\"text\",\"text\":\"héllo\"}}")).Event;
        Assert.Equal("héllo", text.Text);
        var scroll = Assert.IsType<ClientInput>(Parse("{\"type\":\"input\",\"event\":{\"type\":\"scroll\",\"x\":1,\"y\":2,\"dx\":0,\"dy\":-120}}")).Event;
        Assert.Equal(-120, scroll.Dy);
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"nope\":1}")]
    [InlineData("{\"type\":\"hello\",\"protocolVersion\":3}")]            // missing profile
    [InlineData("{\"type\":\"refresh\"}")]                                 // missing hz
    public void Malformed_messages_are_a_format_error_not_a_crash(string json) =>
        Assert.Throws<FormatException>(() => Parse(json));
    [Fact]
    public void Server_messages_contain_every_field_kotlinx_requires_and_only_valid_enum_values()
    {
        var record = new TargetRecord(1, OccupantKind.Canvas, 1440, 3120, 336, 411, 891, true, DensityMethod.AlreadyCorrect,
            "h264", "YUV420", 60, null, StateModel.SharedView, "Phone display", null).ToJson();
        foreach (var key in new[] { "version", "occupant", "widthPx", "heightPx", "dpi", "canvasDpW", "canvasDpH", "native",
                     "densityMethod", "codec", "pixelFormat", "refreshHz", "stateModel", "appLabel" })
            Assert.True(record.ContainsKey(key), $"TargetRecord JSON is missing required field '{key}'");
        Assert.Equal("canvas", (string)record["occupant"]!);
        Assert.Equal("already_correct", (string)record["densityMethod"]!);
        Assert.Equal("shared_view", (string)record["stateModel"]!);
        var hello = ServerMessages.Hello("1.1.0", "PC", "stable");
        Assert.Equal(3, (int)hello["protocolVersion"]!);
        Assert.Equal(Wire.ProtocolVersion, 3);
        var stage = ServerMessages.Stage("capture", "detail", "do this", fatal: true);
        Assert.True((bool)stage["fatal"]!);
        Assert.Equal("do this", (string)stage["remediation"]!);
        var inv = ServerMessages.Invariant(false, new[] { "a", "b" });
        Assert.Equal(2, inv["drift"]!.AsArray().Count);
    }
    [Fact]
    public void Frame_header_constants_match_the_client()
    {
        Assert.Equal(0x4657, Wire.FrameMagic);        // 'F','W' = FrameHeader.MAGIC
        Assert.Equal(20, Wire.FrameHeaderBytes);      // FrameHeader.SIZE_BYTES
        Assert.Equal(1 << 24, Wire.MaxFrameBytes);    // LanTransport.MAX_FRAME_BYTES
        Assert.Equal(1 << 20, Wire.MaxControlBytes);  // LanTransport.MAX_CONTROL_BYTES
        Assert.Equal((byte)1, Wire.KindControl);
        Assert.Equal((byte)2, Wire.KindFrame);
        Assert.Equal((byte)3, Wire.KindPing);
        Assert.Equal((byte)4, Wire.KindPong);
    }
}
public class InputAndGuardTests
{
    [Theory]
    [InlineData(29, 0x41, false)]  // A
    [InlineData(54, 0x5A, false)]  // Z
    [InlineData(7, 0x30, false)]   // 0
    [InlineData(16, 0x39, false)]  // 9
    [InlineData(131, 0x70, false)] // F1
    [InlineData(142, 0x7B, false)] // F12
    [InlineData(66, 0x0D, false)]  // Enter
    [InlineData(67, 0x08, false)]  // Backspace
    [InlineData(111, 0x1B, false)] // Esc
    [InlineData(19, 0x26, true)]   // Up (extended)
    [InlineData(22, 0x27, true)]   // Right (extended)
    [InlineData(112, 0x2E, true)]  // Delete (extended)
    [InlineData(117, 0x5B, true)]  // Win
    [InlineData(144, 0x60, false)] // Numpad 0
    [InlineData(160, 0x0D, true)]  // Numpad Enter (extended)
    public void Android_key_codes_map_to_the_right_virtual_keys(int androidKey, int vk, bool extended)
    {
        Assert.True(KeyMap.TryGet(androidKey, out var key));
        Assert.Equal((ushort)vk, key.VirtualKey);
        Assert.Equal(extended, key.Extended);
    }
    [Fact]
    public void Unknown_keys_are_not_guessed_and_modifier_masks_expand()
    {
        Assert.False(KeyMap.TryGet(9999, out _));
        var mods = KeyMap.ModifierKeys(Modifiers.Ctrl | Modifiers.Shift).Select(k => (int)k.VirtualKey).ToArray();
        Assert.Equal(new[] { 0xA2, 0xA0 }, mods);
        Assert.True(KeyMap.IsModifier(0xA2));
        Assert.False(KeyMap.IsModifier(0x41));
    }
    [Fact]
    public void Pointer_coordinates_map_into_the_phone_monitor_and_are_clamped_to_it()
    {
        var g = new InputGeometry(new RectI(2560, 0, 1440, 3120), 1440, 3120);
        Assert.Equal((2560 + 100, 200), g.ToDesktop(100, 200));
        Assert.Equal((2560, 0), g.ToDesktop(-50, -50));
        Assert.Equal((2560 + 1439, 3119), g.ToDesktop(9999, 9999)); // never leaks onto another monitor
    }
    [Fact]
    public void Physical_display_drift_is_detected_in_every_form()
    {
        var m1 = new PhysicalMonitorInfo(@"\\.\DISPLAY1", new RectI(0, 0, 2560, 1440), 144, true);
        var m2 = new PhysicalMonitorInfo(@"\\.\DISPLAY2", new RectI(2560, 0, 1920, 1080), 96, false);
        var before = new DisplaySnapshot(new[] { m1, m2 });
        Assert.True(DisplayGuardLogic.Compare(before, new DisplaySnapshot(new[] { m1, m2 })).Clean);
        Assert.Contains("rectangle", Assert.Single(DisplayGuardLogic.Compare(before,
            new DisplaySnapshot(new[] { m1, m2 with { Bounds = new RectI(0, 0, 1920, 1080) } })).Drift));
        Assert.Contains("DPI", Assert.Single(DisplayGuardLogic.Compare(before,
            new DisplaySnapshot(new[] { m1 with { Dpi = 192 }, m2 })).Drift));
        Assert.Contains("primary", Assert.Single(DisplayGuardLogic.Compare(before,
            new DisplaySnapshot(new[] { m1 with { Primary = false }, m2 })).Drift));
        Assert.Contains("disappeared", Assert.Single(DisplayGuardLogic.Compare(before, new DisplaySnapshot(new[] { m1 })).Drift));
        Assert.Contains("appeared", Assert.Single(DisplayGuardLogic.Compare(before,
            new DisplaySnapshot(new[] { m1, m2, new PhysicalMonitorInfo(@"\\.\DISPLAY3", new RectI(0, 0, 1, 1), 96, false) })).Drift));
    }
    [Fact]
    public void Physical_size_comes_from_the_diagonal_and_the_serial_is_stable_per_device()
    {
        var (w, h) = PhysicalSize.FromDiagonal(170, 1440, 3120);
        Assert.InRange(w, 70, 72);
        Assert.InRange(h, 153, 155);
        Assert.Equal(170.0, Math.Sqrt(w * w + h * (double)h), 0);
        Assert.Equal(PhysicalSize.StableSerial("device-a"), PhysicalSize.StableSerial("device-a"));
        Assert.NotEqual(PhysicalSize.StableSerial("device-a"), PhysicalSize.StableSerial("device-b"));
        Assert.NotEqual(0u, PhysicalSize.StableSerial(""));
    }
}
