using System.Runtime.Versioning;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class EnrollmentPersistenceOwnerTests
{
    [Fact]
    public async Task Association_flush_retains_custody_until_shutdown_joins_real_io()
    {
        using var fixture = new EnrollmentDurabilityTests.Fixture();
        var store = fixture.Open(); var files = fixture.LastFiles!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.BeforeAssociationComplete = () => { entered.Set(); Assert.True(release.Wait(10000)); };
        var rundown = new Rundown();
        await using var owner = new EnrollmentPersistenceOwner(store, new Signer(), rundown);
        var association = owner.SpeakerAssociationAsync(fixture.Id);
        try
        {
            Assert.True(entered.Wait(10000));
            var stop = owner.StopAsync();
            Assert.Throws<ObjectDisposedException>(() => { _ = owner.SpeakerAssociationAsync(fixture.Id); });
            Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(100)));
            Assert.False(files.Disposed);
            release.Set();
            await Assert.ThrowsAnyAsync<Exception>(async () => await association);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(files.Disposed); Assert.True(rundown.JoinedAll);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Revocation_denies_immediately_while_association_io_is_still_pending()
    {
        using var fixture = new EnrollmentDurabilityTests.Fixture(); var store = fixture.Open();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.BeforeAssociationComplete = () => { entered.Set(); Assert.True(release.Wait(10000)); };
        var rundown = new Rundown();
        await using var owner = new EnrollmentPersistenceOwner(store, new Signer(), rundown);
        var association = owner.SpeakerAssociationAsync(fixture.Id);
        try
        {
            Assert.True(entered.Wait(10000));
            var revoke = owner.RevokeAsync(fixture.Id);
            Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(fixture.Id));
            Assert.False(revoke.IsCompleted); Assert.Equal(EnrollmentOutcome.Unknown, fixture.Status(store));
            release.Set();
            await Assert.ThrowsAnyAsync<Exception>(async () => await association);
            Assert.Equal(EnrollmentOutcome.Revoked, await revoke.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(rundown.Joined); Assert.Equal(EnrollmentOutcome.Revoked, fixture.Status(store));
        }
        finally { release.Set(); }
    }
    private sealed class Signer : IEnrollmentCommitSigner
    {
        public byte[] SignCommittedReceipt(TrustedBrokerEnrollmentStore.Committed committed) => throw new InvalidOperationException();
        public byte[] SignStatus(EnrollmentStatus status) => throw new InvalidOperationException();
    }
    private sealed class Rundown : IBrokerEnrollmentRundown
    {
        internal bool Joined, JoinedAll;
        public void CloseAdmission(Guid enrollment) { }
        public void RevokeAndJoin(Guid enrollment) => Joined = true;
        public void CloseAllAdmission() { }
        public void RevokeAndJoinAll() => JoinedAll = true;
    }
}
