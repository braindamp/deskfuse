using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskFuse.EnrollmentWorker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class EnrollmentTlsTests
{
    [Fact]
    public async Task InMemoryIdentitySupportsPinnedHandshakeDuplexAndCancellation()
    {
        var identity = EnrollmentTlsIdentity.Create();
        using var certificate = new X509Certificate2(identity.CertificateDer);
        Assert.False(certificate.HasPrivateKey); // Only public DER crosses the worker boundary.
        Assert.InRange(identity.CertificateDer.Length, 1, 2048);
        using var publicKey = certificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.Equal(256, publicKey.KeySize);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var phone = new TcpClient();
        var connecting = phone.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var worker = await listener.AcceptTcpClientAsync(deadline.Token);
        await connecting;
        var channel = new EnrollmentTlsChannel(worker.GetStream(), identity);
        using var tls = new SslStream(phone.GetStream(), false, (_, leaf, _, _) => leaf != null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.GetRawCertData()), SHA256.HashData(identity.CertificateDer)));
        await Task.WhenAll(channel.AuthenticateAsync(deadline.Token), tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "DeskFuse Enrollment", EnabledSslProtocols = SslProtocols.Tls12 }, deadline.Token));
        Assert.Contains(tls.NegotiatedCipherSuite, new[] { TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256 });

        // The worker must forward a broker reply while its phone read is pending.
        var incoming = new byte[32];
        var pending = channel.ReadAsync(incoming, deadline.Token).AsTask();
        var outgoing = RandomNumberGenerator.GetBytes(32);
        await channel.WriteAsync(outgoing, deadline.Token);
        var received = new byte[32];
        await tls.ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(outgoing, received);
        await tls.WriteAsync(received, deadline.Token);
        Assert.Equal(32, await pending);
        Assert.Equal(outgoing, incoming);

        using var cancellation = new CancellationTokenSource();
        var blocked = channel.ReadAsync(incoming, cancellation.Token).AsTask();
        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => blocked.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(error is IOException or ObjectDisposedException or OperationCanceledException, error?.ToString());
    }
}
