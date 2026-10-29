namespace DeskFuse.AudioBroker;

internal enum EndpointOwnershipState { Preparing, Active, Closing, Quarantined, Retired }

/// <summary>
/// Serialised by the broker device gate. Failure never restores grant admission;
/// successful irreversible steps are not repeated when cleanup is retried.
/// </summary>
internal sealed class EndpointRetirement(bool startActive = true)
{
    private volatile EndpointOwnershipState state = startActive ? EndpointOwnershipState.Active : EndpointOwnershipState.Preparing;
    public EndpointOwnershipState State => state;
    private bool pcmRevoked;
    private bool captureStopped;
    public bool CanGrant => State == EndpointOwnershipState.Active;

    public void Activate()
    {
        if (state != EndpointOwnershipState.Preparing) throw new InvalidOperationException("Endpoint cannot be reactivated.");
        state = EndpointOwnershipState.Active;
    }

    public async Task RetireAsync(Action revokePcm, Action stopCapture, Func<Task> removeDevice)
    {
        if (State == EndpointOwnershipState.Retired) return;
        state = EndpointOwnershipState.Closing;
        try
        {
            if (!pcmRevoked) { revokePcm(); pcmRevoked = true; }
            if (!captureStopped) { stopCapture(); captureStopped = true; }
            await removeDevice().ConfigureAwait(false);
            state = EndpointOwnershipState.Retired;
        }
        catch
        {
            state = EndpointOwnershipState.Quarantined;
            throw;
        }
    }
}
