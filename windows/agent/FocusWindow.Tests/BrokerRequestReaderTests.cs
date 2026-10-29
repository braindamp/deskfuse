using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class BrokerRequestReaderTests
{
    [Fact]
    public async Task Reads_multiple_bounded_requests_and_eof()
    {
        var reader = new BrokerRequestReader(new StringReader("{\"op\":\"ping\"}\r\n{}\n"));
        Assert.Equal("{\"op\":\"ping\"}", await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("{}", await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_oversized_and_truncated_requests()
    {
        var oversized = new BrokerRequestReader(new StringReader(new string('x', BrokerRequestReader.MaxRequestChars + 1) + "\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await oversized.ReadAsync(CancellationToken.None));
        var truncated = new BrokerRequestReader(new StringReader("{"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await truncated.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Accepts_limit_and_honors_cancellation()
    {
        var reader = new BrokerRequestReader(new StringReader(new string('x', BrokerRequestReader.MaxRequestChars) + "\n"));
        Assert.Equal(BrokerRequestReader.MaxRequestChars, (await reader.ReadAsync(CancellationToken.None))!.Length);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(cancelled.Token));
    }
}
