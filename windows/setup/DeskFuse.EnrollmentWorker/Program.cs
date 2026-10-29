using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskFuse.AudioBroker;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.EnrollmentWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        const string prefix = "--deskfuse-broker-registration=";
        if (args.Length != 1 || !args[0].StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[0][prefix.Length..], "N", out var registration) || registration == Guid.Empty ||
            args[0] != prefix + registration.ToString("N")) return 2;
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var control = await BrokerControlClient.ConnectWorkerAsync(registration, false, lifetime.Token);
            using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=DeskFuse Enrollment", identity, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
            var worker = new SetupNetworkWorker(new(new EnrollmentStreamChannel(control.Stream)), certificate);
            await worker.Run(lifetime.Token); return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AuthenticationException or
            OperationCanceledException or CryptographicException or SocketException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("Enrollment transport stopped: " + error.GetType().Name); return 1; }
    }
}

internal sealed class SetupNetworkWorker(EnrollmentFraming broker, X509Certificate2 certificate)
{
    private sealed record Client(ulong Id, TcpClient Socket, EnrollmentFraming Frames);
    private readonly object gate = new();
    private readonly TaskCompletionSource offerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? publicOffer;
    private Client? current;
    private ulong nextConnection;

    internal async Task Run(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var listener = new TcpListener(IPAddress.Any, 0); listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cert = certificate.RawData;
        if (cert.Length > 2048) throw new InvalidDataException("Enrollment TLS certificate exceeds its bound.");
        var ready = new byte[cert.Length + 4]; BinaryPrimitives.WriteUInt16BigEndian(ready, (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(ready.AsSpan(2), (ushort)cert.Length); cert.CopyTo(ready, 4);
        await broker.WriteAsync(EnrollmentFrameKind.WorkerReady, ready, ct);
        var control = ReadBroker(stop.Token); var network = AcceptPhones(listener, stop.Token);
        var discovery = Discover(port, stop.Token);
        try { var completed = await Task.WhenAny(control, network); await completed; }
        finally
        {
            stop.Cancel(); listener.Stop(); lock (gate) current?.Socket.Dispose();
            // Caller disposes the broker pipe; cancellation also terminates pending overlapped reads.
            try { await Task.WhenAll(control, network, discovery); } catch (Exception error) when (stop.IsCancellationRequested &&
                error is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }
    private static async Task Discover(int port, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 4096 };
            socket.Bind(new IPEndPoint(IPAddress.Any, 47813));
            var probe = System.Text.Encoding.ASCII.GetBytes("DESKFUSE_TRUSTED_SETUP_V1");
            var name = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName);
            if (name.Length > 128) return;
            var response = System.Text.Encoding.ASCII.GetBytes($"DESKFUSE_TRUSTED_SETUP_HOST_V1|{Convert.ToBase64String(name)}|{port}");
            var lastReply = 0L;
            var buffer = new byte[512];
            while (!ct.IsCancellationRequested)
            {
                var request = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                var now = Environment.TickCount64;
                if (!buffer.AsSpan(0, request.ReceivedBytes).SequenceEqual(probe) || now - lastReply < 250) continue;
                lastReply = now;
                await socket.SendToAsync(response.AsMemory(), SocketFlags.None, request.RemoteEndPoint, ct);
            }
        }
        catch (SocketException) { /* Optional locator failure leaves the displayed address/port path available. */ }
    }
    private async Task ReadBroker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await broker.ReadAsync(ct);
            if (frame.Kind == EnrollmentFrameKind.Stop && frame.Body.Length == 0) return;
            if (frame.Kind == EnrollmentFrameKind.PublishOffer)
            {
                var signed = EnrollmentFraming.ParseSigned(frame.Body); var offer = EnrollmentProtocol.ParseOffer(signed.Message);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), offer.TlsLeafSha256) ||
                    !EnrollmentProtocol.Verify(offer.HostSpki, signed.Message, signed.Signature))
                    throw new InvalidDataException("Broker offer does not bind this TLS leaf.");
                lock (gate)
                {
                    if (publicOffer is not null && !publicOffer.AsSpan().SequenceEqual(frame.Body)) throw new InvalidDataException("Enrollment offer changed.");
                    publicOffer = frame.Body.ToArray();
                }
                offerReady.TrySetResult(); continue;
            }
            if (frame.Kind != EnrollmentFrameKind.NetworkReply) throw new InvalidDataException("Unexpected broker network command.");
            var reply = EnrollmentFraming.ParseNetwork(frame.Body);
            if (reply.Frame.Kind is not (EnrollmentFrameKind.Frozen or EnrollmentFrameKind.Receipt or EnrollmentFrameKind.Status or EnrollmentFrameKind.Cancel))
                throw new InvalidDataException("Invalid enrollment reply direction.");
            Client? target; lock (gate) target = current?.Id == reply.ConnectionId ? current : null;
            var sent = false;
            if (target is not null)
            {
                try { await target.Frames.WriteAsync(reply.Frame.Kind, reply.Frame.Body, ct); sent = true; }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { target.Socket.Dispose(); }
            }
            // Actual TLS write completion, tagged to the exact connection and bytes.
            // This is not a phone-activation or durability acknowledgement.
            var acknowledgement = new byte[42]; BinaryPrimitives.WriteUInt64BigEndian(acknowledgement, reply.ConnectionId);
            acknowledgement[8] = (byte)reply.Frame.Kind; acknowledgement[9] = sent ? (byte)1 : (byte)0;
            SHA256.HashData(reply.Frame.Body).CopyTo(acknowledgement, 10);
            await broker.WriteAsync(EnrollmentFrameKind.NetworkReplySent, acknowledgement, ct);
        }
    }
    private async Task AcceptPhones(TcpListener listener, CancellationToken ct)
    {
        await offerReady.Task.WaitAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var socket = await listener.AcceptTcpClientAsync(ct); socket.NoDelay = true;
            Client? client = null;
            try
            {
                using var tls = new SslStream(socket.GetStream(), false);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = certificate, ClientCertificateRequired = false, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowRenegotiation = false }, handshake.Token);
                }
                if (!AllowedCipher(tls.NegotiatedCipherSuite)) throw new AuthenticationException("ECDHE/AEAD TLS is required.");
                var framing = new EnrollmentFraming(new EnrollmentStreamChannel(tls));
                byte[] offer;
                lock (gate) { client = new(checked(++nextConnection), socket, framing); current = client; offer = publicOffer!.ToArray(); }
                await framing.WriteAsync(EnrollmentFrameKind.Offer, offer, ct);
                while (!ct.IsCancellationRequested)
                {
                    var frame = await framing.ReadAsync(ct);
                    if (frame.Kind is not (EnrollmentFrameKind.Proposal or EnrollmentFrameKind.PhoneApproval or EnrollmentFrameKind.StatusRequest or EnrollmentFrameKind.Cancel))
                        throw new InvalidDataException("Unexpected phone setup operation.");
                    await broker.WriteAsync(EnrollmentFrameKind.NetworkRequest, EnrollmentFraming.Network(client.Id, frame), ct);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or AuthenticationException or OperationCanceledException or SocketException) { }
            finally
            {
                if (client is not null)
                {
                    lock (gate) if (ReferenceEquals(current, client)) current = null;
                    var id = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(id, client.Id);
                    if (!ct.IsCancellationRequested) await broker.WriteAsync(EnrollmentFrameKind.NetworkClosed, id, ct);
                }
            }
        }
    }
    private static bool AllowedCipher(TlsCipherSuite cipher) => cipher is TlsCipherSuite.TLS_AES_128_GCM_SHA256 or
        TlsCipherSuite.TLS_AES_256_GCM_SHA384 or TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256 or
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 or TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 or
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256;
}
