using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioWorkerControlTests
{
    [Fact]
    public async Task Bootstrap_matches_shared_Rust_fixture_and_handles_fragmented_control()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-worker-control-v1.json")));
        var expected = Convert.FromHexString(fixture.RootElement.GetProperty("helloHex").GetString()!);
        var hello = AudioWorkerControl.Hello(Guid.ParseExact("00112233445566778899aabbccddeeff", "N"), Enumerable.Repeat((byte)3, 32).ToArray(), 17, 31);
        Assert.Equal(expected, hello);
        var channel = new Channel(); var output = new AudioWorkerControl(channel);
        await output.WriteAsync(AudioWorkerFrameKind.Hello, hello, CancellationToken.None);
        channel.Input = channel.Output.ToArray();
        var frame = await new AudioWorkerControl(channel).ReadAsync(CancellationToken.None);
        Assert.Equal(AudioWorkerFrameKind.Hello, frame.Kind); Assert.Equal(hello, frame.Body);
        Assert.Throws<InvalidDataException>(() => AudioWorkerControl.Hello(Guid.Empty, new byte[32], 0, 0));
    }

    [Fact]
    public async Task Malformed_or_closed_control_is_terminal_and_cannot_reconnect()
    {
        foreach (var input in new byte[][] { [0, 0, 0, 0], [0, 0, 0x20, 1], [0, 0, 0, 1, 255], [0, 0, 0, 2, 1] })
        {
            var channel = new Channel { Input = input }; var frames = new AudioWorkerControl(channel);
            var failure = await Record.ExceptionAsync(async () => await frames.ReadAsync(CancellationToken.None));
            Assert.True(failure is IOException or InvalidDataException);
            channel.Input = [0, 0, 0, 1, 3]; channel.Position = 0;
            await Assert.ThrowsAsync<IOException>(async () => await frames.ReadAsync(CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(async () => await frames.WriteAsync(AudioWorkerFrameKind.Stop, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        }
    }

    private sealed class Channel : IEnrollmentChannel
    {
        internal byte[] Input = []; internal int Position; internal MemoryStream Output = new();
        public ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var length = Math.Min(1, Math.Min(bytes.Length, Input.Length - Position));
            Input.AsSpan(Position, length).CopyTo(bytes.Span); Position += length; return ValueTask.FromResult(length);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => Output.WriteAsync(bytes, ct);
    }
}
