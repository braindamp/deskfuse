using System.Threading.Channels;
using Xunit;

namespace FocusWindow.Tests;

/// <summary>
/// Locks down the .NET 8 diagnostic contract used by the 16-packet speaker
/// route and eight-packet encrypted audio queues. A successful TryWrite into
/// DropOldest DOES NOT guarantee lossless PCM.
/// </summary>
public sealed class AudioDropTelemetryTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void Bounded_drop_oldest_reports_real_evictions_while_accepting_new_items(int capacity)
    {
        long evicted = 0;
        var queue = Channel.CreateBounded<int>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            itemDropped: _ => Interlocked.Increment(ref evicted));

        for (var i = 0; i < capacity + 3; i++)
            Assert.True(queue.Writer.TryWrite(i));

        Assert.Equal(3, Interlocked.Read(ref evicted));
        for (var i = 3; i < capacity + 3; i++)
        {
            Assert.True(queue.Reader.TryRead(out var value));
            Assert.Equal(i, value);
        }
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public void Uncontended_audio_channel_does_not_report_loss()
    {
        long evicted = 0;
        var queue = Channel.CreateBounded<int>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            itemDropped: _ => Interlocked.Increment(ref evicted));
        for (var i = 0; i < 100; i++)
        {
            Assert.True(queue.Writer.TryWrite(i));
            Assert.True(queue.Reader.TryRead(out var value));
            Assert.Equal(i, value);
        }
        Assert.Equal(0, Interlocked.Read(ref evicted));
    }
}
