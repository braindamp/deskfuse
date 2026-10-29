using System.Buffers.Binary;

namespace DeskFuse.AudioBroker;

internal sealed record EnrollmentChoice(Guid Id, byte[] KeySha256, BrokerProofScope[] Scopes);
internal sealed record EnrollmentSetupSelection(EnrollmentAction Action, Guid OldEnrollment, BrokerProofScope[] Scopes)
{
    internal byte[] Encode()
    {
        RequireScopes(Scopes);
        if (!Enum.IsDefined(Action) || (Action == EnrollmentAction.New ? OldEnrollment != Guid.Empty : OldEnrollment == Guid.Empty))
            throw new InvalidDataException("Invalid local setup action.");
        var bytes = new byte[18 + Scopes.Length]; bytes[0] = (byte)Action;
        OldEnrollment.ToByteArray(true).CopyTo(bytes, 1); bytes[17] = (byte)Scopes.Length;
        for (var i = 0; i < Scopes.Length; i++) bytes[18 + i] = (byte)Scopes[i];
        return bytes;
    }
    internal static EnrollmentSetupSelection Parse(byte[] bytes)
    {
        if (bytes.Length is < 19 or > 26 || bytes.Length != 18 + bytes[17]) throw new InvalidDataException("Invalid local selection size.");
        var value = new EnrollmentSetupSelection((EnrollmentAction)bytes[0], new Guid(bytes.AsSpan(1, 16), true),
            bytes.AsSpan(18).ToArray().Select(b => (BrokerProofScope)b).ToArray());
        if (!value.Encode().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical local selection.");
        return value;
    }
    internal static byte[] ChoicePage(EnrollmentChoice[] choices, BrokerProofScope[] maximumScopes, bool last)
    {
        RequireScopes(maximumScopes);
        if (choices.Length > 64) throw new InvalidDataException("Setup choice page exceeds its bound.");
        using var bytes = new MemoryStream(); bytes.WriteByte(last ? (byte)1 : (byte)0);
        bytes.WriteByte((byte)maximumScopes.Length); foreach (var scope in maximumScopes) bytes.WriteByte((byte)scope);
        Span<byte> count = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)choices.Length); bytes.Write(count);
        foreach (var choice in choices)
        {
            RequireScopes(choice.Scopes);
            if (choice.Id == Guid.Empty || choice.KeySha256.Length != 32) throw new InvalidDataException("Invalid protected choice.");
            bytes.Write(choice.Id.ToByteArray(true)); bytes.Write(choice.KeySha256);
            bytes.WriteByte((byte)choice.Scopes.Length); foreach (var scope in choice.Scopes) bytes.WriteByte((byte)scope);
        }
        return bytes.ToArray();
    }
    internal static (EnrollmentChoice[] Choices, BrokerProofScope[] Maximum, bool Last) ParseChoicePage(byte[] bytes)
    {
        if (bytes.Length is < 5 or > 4096 || bytes[0] > 1) throw new InvalidDataException("Invalid setup choice page.");
        var at = 1;
        BrokerProofScope[] Scopes()
        {
            if (at >= bytes.Length || bytes[at] is < 1 or > 8 || at + 1 + bytes[at] > bytes.Length) throw new InvalidDataException("Invalid scopes.");
            var count = bytes[at++]; var result = bytes.AsSpan(at, count).ToArray().Select(b => (BrokerProofScope)b).ToArray();
            at += count; RequireScopes(result); return result;
        }
        var maximum = Scopes();
        if (at + 2 > bytes.Length) throw new InvalidDataException("Missing setup choices.");
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at)); at += 2;
        if (count > 64) throw new InvalidDataException("Too many choices.");
        var choices = new EnrollmentChoice[count];
        for (var i = 0; i < count; i++)
        {
            if (at + 49 > bytes.Length) throw new InvalidDataException("Truncated setup choice.");
            var id = new Guid(bytes.AsSpan(at, 16), true); at += 16;
            var hash = bytes.AsSpan(at, 32).ToArray(); at += 32;
            choices[i] = new(id, hash, Scopes());
        }
        if (at != bytes.Length || !ChoicePage(choices, maximum, bytes[0] == 1).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Noncanonical setup choice page.");
        return (choices, maximum, bytes[0] == 1);
    }
    private static void RequireScopes(BrokerProofScope[] scopes)
    {
        if (scopes.Length is < 1 or > 8 || scopes.Any(s => !Enum.IsDefined(s)) || !scopes.SequenceEqual(scopes.OrderBy(s => s).Distinct()))
            throw new InvalidDataException("Invalid ascending unique setup scopes.");
    }
}
