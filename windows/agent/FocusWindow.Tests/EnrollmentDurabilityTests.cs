using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DeskFuse.AudioBroker;
using DeskFuse.WindowsSecurity;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class EnrollmentDurabilityTests
{
    [Fact]
    public void Startup_completes_evidence_before_existing_marker_and_never_invents_a_marker()
    {
        using var f = new Fixture();
        using (var store = f.Open())
        {
            Assert.Equal(new[] { EnrollmentPrivateRecordKind.Evidence, EnrollmentPrivateRecordKind.Commit }, f.Completions);
            Assert.Equal(2, store.ReadActive(f.Id).Version);
        }
        f.Data.Remove((f.Id, EnrollmentPrivateRecordKind.Commit)); f.Completions.Clear();
        using (var store = f.Open()) Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(f.Id));
        Assert.Empty(f.Completions);
        Assert.False(f.Data.ContainsKey((f.Id, EnrollmentPrivateRecordKind.Commit)));
        f.SeedMarker(); f.Data[(f.Id, EnrollmentPrivateRecordKind.Commit)] = [1, 2];
        using (var store = f.Open()) Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(f.Id));
        Assert.Empty(f.Completions); // Validate both positive records BEFORE reasserting either.
    }

    [Fact]
    public void Torn_tombstone_denies_but_only_completed_tombstone_reports_revoked()
    {
        using var f = new Fixture();
        f.Data[(f.Id, EnrollmentPrivateRecordKind.Revocation)] = [];
        f.Fail = EnrollmentPrivateRecordKind.Revocation;
        using (var store = f.Open())
        {
            Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(f.Id));
            Assert.Equal(EnrollmentOutcome.Unknown, f.Status(store));
            f.Fail = null; // Reported failure is latched on retained custody, not healed by the next flush.
            Assert.Throws<IOException>(() => store.Revoke(f.Id));
            Assert.Equal(EnrollmentOutcome.Unknown, f.Status(store));
        }
        using (var recovered = f.Open())
        {
            Assert.Equal(EnrollmentOutcome.Revoked, f.Status(recovered));
            Assert.Throws<UnauthorizedAccessException>(() => recovered.ReadActive(f.Id));
        }
    }

    [Fact]
    public void Receipt_recovery_preserves_exact_signature_and_cannot_replace_a_torn_receipt()
    {
        using var f = new Fixture(); var receipt = f.SeedReceipt();
        using (var store = f.Open())
        {
            Assert.Equal(receipt, store.GetReceipt(f.Id, _ => throw new Exception("Must not sign again")));
            Assert.Equal(receipt, store.GetReceipt(f.Id, _ => throw new Exception("Must not sign again")));
            Assert.Single(f.Completions, k => k == EnrollmentPrivateRecordKind.Receipt);
        }
        f.Data[(f.Id, EnrollmentPrivateRecordKind.Receipt)] = receipt[..^1];
        using var recovery = f.Open();
        Assert.Throws<InvalidDataException>(() => recovery.GetReceipt(f.Id, _ => throw new Exception("Must not repair")));
        Assert.Equal(2, recovery.ReadActive(f.Id).Version); // Receipt quarantine does not invent commit revocation.
    }

    [Fact]
    public async Task Denial_beats_delayed_receipt_and_dispose_retains_owner_until_io_is_terminal()
    {
        using var f = new Fixture(); f.SeedReceipt();
        var store = f.Open(); var files = f.LastFiles!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.BeforeComplete = kind => { if (kind == EnrollmentPrivateRecordKind.Receipt) { entered.Set(); Assert.True(release.Wait(10000)); } };
        var response = Task.Run(() => Record.Exception(() => store.GetReceipt(f.Id, _ => throw new Exception("Unexpected signing"))));
        try
        {
            Assert.True(entered.Wait(10000));
            store.Deny(f.Id); // Does not wait behind the blocked flush.
            Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(f.Id));
            var shutdown = Task.Run(store.Dispose);
            Assert.NotSame(shutdown, await Task.WhenAny(shutdown, Task.Delay(100))); Assert.False(files.Disposed);
            release.Set();
            Assert.IsAssignableFrom<Exception>(await response.WaitAsync(TimeSpan.FromSeconds(10)));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(files.Disposed);
        }
        finally { release.Set(); store.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa host = ECDsa.Create(ECCurve.NamedCurves.nistP256), phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly Guid Id = Guid.NewGuid(), Namespace = Guid.NewGuid();
        internal readonly Dictionary<(Guid, EnrollmentPrivateRecordKind), byte[]> Data = [];
        internal readonly List<EnrollmentPrivateRecordKind> Completions = [];
        internal EnrollmentPrivateRecordKind? Fail;
        internal Action<EnrollmentPrivateRecordKind>? BeforeComplete;
        internal FakeFiles? LastFiles;
        private readonly EnrollmentTranscript transcript;
        private readonly byte[] evidence;
        private byte[] Host => SHA256.HashData(host.ExportSubjectPublicKeyInfo());
        internal Fixture()
        {
            var offer = TrustedEnrollmentProtocolTests.Offer with { HostSpki = host.ExportSubjectPublicKeyInfo(), AuthorityNamespace = Namespace, EnrollmentId = Id };
            var proposal = new EnrollmentProposal(SHA256.HashData(EnrollmentProtocol.Offer(offer)), phone.ExportSubjectPublicKeyInfo(), new byte[32], [BrokerProofScope.Speaker]);
            transcript = new(offer, proposal, new byte[32]); var binding = EnrollmentProtocol.Binding(transcript);
            var approval = EnrollmentProtocol.PhoneApproval(binding);
            evidence = EnrollmentEvidenceCodec.Encode(new(2, Namespace, Id, EnrollmentProtocol.Transcript(transcript),
                Sign(host, EnrollmentProtocol.Offer(offer)), Sign(phone, EnrollmentProtocol.Proposal(proposal)),
                Sign(host, EnrollmentProtocol.Frozen(binding.TranscriptSha256)), Sign(phone, approval),
                new(offer.UiRegistration, offer.UiProcessId, offer.UiCreationFileTime, offer.ApproverUserSid, approval), "S-1-5-18"));
            Data[(Id, EnrollmentPrivateRecordKind.Evidence)] = evidence; SeedMarker();
        }
        internal void SeedMarker()
        {
            var b = EnrollmentProtocol.Binding(transcript);
            Data[(Id, EnrollmentPrivateRecordKind.Commit)] = JsonSerializer.SerializeToUtf8Bytes(new
            { Version = 2, Namespace, EnrollmentId = Id, TicketId = b.TicketId, TranscriptSha256 = b.TranscriptSha256, RecordSha256 = SHA256.HashData(evidence) }, ProtectedBrokerEnrollments.Json);
        }
        internal byte[] SeedReceipt()
        {
            var receipt = EnrollmentProtocol.Receipt(new(EnrollmentProtocol.Binding(transcript), transcript.Offer.PeerIdentity,
                transcript.Offer.SubjectUserSid, transcript.Proposal.Scopes, SHA256.HashData(transcript.Proposal.PhoneSpki), SHA256.HashData(evidence)));
            var signed = EnrollmentFraming.Signed(receipt, Sign(host, receipt));
            Data[(Id, EnrollmentPrivateRecordKind.Receipt)] = signed; return signed;
        }
        internal TrustedBrokerEnrollmentStore Open() => new(LastFiles = new FakeFiles(this), Host);
        internal EnrollmentOutcome Status(TrustedBrokerEnrollmentStore store)
        {
            var request = new EnrollmentStatusRequest(EnrollmentProtocol.Binding(transcript), new byte[32], phone.ExportSubjectPublicKeyInfo());
            var bytes = EnrollmentProtocol.StatusRequest(request); return store.StatusFor(request, bytes, Sign(phone, bytes));
        }
        private static byte[] Sign(ECDsa key, byte[] bytes) => key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        public void Dispose() { host.Dispose(); phone.Dispose(); }
        internal sealed class FakeFiles(Fixture owner) : IEnrollmentRecordFiles
        {
            public bool Disposed;
            public Guid NamespaceId => owner.Namespace;
            public string[] EnumerateNames() => owner.Data.Keys.Select(k => k.Item1.ToString("N") + (k.Item2 switch
            { EnrollmentPrivateRecordKind.Evidence => ".v2.record.json", EnrollmentPrivateRecordKind.Commit => ".v2.commit.json", EnrollmentPrivateRecordKind.Receipt => ".v2.receipt.bin", _ => ".revoked.json" })).ToArray();
            public IEnrollmentRecordFile Open(Guid id, EnrollmentPrivateRecordKind kind)
                => owner.Data.ContainsKey((id, kind)) ? new File(this, owner, id, kind) : throw new FileNotFoundException();
            public IEnrollmentRecordFile Create(Guid id, EnrollmentPrivateRecordKind kind)
            { owner.Data.Add((id, kind), []); return new File(this, owner, id, kind); }
            public void Dispose() => Disposed = true;
            private sealed class File(FakeFiles files, Fixture owner, Guid id, EnrollmentPrivateRecordKind kind) : IEnrollmentRecordFile
            {
                private bool completed, failed;
                public byte[] Read() => owner.Data[(id, kind)].ToArray();
                public void CompletePositive(byte[] bytes)
                {
                    if (failed) throw new IOException("Latched writeback failure");
                    var existing = Read();
                    if (existing.Length > 0 && !existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException();
                    Complete(); owner.Data[(id, kind)] = bytes.ToArray();
                }
                public void CompleteDenial() => Complete();
                private void Complete()
                {
                    Assert.False(files.Disposed);
                    if (failed) throw new IOException("Latched writeback failure");
                    if (completed) return;
                    owner.BeforeComplete?.Invoke(kind);
                    if (owner.Fail == kind) { failed = true; throw new IOException("Injected flush failure"); }
                    owner.Completions.Add(kind); completed = true;
                }
                public void Dispose() { }
            }
        }
    }
}
