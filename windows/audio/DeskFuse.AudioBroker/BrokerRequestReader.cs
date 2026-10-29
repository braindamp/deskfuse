using System.Text;

namespace DeskFuse.AudioBroker;

/// <summary>Bounds untrusted control input before allocating a JSON request.</summary>
internal sealed class BrokerRequestReader(TextReader reader)
{
    internal const int MaxRequestChars = 4096;
    private readonly char[] buffer = new char[512];
    private int offset, count;

    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == count)
            {
                count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (count == 0)
                    return line.Length == 0 ? null : throw new InvalidDataException("Unterminated broker request.");
            }
            var c = buffer[offset++];
            if (c == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
            if (line.Length == MaxRequestChars) throw new InvalidDataException("Broker request exceeds the size limit.");
            line.Append(c);
        }
    }
}
