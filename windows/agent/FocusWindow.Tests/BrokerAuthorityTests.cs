using System.Diagnostics;
using System.Security.Cryptography;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class BrokerAuthorityTests
{
    private sealed class State(byte[] key) : IBrokerAuthorityState
    {
        private readonly object gate = new();
        public bool Active = true;
        public ulong Revision = 1, Creation = 200;
        public byte[] Key = key;
        public int Calls;
        public Action<int>? BeforeCall;
        public static readonly Guid Registration = new("00112233-4455-6677-8899-aabbccddeeff");
        public static readonly byte[] Resource = Enumerable.Repeat((byte)0x22, 32).ToArray();
        public static BrokerAuthorityRequest Request => new(Registration, Resource, BrokerProofOperation.Acquire,
            BrokerProofScope.Speaker, 1);
        public T WithAuthorizedFacts<T>(BrokerAuthorityRequest request, Func<BrokerProofContext, byte[], ulong, T> commit)
        {
            lock (gate)
            {
                Calls++;
                BeforeCall?.Invoke(Calls);
                if (!Active || request.Registration != Registration) throw new UnauthorizedAccessException();
                var context = new BrokerProofContext(Enumerable.Repeat((byte)0x11, 32).ToArray(), Resource,
                    request.Operation, request.Scope, Registration,
                    [1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0],
                    [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0],
                    42, 1, 101, Creation, request.ExpectedOwnerGeneration);
                return commit(context, Key, Revision);
            }
        }
        public void RevokeEnrollment(Guid enrollmentId) { lock (gate) { Active = false; Revision++; } }
    }

    [Fact]
    public void Verified_proof_admits_one_exact_operation_once()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = new State(key.ExportSubjectPublicKeyInfo());
        var authority = new BrokerAuthority(state, () => 10000);
        var issued = authority.Issue(State.Request, 1000);
        var signature = key.SignData(issued.Transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var admitted = authority.Admit(State.Request, issued.Nonce, signature);
        Assert.NotNull(admitted);
        Assert.Equal(State.Registration, admitted.Registration);
        Assert.Equal(State.Resource, admitted.ResourceIdentity);
        Assert.Equal(3, admitted.ExecuteOnce(_ => 3));
        Assert.Throws<InvalidOperationException>(() => admitted.ExecuteOnce(_ => 4));
        Assert.Null(authority.Admit(State.Request, issued.Nonce, signature));
    }

    [Fact]
    public async Task Admission_dispatch_is_single_use_under_concurrency_and_defensively_binds_identity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = new State(key.ExportSubjectPublicKeyInfo());
        var authority = new BrokerAuthority(state, () => 10000);
        var challenge = authority.Issue(State.Request, 1000);
        var admitted = authority.Admit(State.Request, challenge.Nonce,
            key.SignData(challenge.Transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))!;
        var copy = admitted.ResourceIdentity;
        copy[0] = 0;
        Assert.Equal(State.Resource, admitted.ResourceIdentity);
        var calls = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { admitted.ExecuteOnce(admission => Interlocked.Increment(ref calls)); }
            catch (InvalidOperationException) { }
        })));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("revision")]
    [InlineData("process")]
    [InlineData("expiry")]
    public void Final_gate_rejects_change_after_signature_verification(string change)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = new State(key.ExportSubjectPublicKeyInfo());
        long now = 10000;
        var authority = new BrokerAuthority(state, () => now);
        var issued = authority.Issue(State.Request, 1000);
        var signature = key.SignData(issued.Transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        state.BeforeCall = call =>
        {
            if (call != 3) return; // Issue, verification snapshot, then final short gate.
            if (change == "revoke") state.Active = false;
            if (change == "revision") state.Revision++;
            if (change == "process") state.Creation++;
            if (change == "expiry") now = 11000;
        };
        if (change == "revoke") Assert.Throws<UnauthorizedAccessException>(() => authority.Admit(State.Request, issued.Nonce, signature));
        else Assert.Null(authority.Admit(State.Request, issued.Nonce, signature));
    }

    [Fact]
    public void Malformed_signature_burns_challenge_and_unregistered_caller_cannot_issue()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = new State(key.ExportSubjectPublicKeyInfo());
        var authority = new BrokerAuthority(state, () => 10000);
        var issued = authority.Issue(State.Request, 1000);
        var signature = key.SignData(issued.Transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Null(authority.Admit(State.Request, issued.Nonce, []));
        Assert.Null(authority.Admit(State.Request, issued.Nonce, signature));
        Assert.Throws<UnauthorizedAccessException>(() => authority.Issue(State.Request with { Registration = Guid.NewGuid() }, 1000));
    }

    [BrokerKotlinFact]
    public async Task Actual_android_proof_signer_and_windows_broker_accept_the_same_context_and_der_signature()
    {
        var jar = Environment.GetEnvironmentVariable("DESKFUSE_KOTLIN_INTEROP_JAR")!;
        using var process = new Process { StartInfo = new ProcessStartInfo("java")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "-cp", jar, "com.focuswindow.client.transport.BrokerProofKotlinCheck", "bridge" }
        } };
        Assert.True(process.Start());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var spkiHex = await process.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.NotNull(spkiHex);
            // Test-only protected state. The production ceremony never accepts a key nominated by a pipe caller.
            var state = new State(Convert.FromHexString(spkiHex));
            var authority = new BrokerAuthority(state, () => 10000);
            var issued = authority.Issue(State.Request, 1000);
            await process.StandardInput.WriteLineAsync(Convert.ToHexString(issued.Transcript));
            await process.StandardInput.FlushAsync(deadline.Token);
            var signatureHex = await process.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.NotNull(signatureHex);
            Assert.NotNull(authority.Admit(State.Request, issued.Nonce, Convert.FromHexString(signatureHex)));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(deadline.Token));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}

public sealed class BrokerKotlinFactAttribute : FactAttribute
{
    public BrokerKotlinFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKFUSE_KOTLIN_INTEROP_JAR")))
            Skip = "Build the existing protocol3 JVM harness and set DESKFUSE_KOTLIN_INTEROP_JAR.";
    }
}
