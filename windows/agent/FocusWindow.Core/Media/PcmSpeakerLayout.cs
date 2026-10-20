namespace FocusWindow.Core.Media;

/// <summary>Exact Windows speaker positions, independent of hardware access.</summary>
public sealed record PcmSpeakerLayout(string Name, int Channels, int Mask)
{
    public static readonly PcmSpeakerLayout Stereo = new("stereo", 2, 0x3);
    public static readonly PcmSpeakerLayout FiveOneBack = new("5.1_back", 6, 0x3f);
    public static readonly PcmSpeakerLayout FiveOneSide = new("5.1_side", 6, 0x60f);
    public static readonly PcmSpeakerLayout SevenOne = new("7.1_surround", 8, 0x63f);

    public static PcmSpeakerLayout Select(int sourceMask, IReadOnlyList<string> receiverLayouts)
    {
        if (sourceMask == SevenOne.Mask && receiverLayouts.Contains(SevenOne.Name)) return SevenOne;
        if (sourceMask == FiveOneBack.Mask || sourceMask == FiveOneSide.Mask || sourceMask == SevenOne.Mask)
        {
            if (receiverLayouts.Contains(FiveOneBack.Name)) return FiveOneBack;
            if (receiverLayouts.Contains(FiveOneSide.Name)) return FiveOneSide;
        }
        return Stereo;
    }
}
