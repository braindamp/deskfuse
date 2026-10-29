using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class EndpointRetirementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failure_quarantines_and_retry_resumes_without_repeating_confirmed_steps(int failedStep)
    {
        var retirement = new EndpointRetirement();
        var calls = new int[3];
        var fail = true;
        void Step(int index)
        {
            Assert.False(retirement.CanGrant);
            calls[index]++;
            if (fail && index == failedStep) throw new IOException("injected failure");
        }
        Task Remove() { Step(2); return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => retirement.RetireAsync(() => Step(0), () => Step(1), Remove));
        Assert.Equal(EndpointOwnershipState.Quarantined, retirement.State);
        Assert.False(retirement.CanGrant);
        Assert.Equal(0, calls.Skip(failedStep + 1).Sum());
        fail = false;
        await retirement.RetireAsync(() => Step(0), () => Step(1), Remove);
        Assert.Equal(EndpointOwnershipState.Retired, retirement.State);
        Assert.False(retirement.CanGrant);
        Assert.Equal(new[] { failedStep == 0 ? 2 : 1, failedStep == 1 ? 2 : 1, failedStep == 2 ? 2 : 1 }, calls);
        await retirement.RetireAsync(() => throw new Exception(), () => throw new Exception(), () => throw new Exception());
    }

    [Fact]
    public async Task Preparing_endpoint_cannot_grant_and_never_reactivates_after_retirement()
    {
        var retirement = new EndpointRetirement(startActive: false);
        Assert.False(retirement.CanGrant);
        retirement.Activate();
        Assert.True(retirement.CanGrant);
        await retirement.RetireAsync(() => { }, () => { }, () => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => retirement.Activate());
        Assert.False(retirement.CanGrant);
    }

    [Fact]
    public async Task Pending_device_removal_is_closing_and_never_admits_new_grants()
    {
        var retirement = new EndpointRetirement();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = retirement.RetireAsync(() => { }, () => { }, () => completion.Task);
        Assert.Equal(EndpointOwnershipState.Closing, retirement.State);
        Assert.False(retirement.CanGrant);
        completion.SetResult();
        await run;
        Assert.Equal(EndpointOwnershipState.Retired, retirement.State);
    }
}
