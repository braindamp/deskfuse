using System.Runtime.ExceptionServices;

namespace FocusWindow.Core.Session;

/// <summary>
/// Stops the authenticated session when ANY of its three workers finishes or the
/// transport ends. The transport-completion task is a notification, NOT a worker
/// that must be joined: on a graceful phone Bye the TCP reader stays alive until
/// PhoneConnection.DisposeAsync closes the socket. Waiting for that notification
/// after processing Bye would deadlock monitor removal.
/// </summary>
public static class SessionLifetime
{
    public static async Task RunUntilFirstStopAsync(
        Func<CancellationToken, Task> control,
        Func<CancellationToken, Task> video,
        Func<CancellationToken, Task> housekeeping,
        Task transportCompletion,
        CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(housekeeping);
        ArgumentNullException.ThrowIfNull(transportCompletion);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var workerTasks = new[]
        {
            control(linked.Token),
            video(linked.Token),
            housekeeping(linked.Token),
        };
        try
        {
            await Task.WhenAny(workerTasks[0], workerTasks[1], workerTasks[2],
                transportCompletion).ConfigureAwait(false);
        }
        finally
        {
            // Wakes all three workers when a Bye, TCP disconnect, media failure,
            // or host cancellation occurs. This must happen before joining them.
            linked.Cancel();
        }

        // Await only the actual workers, so shutdown cannot be held hostage by an
        // open socket. Preserve a real worker failure even when a different worker
        // finished cleanly. Cancellation is expected during coordinated teardown.
        ExceptionDispatchInfo? firstFailure = null;
        foreach (var task in workerTasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception ex) { firstFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }
        firstFailure?.Throw();
    }
}
