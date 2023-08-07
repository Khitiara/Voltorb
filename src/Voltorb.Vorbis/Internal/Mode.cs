using CommunityToolkit.HighPerformance;
using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed class Mode
{
    private const float Pi2 = 3.1415926539f / 2;

    private readonly int     _channels;
    private readonly bool    _blockFlag;
    private readonly int     _block0Size;
    private readonly int     _block1Size;
    private readonly Mapping _mapping;

    public Mode(ref BitReader packet, int channels, int block0Size, int block1Size, Mapping[] mappings) {
        _channels = channels;
        _block0Size = block0Size;
        _block1Size = block1Size;

        _blockFlag = packet.ReadBit();
        if (0 != packet.ReadBits(32)) {
            throw new InvalidDataException("Mode header had invalid window or transform type!");
        }

        int mappingIdx = (int)packet.ReadBits(8);
        if (mappingIdx >= mappings.Length) {
            throw new InvalidDataException("Mode header had invalid mapping index!");
        }

        _mapping = mappings[mappingIdx];

        if (_blockFlag) {
            Windows = new[] {
                new float[_block1Size],
                new float[_block1Size],
                new float[_block1Size],
                new float[_block1Size],
            };
        } else {
            Windows = new[] {
                new float[_block0Size],
            };
        }

        CalcWindows();
    }

    private void CalcWindows() {
        // 0: prev = s, next = s || BlockFlag = false
        // 1: prev = l, next = s
        // 2: prev = s, next = l
        // 3: prev = l, next = l

        for (int idx = 0; idx < Windows.Length; idx++) {
            float[] array = Windows[idx];

            int left = ((idx & 1) == 0 ? _block0Size : _block1Size) / 2;
            int wnd = BlockSize;
            int right = ((idx & 2) == 0 ? _block0Size : _block1Size) / 2;

            int leftbegin = wnd / 4 - left / 2;
            int rightbegin = wnd - wnd / 4 - right / 2;

            for (int i = 0; i < left; i++) {
                float x = (float)Math.Sin((i + .5) / left * Pi2);
                x *= x;
                array[leftbegin + i] = (float)Math.Sin(x * Pi2);
            }

            for (int i = leftbegin + left; i < rightbegin; i++) {
                array[i] = 1.0f;
            }

            for (int i = 0; i < right; i++) {
                float x = (float)Math.Sin((right - i - .5) / right * Pi2);
                x *= x;
                array[rightbegin + i] = (float)Math.Sin(x * Pi2);
            }
        }
    }

    private bool GetPacketInfo(ref BitReader packet, bool isLastInPage, out int blockSize, out int windowIndex,
        out int packetStartIndex, out int packetValidLength, out int packetTotalLength) {
        int leftOverlapHalfSize;
        bool prevFlag, nextFlag;
        if (_blockFlag) {
            blockSize = _block1Size;
            prevFlag = packet.ReadBit();
            nextFlag = packet.ReadBit();
        } else {
            blockSize = _block0Size;
            prevFlag = nextFlag = false;
        }

        int rightOverlapHalfSize = (nextFlag ? _block1Size : _block0Size) / 4;

        windowIndex = (prevFlag ? 1 : 0) + (nextFlag ? 2 : 0);
        leftOverlapHalfSize = (prevFlag ? _block1Size : _block0Size) / 4;
        packetStartIndex = blockSize / 4 - leftOverlapHalfSize;
        packetTotalLength = blockSize / 4 * 3 + rightOverlapHalfSize;
        packetValidLength = packetTotalLength - rightOverlapHalfSize * 2;

        if (isLastInPage && _blockFlag && !nextFlag) {
            // this fixes a bug in certain libvorbis versions where a long->short that crosses a page boundary doesn't
            // get counted correctly in the first page's granulePos
            packetValidLength -= _block1Size / 4 - _block0Size / 4;
        }

        return true;
    }

    public bool Decode(ref BitReader packet, Span2D<float> buffer, out int packetStartindex,
        out int packetValidLength, out int packetTotalLength) {
        if (!GetPacketInfo(ref packet, false, out int blockSize, out int windowIndex, out packetStartindex,
                out packetValidLength, out packetTotalLength)) return false;

        _mapping.DecodePacket(ref packet, blockSize, buffer);

        float[] window = Windows[windowIndex];
        for (int i = 0; i < blockSize; i++) {
            for (int ch = 0; ch < _channels; ch++) {
                buffer[ch,i] *= window[i];
            }
        }

        return true;
    }

    public int GetPacketSampleCount(ref BitReader packet, bool isLastInPage) {
        GetPacketInfo(ref packet, isLastInPage, out _, out _, out int packetStartIndex, out int packetValidLength,
            out _);
        packet.Seek(0, SeekOrigin.Begin);
        return packetValidLength - packetStartIndex;
    }

    public int BlockSize => _blockFlag ? _block1Size : _block0Size;

    public float[][] Windows { get; }
}