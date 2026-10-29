using System.Security.Cryptography;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class BrokerDeviceProofTests : IDisposable
{
    private readonly ECDsa device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] peer = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private readonly byte[] resource = Enumerable.Repeat((byte)0x22, 32).ToArray();
    private readonly byte[] user = [1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0];
    private readonly byte[] logon = [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0];
    private readonly Guid registration = Guid.NewGuid();
    private long now = 10000;
    private byte[] Public => device.ExportSubjectPublicKeyInfo();
    private byte[] Sign(IssuedBrokerChallenge challenge) => device.SignData(challenge.Transcript,
        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    private BrokerProofContext Context(string? changed = null)
    {
        var peerValue = peer.ToArray(); var resourceValue = resource.ToArray();
        var userValue = user.ToArray(); var logonValue = logon.ToArray();
        if (changed == "peer") peerValue[0]++;
        if (changed == "resource") resourceValue[0]++;
        if (changed == "user") userValue[8]++;
        if (changed == "logon") logonValue[12]++;
        return new(peerValue, resourceValue,
            changed == "operation" ? BrokerProofOperation.Renew : BrokerProofOperation.Acquire,
            changed == "scope" ? BrokerProofScope.Display : BrokerProofScope.Speaker,
            changed == "registration" ? Guid.NewGuid() : registration, userValue, logonValue,
            changed == "authid" ? 43u : 42u, changed == "session" ? 2u : 1u,
            changed == "pid" ? 102u : 101u, changed == "creation" ? 201u : 200u,
            changed == "generation" ? 2u : 1u);
    }

    [Fact]
    public void Transcript_has_the_stable_network_order_golden_encoding_for_android_interop()
    {
        var context = new BrokerProofContext(peer, resource, BrokerProofOperation.Acquire, BrokerProofScope.Speaker,
            new Guid("00112233-4455-6677-8899-aabbccddeeff"), user, logon, 42, 1, 101, 200, 1);
        var actual = Convert.ToHexString(context.Transcript(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray(), 11000)).ToLowerInvariant();
        const string expected = "4465736b467573652e42726f6b657250726f6f662e763100000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f11111111111111111111111111111111111111111111111111111111111111112222222222222222222222222222222222222222222222222222222222222222010100112233445566778899aabbccddeeff0c010100000000000515000000140103000000000005050000001600000021000000000000000000002a000000010000006500000000000000c800000000000000010000000000002af8";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Approved_p256_signature_is_valid_once_only()
    {
        var challenges = new BrokerProofChallenges(() => now);
        var context = Context();
        var challenge = challenges.Issue(context, Public, 1000);
        var signature = Sign(challenge);
        Assert.True(challenges.Consume(challenge.Nonce, signature, context, Public));
        Assert.False(challenges.Consume(challenge.Nonce, signature, context, Public));
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("resource")]
    [InlineData("operation")]
    [InlineData("scope")]
    [InlineData("registration")]
    [InlineData("user")]
    [InlineData("logon")]
    [InlineData("authid")]
    [InlineData("session")]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("generation")]
    public void Proof_cannot_move_between_privileged_contexts(string changed)
    {
        var challenges = new BrokerProofChallenges(() => now);
        var original = Context();
        var challenge = challenges.Issue(original, Public, 1000);
        var signature = Sign(challenge);
        Assert.False(challenges.Consume(challenge.Nonce, signature, Context(changed), Public));
        Assert.False(challenges.Consume(challenge.Nonce, signature, original, Public));
    }

    [Fact]
    public void Expiry_clock_rollback_and_key_rotation_deny_and_consume()
    {
        var challenges = new BrokerProofChallenges(() => now);
        var context = Context();
        var expired = challenges.Issue(context, Public, 10);
        now += 10;
        Assert.False(challenges.Consume(expired.Nonce, Sign(expired), context, Public));
        var rollback = challenges.Issue(context, Public, 100);
        now--;
        Assert.False(challenges.Consume(rollback.Nonce, Sign(rollback), context, Public));
        now++;
        var rotated = challenges.Issue(context, Public, 100);
        Assert.False(challenges.Consume(rotated.Nonce, Sign(rotated), context, other.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Malformed_der_wrong_key_and_mutated_transcript_do_not_verify()
    {
        var challenges = new BrokerProofChallenges(() => now);
        var context = Context();
        var malformed = challenges.Issue(context, Public, 100);
        var noncanonical = Sign(malformed).Concat(new byte[] { 0 }).ToArray();
        Assert.False(challenges.Consume(malformed.Nonce, noncanonical, context, Public));
        Assert.False(challenges.Consume(malformed.Nonce, Sign(malformed), context, Public));
        var wrongKey = challenges.Issue(context, Public, 100);
        var signature = other.SignData(wrongKey.Transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(challenges.Consume(wrongKey.Nonce, signature, context, Public));
        var mutated = challenges.Issue(context, Public, 100);
        mutated.Transcript[^1] ^= 1;
        Assert.False(challenges.Consume(mutated.Nonce, Sign(mutated), context, Public));
    }

    [Fact]
    public void Invalid_curve_and_unbounded_or_malformed_requests_are_rejected()
    {
        var challenges = new BrokerProofChallenges(() => now);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidDataException>(() => challenges.Issue(Context(), wrongCurve.ExportSubjectPublicKeyInfo(), 100));
        Assert.Throws<InvalidDataException>(() => challenges.Issue(Context(), Public, 0));
        Assert.Throws<InvalidDataException>(() => challenges.Issue(Context(), Public, BrokerProofChallenges.MaxChallengeLifetimeMs + 1));
        Assert.Throws<InvalidDataException>(() => new BrokerProofContext(peer, resource, BrokerProofOperation.Acquire,
            BrokerProofScope.Speaker, registration, user, user, 42, 1, 101, 200, 1));
    }

    [Fact]
    public void Outstanding_challenges_are_bounded_and_expiry_returns_capacity()
    {
        var challenges = new BrokerProofChallenges(() => now);
        var context = Context();
        for (var i = 0; i < 16; i++) challenges.Issue(context, Public, 1);
        Assert.Throws<InvalidOperationException>(() => challenges.Issue(context, Public, 1));
        now++;
        Assert.NotNull(challenges.Issue(context, Public, 1));
    }

    [Fact]
    public async Task Concurrent_duplicate_proofs_admit_at_most_once()
    {
        var challenges = new BrokerProofChallenges(() => now);
        var context = Context();
        var key = Public;
        var challenge = challenges.Issue(context, key, 1000);
        var signature = Sign(challenge);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            challenges.Consume(challenge.Nonce, signature, context, key))));
        Assert.Equal(1, results.Count(value => value));
    }

    public void Dispose() { device.Dispose(); other.Dispose(); }
}
