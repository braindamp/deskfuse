using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DeskFuse.AudioBroker;

[SupportedOSPlatform("windows")]
internal static class BrokerProtectedAudioAcquisition
{
    internal sealed record PreparedSession(
        BrokerAudioOwner Owner, BrokerCommittedAudioSource Resource,
        BrokerAudioWorkerSession Session, IPEndPoint Listener);

    internal static async Task<PreparedSession> OpenAsync(
        BrokerInstalledDeployment deployment, BrokerHostIdentity host,
        TrustedBrokerEnrollmentStore store, BrokerAuthorityState registry,
        BrokerEnrollmentRundown rundown, BrokerLogonIdentity approvedLogon,
        Guid enrollmentId, byte[] serviceBoot, AudioLeasePolicy lease,
        AudioWireContext wire, bool minimumPeriod, IPEndPoint bind,
        AudioCandidatePolicy candidatePolicy, Func<ulong> clock,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        deployment.RequireSignedSetup();
        lease.Validate(); wire.Validate(); candidatePolicy.Validate();
        var enrolled = store.ReadActive(enrollmentId);
        if (enrolled.Version != 2 || !enrolled.Scopes.Contains(BrokerProofScope.Speaker) ||
            enrolled.SubjectUserSid is null ||
            !CryptographicOperations.FixedTimeEquals(enrolled.SubjectUserSid, approvedLogon.UserSid) ||
            enrolled.StorageNamespace != host.NamespaceId ||
            !CryptographicOperations.FixedTimeEquals(enrolled.HostIdentitySha256, host.HostSha256))
            throw new UnauthorizedAccessException("The protected speaker enrollment or approved logon changed.");
        var association = store.GetSpeakerAssociation(enrollmentId);
        registry.RequireSpeakerSourceAvailable(association.DeviceKey);
        var image = deployment.Acquire(BrokerInstalledRole.AudioWorker);
        BrokerRegisteredWorker? worker = null;
        BrokerAudioOwner? owner = null;
        BrokerCommittedAudioSource? resource = null;
        var tracked = false;
        try
        {
            worker = new BrokerWorkerLauncher().LaunchAudio(image, approvedLogon, AudioDeliveryMode.Game);
            image = null;
            var session = await BrokerAudioWorkerSession.ConnectAsync(worker, serviceBoot, cancellation)
                .ConfigureAwait(false);
            owner = new(registry, worker, session.Caller, enrollmentId, association,
                AudioSourceBackend.Committed, lease, minimumPeriod, wire, serviceBoot,
                clock, host.SignIssuedAudioChallenge);
            resource = BrokerCommittedAudioSource.Bind(owner, session, rundown, host,
                () => tracked = true);
            var config = AudioListenerConfiguration.Create(owner.Source, bind, candidatePolicy);
            var listener = await session.ConfigureListenerAsync(owner, config, cancellation)
                .ConfigureAwait(false);
            return new(owner, resource, session, listener);
        }
        catch (BrokerWorkerLaunchIncompleteException) when (worker is null)
        {
            image = null;
            throw;
        }
        catch (Exception failure)
        {
            if (tracked && owner is not null)
            {
                owner.CloseAdmission();
                rundown.RequestSourceStop(owner);
            }
            else if (worker is not null)
            {
                owner?.CloseAdmission();
                registry.CloseRegistration(worker.Registration);
                try { worker.StopAndJoin(); worker.Dispose(); }
                catch (Exception cleanup)
                {
                    throw new BrokerWorkerLaunchIncompleteException(worker, failure, cleanup);
                }
            }
            throw;
        }
        finally { image?.Dispose(); }
    }
}
