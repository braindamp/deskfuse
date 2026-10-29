using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace DeskFuse.AudioBroker;

/// <summary>Service-owned setup composition. No SYSTEM network/TLS parser and no network PC-confirm path.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerSetupCoordinator
{
    private readonly BrokerInstalledDeployment deployment;
    private readonly BrokerHostIdentity signer;
    private readonly TrustedBrokerEnrollmentStore store;
    private readonly EnrollmentPersistenceOwner persistence;
    private readonly BrokerWorkerLauncher launcher = new();
    private readonly BrokerProofScope[] maximumScopes;
    private readonly byte[] bootNonce = RandomNumberGenerator.GetBytes(32);
    private readonly List<BrokerRegisteredWorker> quarantinedActors = [];
    private int occupied;

    internal BrokerSetupCoordinator(BrokerInstalledDeployment deployment, BrokerHostIdentity signer,
        TrustedBrokerEnrollmentStore store, EnrollmentPersistenceOwner persistence, BrokerProofScope[] maximumScopes)
    {
        this.deployment = deployment; this.signer = signer; this.store = store; this.persistence = persistence;
        this.maximumScopes = maximumScopes.ToArray();
        _ = EnrollmentSetupSelection.ChoicePage([], this.maximumScopes, true);
    }

    internal async Task RunActivationAsync(BrokerLocalPipe activationPipe, CancellationToken serviceStop)
    {
        if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0) throw new InvalidOperationException("Setup is active or requires retained recovery.");
        var actors = new List<BrokerRegisteredWorker>();
        var operations = new List<Task>();
        EnrollmentCeremony? ceremony = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serviceStop);
        stop.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            deployment.RequireSignedSetup();
            using var launcherImage = deployment.Acquire(BrokerInstalledRole.Setup);
            using var activation = activationPipe.ObserveSetupActivation(launcherImage);
            var activationFrames = new EnrollmentFraming(activationPipe);
            var begin = await activationFrames.ReadAsync(stop.Token).ConfigureAwait(false);
            if (begin.Kind != EnrollmentFrameKind.BeginSetup || begin.Body.Length != 0) throw new InvalidDataException("Expected local BeginSetup only.");
            var choices = store.ListForSubject(activation.Subject.UserSid);
            var pages = choices.Chunk(64).ToArray();
            if (pages.Length == 0) pages = [[]];
            for (var i = 0; i < pages.Length; i++)
                await activationFrames.WriteAsync(EnrollmentFrameKind.SetupChoices,
                    EnrollmentSetupSelection.ChoicePage(pages[i], maximumScopes, i == pages.Length - 1), stop.Token).ConfigureAwait(false);
            var selected = await activationFrames.ReadAsync(stop.Token).ConfigureAwait(false);
            if (selected.Kind == EnrollmentFrameKind.Cancel && selected.Body.Length == 0) return;
            if (selected.Kind != EnrollmentFrameKind.SelectSetup) throw new InvalidDataException("Selection must come from the retained elevated launcher.");
            var selection = EnrollmentSetupSelection.Parse(selected.Body);
            if (selection.Scopes.Any(s => !maximumScopes.Contains(s)) || selection.Action == EnrollmentAction.Replace && !choices.Any(c => c.Id == selection.OldEnrollment))
                throw new UnauthorizedAccessException("Selection is outside the protected subject/policy.");
            var peer = selection.Action == EnrollmentAction.New ? RandomNumberGenerator.GetBytes(32) : store.ReadActive(selection.OldEnrollment).PeerIdentity;
            var ui = Launch(BrokerInstalledRole.Setup, image => BrokerSetupUiLauncher.Launch(image, activation), actors);
            var transport = Launch(BrokerInstalledRole.EnrollmentWorker, image => launcher.LaunchEnrollment(image, activation.Subject), actors);
            // Genuine UI now owns consent independently of the short-lived UAC launcher.
            await activationFrames.WriteAsync(EnrollmentFrameKind.SetupLaunched, ui.Registration.ToByteArray(true), stop.Token).ConfigureAwait(false);
            using var uiCaller = await Observe(ui, stop.Token).ConfigureAwait(false);
            using var transportCaller = await Observe(transport, stop.Token).ConfigureAwait(false);
            var uiFrames = new EnrollmentFraming(ui.ControlPipe!); var networkFrames = new EnrollmentFraming(transport.ControlPipe!);
            var ready = await networkFrames.ReadAsync(stop.Token).ConfigureAwait(false);
            if (ready.Kind != EnrollmentFrameKind.WorkerReady || ready.Body.Length is < 5 or > 2052) throw new InvalidDataException("Missing bounded worker TLS readiness.");
            var port = BinaryPrimitives.ReadUInt16BigEndian(ready.Body);
            var certificateLength = BinaryPrimitives.ReadUInt16BigEndian(ready.Body.AsSpan(2));
            if (port == 0 || certificateLength == 0 || ready.Body.Length != certificateLength + 4) throw new InvalidDataException("Invalid worker locator/certificate.");
            var offer = new EnrollmentOffer(signer.PublicSpki, signer.NamespaceId, Guid.NewGuid(), Guid.NewGuid(), peer.ToArray(),
                selection.Action, selection.OldEnrollment, bootNonce.ToArray(), RandomNumberGenerator.GetBytes(32), transport.Registration,
                SHA256.HashData(ready.Body.AsSpan(4)), activation.Subject.UserSid, activation.Subject.LogonSid,
                activation.Subject.AuthenticationId, activation.Subject.SessionId, activation.Approver.UserSid,
                ui.Registration, ui.ProcessId, ui.CreationFileTime, selection.Scopes,
                checked((ulong)DeskFuse.WindowsSecurity.WindowsAuthorityClock.NowMilliseconds + 300000));
            var uiLost = 0;
            bool OwnerCurrent() => Volatile.Read(ref uiLost) == 0 && uiCaller.Matches(ui) && transportCaller.Matches(transport) &&
                BrokerSetupActivation.ObserveSubject(offer.SessionId).Matches(activation.Subject);
            ceremony = new(offer, signer, () => DeskFuse.WindowsSecurity.WindowsAuthorityClock.NowMilliseconds, OwnerCurrent);
            var uiOutput = new Output(uiFrames, stop.Token); var networkOutput = new Output(networkFrames, stop.Token);
            Task<EnrollmentFrame>? uiRead = null, networkRead = null;
            try
            {
                uiOutput.Send(EnrollmentFrameKind.ShowOffer, ceremony.SignedOffer);
                var hostName = Encoding.UTF8.GetBytes(Environment.MachineName);
                if (hostName.Length is < 1 or > 255) throw new InvalidDataException("Invalid local setup locator name.");
                var location = new byte[hostName.Length + 2]; BinaryPrimitives.WriteUInt16BigEndian(location, port); hostName.CopyTo(location, 2);
                uiOutput.Send(EnrollmentFrameKind.SetupLocation, location);
                networkOutput.Send(EnrollmentFrameKind.PublishOffer, ceremony.SignedOffer);
                uiRead = uiFrames.ReadAsync(stop.Token).AsTask(); networkRead = networkFrames.ReadAsync(stop.Token).AsTask();
                var pending = new List<Pending>();
                Task<EnrollmentOutcome>? cancel = null;
                var cancellationRequested = false;
                var approvalConnection = 0UL;
                Task<EnrollmentPersistenceOwner.Reply>? commit = null;
                (ulong Connection, byte[] Hash, bool Recovery)? receiptSent = null;
                var controls = 0;
                var finished = false;
                void MaybeCommit()
                {
                    if (ceremony.Phase != EnrollmentCeremonyPhase.ApprovalsComplete || commit is not null) return;
                    commit = persistence.CommitAsync(ceremony); operations.Add(commit);
                    pending.Add(new(approvalConnection, EnrollmentFrameKind.Receipt, commit, null, false));
                }
                void SendNetwork(ulong connection, EnrollmentFrameKind kind, byte[] bytes)
                    => networkOutput.Send(EnrollmentFrameKind.NetworkReply, EnrollmentFraming.Network(connection, new(kind, bytes)));
                while (true)
                {
                    var waits = new List<Task> { uiRead, networkRead, uiOutput.Writer, networkOutput.Writer };
                    waits.AddRange(pending.Select(p => p.Operation)); if (cancel is not null) waits.Add(cancel);
                    var done = await Task.WhenAny(waits).ConfigureAwait(false);
                    if (done == uiOutput.Writer || done == networkOutput.Writer) { await done.ConfigureAwait(false); throw new IOException("Setup output closed unexpectedly."); }
                    if (done == uiRead)
                    {
                        EnrollmentFrame frame;
                        try { frame = await uiRead.ConfigureAwait(false); }
                        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                            Interlocked.Exchange(ref uiLost, 1);
                            if (finished) return; // Retain the real UI until the operator closes the result.
                            if (ceremony.CancelBeforeCommit()) return; // Loss is not post-admission revocation.
                            uiRead = Task.Delay(Timeout.Infinite, stop.Token).ContinueWith<EnrollmentFrame>(_ => throw new OperationCanceledException(), TaskScheduler.Default);
                            continue;
                        }
                        if (!uiCaller.Matches(ui)) throw new UnauthorizedAccessException("Setup UI caller is no longer current.");
                        if (frame.Kind == EnrollmentFrameKind.Confirm)
                        { ceremony.ConfirmFromUi(uiCaller, ui, frame.Body); MaybeCommit(); }
                        else if (frame.Kind == EnrollmentFrameKind.Cancel && (frame.Body.Length == 0 || ceremony.MatchesFrozenBinding(EnrollmentProtocol.ParsePhoneApproval(frame.Body))))
                        { cancellationRequested = true; cancel ??= persistence.CancelAsync(ceremony); operations.Add(cancel); }
                        else throw new InvalidDataException("Unexpected protected UI operation.");
                        uiRead = uiFrames.ReadAsync(stop.Token).AsTask();
                    }
                    else if (done == networkRead)
                    {
                        var frame = await networkRead.ConfigureAwait(false);
                        if (!transportCaller.Matches(transport) || ++controls > 256) throw new UnauthorizedAccessException("Setup transport is stale or exceeded its control bound.");
                        if (frame.Kind == EnrollmentFrameKind.NetworkClosed)
                        {
                            if (frame.Body.Length != 8) throw new InvalidDataException("Invalid connection closure.");
                            // Lost network delivery never manufactures abort or durable revocation.
                        }
                        else if (frame.Kind == EnrollmentFrameKind.NetworkReplySent)
                        {
                            if (frame.Body.Length != 42 || frame.Body[9] > 1) throw new InvalidDataException("Invalid transport write acknowledgement.");
                            if (receiptSent is { } sent && BinaryPrimitives.ReadUInt64BigEndian(frame.Body) == sent.Connection &&
                                frame.Body[8] == (byte)EnrollmentFrameKind.Receipt && CryptographicOperations.FixedTimeEquals(frame.Body.AsSpan(10), sent.Hash))
                            {
                                if (Volatile.Read(ref uiLost) != 0) return;
                                if (!finished) uiOutput.Send(EnrollmentFrameKind.Result, [frame.Body[9] == 1 ? sent.Recovery ? (byte)5 : (byte)1 : (byte)4]);
                                finished = true;
                            }
                        }
                        else if (frame.Kind == EnrollmentFrameKind.NetworkRequest)
                        {
                            var request = EnrollmentFraming.ParseNetwork(frame.Body);
                            switch (request.Frame.Kind)
                            {
                                case EnrollmentFrameKind.Proposal:
                                    var frozen = ceremony.FreezePhoneProposal(request.Frame.Body);
                                    uiOutput.Send(EnrollmentFrameKind.ShowFrozen, frozen); SendNetwork(request.ConnectionId, EnrollmentFrameKind.Frozen, frozen); break;
                                case EnrollmentFrameKind.PhoneApproval:
                                    ceremony.AcceptPhoneApproval(request.Frame.Body); approvalConnection = request.ConnectionId; MaybeCommit(); break;
                                case EnrollmentFrameKind.StatusRequest:
                                    if (pending.Count >= 8) throw new InvalidDataException("Too many pending setup operations.");
                                    var status = persistence.StatusAsync(request.Frame.Body); operations.Add(status);
                                    var statusRequest = EnrollmentProtocol.ParseStatusRequest(EnrollmentFraming.ParseSigned(request.Frame.Body).Message);
                                    pending.Add(new(request.ConnectionId, EnrollmentFrameKind.Status, status, statusRequest.Binding.EnrollmentId, true)); break;
                                default: throw new InvalidDataException("Network input cannot confirm or cancel PC authority.");
                            }
                        }
                        else throw new InvalidDataException("Unexpected worker direction.");
                        networkRead = networkFrames.ReadAsync(stop.Token).AsTask();
                    }
                    else if (done == cancel)
                    {
                        var outcome = await cancel!.ConfigureAwait(false);
                        if (Volatile.Read(ref uiLost) != 0) return;
                        uiOutput.Send(EnrollmentFrameKind.Result, [outcome == EnrollmentOutcome.Revoked ? (byte)3 : (byte)2]);
                        finished = true; cancel = null;
                    }
                    foreach (var item in pending.Where(p => p.Operation.IsCompleted).ToArray())
                    {
                        pending.Remove(item);
                        EnrollmentPersistenceOwner.Reply reply;
                        try { reply = await item.Operation.ConfigureAwait(false); }
                        catch (Exception) when (cancellationRequested && ReferenceEquals(item.Operation, commit))
                        { continue; } // The cancellation/rundown result wins even if its task was already consumed.
                        if (!persistence.AdmitResponse(reply)) continue; // A newer denial wins, including delayed signed status.
                        SendNetwork(item.Connection, item.Kind, reply.Bytes);
                        if (item.Kind == EnrollmentFrameKind.Receipt)
                            receiptSent = (item.Connection, SHA256.HashData(reply.Bytes), item.Recovery);
                        else if (item.Enrollment is { } id && reply.Snapshot.Outcome == EnrollmentOutcome.Committed)
                        {
                            var receipt = persistence.ReceiptAsync(id); operations.Add(receipt);
                            pending.Add(new(item.Connection, EnrollmentFrameKind.Receipt, receipt, id, true));
                        }
                    }
                    operations.RemoveAll(t => t.IsCompleted); // Pending/in-flight operations remain retained until terminal.
                }
            }
            finally
            {
                ceremony.CancelBeforeCommit();
                stop.Cancel();
                // Cancellation requests I/O termination; actually join the pipe owners before releasing actors.
                await JoinRead(uiRead).ConfigureAwait(false); await JoinRead(networkRead).ConfigureAwait(false);
                await uiOutput.JoinAsync().ConfigureAwait(false); await networkOutput.JoinAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ceremony?.CancelBeforeCommit();
            stop.Cancel();
            try { await Task.WhenAll(operations).ConfigureAwait(false); } catch (Exception) { /* terminal failures stay in store/ceremony */ }
            var failures = new List<Exception>();
            foreach (var actor in actors)
            {
                try { actor.StopAndJoin(); actor.Dispose(); }
                catch (Exception error) { lock (quarantinedActors) quarantinedActors.Add(actor); failures.Add(error); }
            }
            Interlocked.Exchange(ref occupied, failures.Count == 0 ? 0 : 2);
            if (failures.Count > 0) throw new AggregateException("Setup actor rundown is unconfirmed; retain custody and block new setup.", failures);
        }
    }

    internal void RetryQuarantinedActors()
    {
        lock (quarantinedActors)
        {
            foreach (var actor in quarantinedActors.ToArray())
            { actor.StopAndJoin(); actor.Dispose(); quarantinedActors.Remove(actor); }
            if (Volatile.Read(ref occupied) == 2) Interlocked.Exchange(ref occupied, 0);
        }
    }
    private BrokerRegisteredWorker Launch(BrokerInstalledRole role, Func<IInstalledBrokerWorkerImage, BrokerRegisteredWorker> create, List<BrokerRegisteredWorker> actors)
    {
        var image = deployment.Acquire(role);
        try { var worker = create(image); actors.Add(worker); return worker; }
        catch (BrokerWorkerLaunchIncompleteException error) { actors.Add(error.Worker); throw; }
        catch { image.Dispose(); throw; }
    }
    private static async Task<BrokerCallerBinding> Observe(BrokerRegisteredWorker worker, CancellationToken ct)
    {
        var pipe = worker.ControlPipe ?? throw new InvalidOperationException("Worker has no reserved control pipe.");
        for (var rejected = 0; rejected < 8; rejected++)
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var caller = pipe.ObserveConnectedCaller();
            if (caller.Matches(worker)) return caller;
            caller.Dispose(); pipe.Disconnect(); // Only before protocol I/O; never reconnect established control.
        }
        throw new UnauthorizedAccessException("Too many foreign clients attempted the setup pipe.");
    }
    private sealed record Pending(ulong Connection, EnrollmentFrameKind Kind, Task<EnrollmentPersistenceOwner.Reply> Operation, Guid? Enrollment, bool Recovery);
    private static async Task JoinRead(Task<EnrollmentFrame>? read)
    { if (read is not null) try { await read.ConfigureAwait(false); } catch (Exception) { /* actual terminal read failure observed */ } }
    private sealed class Output
    {
        private readonly Channel<EnrollmentFrame> queue = Channel.CreateBounded<EnrollmentFrame>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        internal Task Writer { get; }
        internal Output(EnrollmentFraming channel, CancellationToken ct)
            => Writer = Task.Run(async () => { await foreach (var frame in queue.Reader.ReadAllAsync(ct)) await channel.WriteAsync(frame.Kind, frame.Body, ct).ConfigureAwait(false); });
        internal void Send(EnrollmentFrameKind kind, byte[] body)
        { if (!queue.Writer.TryWrite(new(kind, body.ToArray()))) throw new IOException("Bounded setup output is unavailable."); }
        internal async Task CompleteAsync() { queue.Writer.TryComplete(); await Writer.ConfigureAwait(false); }
        internal async Task JoinAsync()
        { queue.Writer.TryComplete(); try { await Writer.ConfigureAwait(false); } catch (Exception) { /* actual terminal writer failure observed */ } }
    }
}
