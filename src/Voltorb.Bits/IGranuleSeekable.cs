namespace Voltorb.Bits;

public delegate int PacketGranuleCountFunc(ref BitReader reader, bool isLastInPage);

public interface IPacketFramingDecoder
{
    
}

public interface IGranuleSeekable : IPacketFramingDecoder
{
    public ulong SeekTo(ulong granulePosition, int preRollPackets, PacketGranuleCountFunc granuleCountFunc);
    public ulong TotalGranules { get; }
}