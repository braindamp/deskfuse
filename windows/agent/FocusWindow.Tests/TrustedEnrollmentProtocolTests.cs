using System.Diagnostics;
using System.Security.Cryptography;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class TrustedEnrollmentProtocolTests
{
    internal static byte[] Public => Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200046B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C2964FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");
    internal static byte[] User => [1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0];
    internal static byte[] Logon => [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0];
    internal static Guid Id => new("00112233-4455-6677-8899-aabbccddeeff");
    internal static EnrollmentOffer Offer => new(Public, Id, Id, Id, new byte[32], EnrollmentAction.New, Guid.Empty,
        new byte[32], new byte[32], Id, new byte[32], User, Logon, 42, 1, User, Id, 101, 200,
        [BrokerProofScope.Speaker, BrokerProofScope.Display], 310000);

    [Fact]
    public void Protected_setup_selection_is_bounded_and_cannot_be_forwarded_as_network_input()
    {
        var choice = new EnrollmentChoice(Id, new byte[32], [BrokerProofScope.Speaker]);
        var page = EnrollmentSetupSelection.ChoicePage([choice], [BrokerProofScope.Speaker, BrokerProofScope.Display], true);
        var decoded = EnrollmentSetupSelection.ParseChoicePage(page);
        Assert.True(decoded.Last); Assert.Equal(Id, Assert.Single(decoded.Choices).Id);
        var selection = new EnrollmentSetupSelection(EnrollmentAction.Replace, Id, [BrokerProofScope.Speaker]);
        Assert.Equal(selection.Encode(), EnrollmentSetupSelection.Parse(selection.Encode()).Encode());
        Assert.Throws<InvalidDataException>(() => EnrollmentSetupSelection.Parse(selection.Encode().Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => (selection with { OldEnrollment = Guid.Empty }).Encode());
        Assert.Throws<InvalidDataException>(() => (selection with { Scopes = [BrokerProofScope.Display, BrokerProofScope.Speaker] }).Encode());
        Assert.Throws<InvalidDataException>(() => EnrollmentFraming.Network(1, new(EnrollmentFrameKind.SelectSetup, selection.Encode())));
    }

    [Fact]
    public void Canonical_messages_round_trip_and_reject_substitution_trailing_and_unbounded_fields()
    {
        var offer = EnrollmentProtocol.Offer(Offer);
        Assert.Equal(offer, EnrollmentProtocol.Offer(EnrollmentProtocol.ParseOffer(offer)));
        var proposal = new EnrollmentProposal(SHA256.HashData(offer), Public, new byte[32], [BrokerProofScope.Speaker]);
        var transcript = new EnrollmentTranscript(Offer, proposal, new byte[32]);
        var bytes = EnrollmentProtocol.Transcript(transcript);
        Assert.Equal(bytes, EnrollmentProtocol.Transcript(EnrollmentProtocol.ParseTranscript(bytes)));
        var binding = EnrollmentProtocol.Binding(transcript);
        var approval = EnrollmentProtocol.PhoneApproval(binding);
        Assert.Equal(approval, EnrollmentProtocol.PhoneApproval(EnrollmentProtocol.ParsePhoneApproval(approval)));
        var receipt = EnrollmentProtocol.Receipt(new(binding, new byte[32], User, proposal.Scopes, SHA256.HashData(Public), new byte[32]));
        Assert.Equal(receipt, EnrollmentProtocol.Receipt(EnrollmentProtocol.ParseReceipt(receipt)));
        var status = EnrollmentProtocol.Status(new(binding, new byte[32], EnrollmentOutcome.Committed));
        Assert.Equal(status, EnrollmentProtocol.Status(EnrollmentProtocol.ParseStatus(status)));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseOffer(offer.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseTranscript(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.Offer(Offer with { Scopes = [BrokerProofScope.Display, BrokerProofScope.Speaker] }));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.Transcript(transcript with { Proposal = proposal with { OfferSha256 = new byte[32] } }));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.Transcript(transcript with { Proposal = proposal with { Scopes = [BrokerProofScope.Camera] } }));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.Offer(Offer with { Action = EnrollmentAction.Replace }));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseStatus(new byte[8193]));
    }

    [Fact]
    public void Full_digest_text_has_no_truncation_or_noncanonical_padding()
    {
        Assert.Equal(new string('0', 52), EnrollmentProtocol.VerificationText(new byte[32]));
        Assert.Equal(new string('Z', 51) + "G", EnrollmentProtocol.VerificationText(Enumerable.Repeat((byte)255, 32).ToArray()));
        var digest = SHA256.HashData(EnrollmentProtocol.Offer(Offer));
        var text = EnrollmentProtocol.VerificationText(digest);
        Assert.Equal(digest, EnrollmentProtocol.ParseVerificationText(string.Join("-", text.Chunk(4).Select(c => new string(c).ToLowerInvariant()))));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseVerificationText(text[..51]));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseVerificationText(new string('Z', 52)));
        Assert.Throws<InvalidDataException>(() => EnrollmentProtocol.ParseVerificationText(new string('O', 52)));
    }

    [Fact]
    public async Task Actual_shipping_Kotlin_agrees_on_all_canonical_bytes_and_signs_proposal()
    {
        var jar = Environment.GetEnvironmentVariable("DESKFUSE_PROTOCOL3_KOTLIN_JAR");
        Assert.True(!string.IsNullOrEmpty(jar) && File.Exists(jar), "Build :protocol3:interopJar and set DESKFUSE_PROTOCOL3_KOTLIN_JAR.");
        using var process = Process.Start(new ProcessStartInfo("java")
        {
            ArgumentList = { "-cp", jar!, "com.focuswindow.client.transport.TrustedEnrollmentKotlinCheck" },
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        })!;
        var errors = process.StandardError.ReadToEndAsync();
        var offer = EnrollmentProtocol.Offer(Offer);
        await process.StandardInput.WriteLineAsync(Convert.ToHexString(offer));
        var proposal = Convert.FromHexString((await process.StandardOutput.ReadLineAsync())!);
        var signature = Convert.FromHexString((await process.StandardOutput.ReadLineAsync())!);
        var parsed = EnrollmentProtocol.ParseProposal(proposal);
        Assert.Equal(SHA256.HashData(offer), parsed.OfferSha256);
        Assert.True(EnrollmentProtocol.Verify(parsed.PhoneSpki, proposal, signature));
        var transcript = new EnrollmentTranscript(Offer, parsed, new byte[32]);
        var bytes = EnrollmentProtocol.Transcript(transcript); var binding = EnrollmentProtocol.Binding(transcript);
        var receipt = EnrollmentProtocol.Receipt(new(binding, Offer.PeerIdentity, User, parsed.Scopes, SHA256.HashData(parsed.PhoneSpki), new byte[32]));
        var status = EnrollmentProtocol.Status(new(binding, new byte[32], EnrollmentOutcome.Pending));
        foreach (var value in new[] { bytes, EnrollmentProtocol.PhoneApproval(binding), receipt, status })
            await process.StandardInput.WriteLineAsync(Convert.ToHexString(value));
        await process.StandardInput.WriteLineAsync(EnrollmentProtocol.VerificationText(binding.TranscriptSha256));
        await process.StandardInput.WriteLineAsync(EnrollmentProtocol.VerificationQr(Id, binding.TranscriptSha256));
        var context = new BrokerProofContext(Offer.PeerIdentity, new byte[32], BrokerProofOperation.Acquire, BrokerProofScope.Speaker,
            Id, User, Logon, 42, 1, 101, 200, 1, new(binding.HostSha256, binding.AuthorityNamespace, binding.EnrollmentId, User));
        var challenges = new BrokerProofChallenges(() => 10000);
        var challenge = challenges.Issue(context, parsed.PhoneSpki, 1000);
        await process.StandardInput.WriteLineAsync(Convert.ToHexString(challenge.Transcript));
        // This vector's public key is the P-256 generator (private scalar 1).
        using var vectorRoot = ECDsa.Create(); vectorRoot.ImportSubjectPublicKeyInfo(Public, out _);
        var vectorParameters = vectorRoot.ExportParameters(false); vectorParameters.D = new byte[32]; vectorParameters.D[^1] = 1;
        vectorRoot.ImportParameters(vectorParameters);
        byte[] SignRoot(byte[] message) => vectorRoot.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        await process.StandardInput.WriteLineAsync(Convert.ToHexString(EnrollmentFraming.Signed(bytes, SignRoot(EnrollmentProtocol.Frozen(binding.TranscriptSha256)))));
        await process.StandardInput.WriteLineAsync(Convert.ToHexString(EnrollmentFraming.Signed(receipt, SignRoot(receipt))));
        var hostChallenge = "DeskFuse.BrokerChallenge.v1\0"u8.ToArray().Concat(SHA256.HashData(challenge.Transcript)).ToArray();
        await process.StandardInput.WriteLineAsync(Convert.ToHexString(SignRoot(hostChallenge)));
        var proof = Convert.FromHexString((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!);
        Assert.True(challenges.Consume(challenge.Nonce, proof, context, parsed.PhoneSpki));
        Assert.Throws<InvalidDataException>(() => new BrokerProofContext(Offer.PeerIdentity, new byte[32], BrokerProofOperation.Acquire,
            BrokerProofScope.Speaker, Id, User, Logon, 42, 1, 101, 200, 1,
            new(binding.HostSha256, binding.AuthorityNamespace, binding.EnrollmentId, Logon)));
        process.StandardInput.Close();
        Assert.Equal("TRUSTED_ENROLLMENT_KOTLIN_PASS", await process.StandardOutput.ReadLineAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await errors);
    }
}
