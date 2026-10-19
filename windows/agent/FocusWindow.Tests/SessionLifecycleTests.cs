using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

public sealed class SessionLifecycleTests
{
    private static Task UntilCancelled(CancellationToken token) =>
        Task.Delay(Timeout.InfiniteTimeSpan, token);

    [Fact]
    public async Task Graceful_phone_bye_finishes_without_waiting_for_socket_completion()
    {
        var bye = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var videoStopped = false;
        var housekeepingStopped = false;

        async Task Video(CancellationToken token)
        {
            try { await UntilCancelled(token); }
            finally { videoStopped = true; }
        }
        async Task Housekeeping(CancellationToken token)
        {
            try { await UntilCancelled(token); }
            finally { housekeepingStopped = true; }
        }

        var run = SessionLifetime.RunUntilFirstStopAsync(
            _ => bye.Task, Video, Housekeeping, socket.Task, CancellationToken.None);
        bye.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(socket.Task.IsCompleted); // real TCP reader may still be blocked
        Assert.True(videoStopped);
        Assert.True(housekeepingStopped);
    }

    [Fact]
    public async Task Network_disconnect_cancels_workers_and_returns()
    {
        var socket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = SessionLifetime.RunUntilFirstStopAsync(
            UntilCancelled, UntilCancelled, UntilCancelled,
            socket.Task, CancellationToken.None);
        socket.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Media_worker_fault_survives_orderly_cancellation_of_other_workers()
    {
        var transport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = SessionLifetime.RunUntilFirstStopAsync(
            UntilCancelled,
            _ => Task.FromException(new InvalidDataException("broken encoder frame")),
            UntilCancelled, transport.Task, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("broken encoder", failure.Message);
    }

    [Fact]
    public async Task Host_shutdown_finishes_even_if_transport_completion_remains_pending()
    {
        using var shutdown = new CancellationTokenSource();
        var transport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = SessionLifetime.RunUntilFirstStopAsync(
            UntilCancelled, UntilCancelled, UntilCancelled,
            transport.Task, shutdown.Token);
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(transport.Task.IsCompleted);
    }

    [Fact]
    public void Frame_size_limit_accounts_for_h264_header_and_aes_gcm_envelope()
    {
        Assert.Equal(20, FocusWindow.Core.Protocol.Wire.FrameHeaderBytes);
        Assert.Equal(24, SecureChannel.RecordOverheadBytes);
        Assert.Equal((1 << 24) - 20 - 24, PhoneConnection.MaxVideoAccessUnitBytes);
        Assert.False(PhoneConnection.CanSendAccessUnit(0));
        Assert.True(PhoneConnection.CanSendAccessUnit(PhoneConnection.MaxVideoAccessUnitBytes));
        Assert.False(PhoneConnection.CanSendAccessUnit(PhoneConnection.MaxVideoAccessUnitBytes + 1));
    }
}
