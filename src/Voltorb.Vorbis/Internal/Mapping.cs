using CommunityToolkit.HighPerformance;
using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed class Mapping
{
    private readonly int[]     _couplingAngle;
    private readonly int[]     _couplingMangitude;
    private readonly IFloor[]  _submapFloor;
    private readonly Residue[] _submapResidue;
    private readonly IFloor[]  _channelFloor;
    private readonly Residue[] _channelResidue;

    public Mapping(ref BitReader packet, int channels, IFloor[] floors, Residue[] residues) {
        int submapCount = 1;
        if (packet.ReadBit()) {
            submapCount += (int)packet.ReadBits(4);
        }

        // square polar mapping
        int couplingSteps = 0;
        if (packet.ReadBit()) {
            couplingSteps = (int)packet.ReadBits(8) + 1;
        }

        int couplingBits = MathExtensions.ILog(channels - 1);
        _couplingAngle = new int[couplingSteps];
        _couplingMangitude = new int[couplingSteps];
        for (int j = 0; j < couplingSteps; j++) {
            int magnitude = (int)packet.ReadBits(couplingBits);
            int angle = (int)packet.ReadBits(couplingBits);
            if (magnitude == angle || magnitude > channels - 1 || angle > channels - 1) {
                throw new InvalidDataException("Invalid magnitude or angle in mapping header!");
            }

            _couplingAngle[j] = angle;
            _couplingMangitude[j] = magnitude;
        }

        if (0 != packet.ReadBits(2)) {
            throw new InvalidDataException("Reserved bits not 0 in mapping header.");
        }

        int[] mux = new int[channels];
        if (submapCount > 1) {
            for (int c = 0; c < channels; c++) {
                mux[c] = (int)packet.ReadBits(4);
                if (mux[c] > submapCount) {
                    throw new InvalidDataException("Invalid channel mux submap index in mapping header!");
                }
            }
        }

        _submapFloor = new IFloor[submapCount];
        _submapResidue = new Residue[submapCount];
        for (int j = 0; j < submapCount; j++) {
            packet.TryAdvance(8); // unused placeholder
            int floorNum = (int)packet.ReadBits(8);
            if (floorNum >= floors.Length) {
                throw new InvalidDataException("Invalid floor number in mapping header!");
            }

            int residueNum = (int)packet.ReadBits(8);
            if (residueNum >= residues.Length) {
                throw new InvalidDataException("Invalid residue number in mapping header!");
            }

            _submapFloor[j] = floors[floorNum];
            _submapResidue[j] = residues[residueNum];
        }

        _channelFloor = new IFloor[channels];
        _channelResidue = new Residue[channels];
        for (int c = 0; c < channels; c++) {
            _channelFloor[c] = _submapFloor[mux[c]];
            _channelResidue[c] = _submapResidue[mux[c]];
        }
    }

    public void DecodePacket(ref BitReader packet, int blockSize, Span2D<float> buffer) {
        int halfBlockSize = blockSize >> 1;

        // read the noise floor data
        IFloorData[] floorData = new IFloorData[_channelFloor.Length];
        Span<bool> noExecuteChannel = stackalloc bool[_channelFloor.Length];
        for (int i = 0; i < _channelFloor.Length; i++) {
            floorData[i] = _channelFloor[i].Unpack(ref packet, blockSize, i);
            noExecuteChannel[i] = !floorData[i].ExecuteChannel;

            // pre-clear the residue buffers
            buffer[i..i, ..halfBlockSize].Clear();
        }

        // make sure we handle no-energy channels correctly given the couplings..
        for (int i = 0; i < _couplingAngle.Length; i++) {
            if (floorData[_couplingAngle[i]].ExecuteChannel || floorData[_couplingMangitude[i]].ExecuteChannel) {
                floorData[_couplingAngle[i]].ForceEnergy = true;
                floorData[_couplingMangitude[i]].ForceEnergy = true;
            }
        }

        // decode the submaps into the residue buffer
        for (int i = 0; i < _submapFloor.Length; i++) {
            for (int j = 0; j < _channelFloor.Length; j++) {
                if (_submapFloor[i] != _channelFloor[j] || _submapResidue[i] != _channelResidue[j]) {
                    // the submap doesn't match, so this floor doesn't contribute
                    floorData[j].ForceNoEnergy = true;
                }
            }

            if (noExecuteChannel.Contains(false))
                _submapResidue[i].Decode(ref packet, blockSize, buffer);
        }

        // inverse coupling
        for (int i = _couplingAngle.Length - 1; i >= 0; i--) {
            if (floorData[_couplingAngle[i]].ExecuteChannel || floorData[_couplingMangitude[i]].ExecuteChannel) {
                Span<float> magnitude = buffer.GetRowSpan(_couplingMangitude[i]);
                Span<float> angle = buffer.GetRowSpan(_couplingAngle[i]);

                // we only have to do the first half; MDCT ignores the last half
                for (int j = 0; j < halfBlockSize; j++) {
                    float newM, newA;

                    float oldM = magnitude[j];
                    float oldA = angle[j];
                    if (oldM > 0) {
                        if (oldA > 0) {
                            newM = oldM;
                            newA = oldM - oldA;
                        } else {
                            newA = oldM;
                            newM = oldM + oldA;
                        }
                    } else {
                        if (oldA > 0) {
                            newM = oldM;
                            newA = oldM + oldA;
                        } else {
                            newA = oldM;
                            newM = oldM - oldA;
                        }
                    }

                    magnitude[j] = newM;
                    angle[j] = newA;
                }
            }
        }

        // apply floor / dot product / MDCT (only run if we have sound energy in that channel)
        for (int c = 0; c < _channelFloor.Length; c++) {
            if (floorData[c].ExecuteChannel) {
                _channelFloor[c].Apply(floorData[c], blockSize, buffer.GetRowSpan(c));
                Mdct.Reverse(buffer.GetRowSpan(c), blockSize);
            } else {
                // since we aren't doing the Mdct, we have to explicitly clear the back half of the block
                buffer.GetRowSpan(c).Slice(halfBlockSize, halfBlockSize).Clear();
            }
        }
    }
}