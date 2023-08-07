using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal interface IFloor
{
    IFloorData Unpack(ref BitReader packet, int blockSize, int channel);

    void Apply(IFloorData floorData, int blockSize, Span<float> residue);
}