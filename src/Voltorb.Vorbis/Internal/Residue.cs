using System.Numerics;
using CommunityToolkit.HighPerformance;
using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed class Residue
{
    private readonly int _channels2;

    private enum ResidueType
    {
        Type0,
        Type1,
        Type2,
    }

    private readonly ResidueType   _type;
    private readonly int           _begin;
    private readonly int           _end;
    private readonly int           _partitionSize;
    private readonly Codebook      _classBook;
    private readonly int[]         _cascade;
    private readonly Codebook?[][] _books;
    private readonly int           _maxStages;
    private readonly int[][]       _decodeMap;
    private readonly int           _channels;

    public Residue(ref BitReader packet, int channels, Codebook[] codebooks) {
        _channels2 = channels;
        _type = (ResidueType)packet.ReadNumeric<ushort>();
        if (_type == ResidueType.Type2)
            channels = 1;

        // this is pretty well stolen directly from libvorbis...  BSD license
        _begin = (int)packet.ReadBits(24);
        _end = (int)packet.ReadBits(24);
        _partitionSize = (int)packet.ReadBits(24) + 1;
        int classifications = (int)packet.ReadBits(6) + 1;
        _classBook = codebooks[(int)packet.ReadBits(8)];

        _cascade = new int[classifications];
        int acc = 0;
        for (int i = 0; i < classifications; i++) {
            int lowBits = (int)packet.ReadBits(3);
            if (packet.ReadBit()) {
                _cascade[i] = (int)packet.ReadBits(5) << 3 | lowBits;
            } else {
                _cascade[i] = lowBits;
            }

            acc += BitOperations.PopCount((uint)_cascade[i]);
        }

        Span<int> bookNums = stackalloc int[acc];
        for (int i = 0; i < acc; i++) {
            bookNums[i] = (int)packet.ReadBits(8);
            if (codebooks[bookNums[i]].MapType == 0) throw new InvalidDataException();
        }

        int entries = _classBook.Entries;
        int dim = _classBook.Dimensions;
        int partvals = 1;
        while (dim > 0) {
            partvals *= classifications;
            if (partvals > entries) throw new InvalidDataException();
            --dim;
        }

        // now the lookups
        _books = new Codebook[classifications][];

        acc = 0;
        int maxstage = 0;
        int stages;
        for (int j = 0; j < classifications; j++) {
            stages = MathExtensions.ILog(_cascade[j]);
            _books[j] = new Codebook[stages];
            if (stages > 0) {
                maxstage = Math.Max(maxstage, stages);
                for (int k = 0; k < stages; k++) {
                    if ((_cascade[j] & (1 << k)) > 0) {
                        _books[j][k] = codebooks[bookNums[acc++]];
                    }
                }
            }
        }

        _maxStages = maxstage;

        _decodeMap = new int[partvals][];
        for (int j = 0; j < partvals; j++) {
            int val = j;
            int mult = partvals / classifications;
            _decodeMap[j] = new int[_classBook.Dimensions];
            for (int k = 0; k < _classBook.Dimensions; k++) {
                int deco = val / mult;
                val -= deco * mult;
                mult /= classifications;
                _decodeMap[j][k] = deco;
            }
        }

        _channels = channels;
    }

    public void Decode(ref BitReader packet, int blockSize, Span2D<float> buffer) {
        if (_type == ResidueType.Type2)
            blockSize *= _channels2;
        // this is pretty well stolen directly from libvorbis...  BSD license
        int end = _end < blockSize / 2 ? _end : blockSize / 2;
        int n = end - _begin;

        if (n <= 0) return;
        int partitionCount = n / _partitionSize;

        int partitionWords = (partitionCount + _classBook.Dimensions - 1) / _classBook.Dimensions;
        int[,][] partWordCache = new int[_channels, partitionWords][];

        for (int stage = 0; stage < _maxStages; stage++) {
            for (int partitionIdx = 0, entryIdx = 0; partitionIdx < partitionCount; entryIdx++) {
                if (stage == 0) {
                    for (int ch = 0; ch < _channels; ch++) {
                        int idx = _classBook.DecodeScalar(ref packet);
                        if (idx >= 0 && idx < _decodeMap.Length) {
                            partWordCache[ch, entryIdx] = _decodeMap[idx];
                        } else {
                            partitionIdx = partitionCount;
                            stage = _maxStages;
                            break;
                        }
                    }
                }

                for (int dimensionIdx = 0;
                     partitionIdx < partitionCount && dimensionIdx < _classBook.Dimensions;
                     dimensionIdx++, partitionIdx++) {
                    int offset = _begin + partitionIdx * _partitionSize;
                    for (int ch = 0; ch < _channels; ch++) {
                        int idx = partWordCache[ch, entryIdx][dimensionIdx];
                        if ((_cascade[idx] & (1 << stage)) != 0) {
                            Codebook? book = _books[idx][stage];
                            if (book != null && WriteVectors(book, ref packet, buffer, ch, offset, _partitionSize)) {
                                // bad packet...  exit now and try to use what we already have
                                partitionIdx = partitionCount;
                                stage = _maxStages;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    private bool WriteVectors(Codebook book, ref BitReader packet, Span2D<float> residue, int ch, int offset,
        int partitionSize) => _type switch {
        ResidueType.Type0 => WriteVectors0(book, ref packet, residue, ch, offset, partitionSize),
        ResidueType.Type1 => WriteVectors1(book, ref packet, residue, ch, offset, partitionSize),
        ResidueType.Type2 => WriteVectors2(book, ref packet, residue, offset, partitionSize),
        _ => throw new InvalidOperationException(),
    };

    private static bool WriteVectors0(Codebook codebook, ref BitReader packet, Span2D<float> residue, int channel,
        int offset,
        int partitionSize) {
        Span<float> res = residue.GetRowSpan(channel);
        int steps = partitionSize / codebook.Dimensions;
        Span<int> entryCache = stackalloc int[steps];

        for (int i = 0; i < steps; i++) {
            if ((entryCache[i] = codebook.DecodeScalar(ref packet)) == -1) {
                return true;
            }
        }

        for (int dim = 0; dim < codebook.Dimensions; dim++) {
            for (int step = 0; step < steps; step++, offset++) {
                res[offset] += codebook.Lookup[entryCache[step], dim];
            }
        }

        return false;
    }

    private static bool WriteVectors1(Codebook codebook, ref BitReader packet, Span2D<float> residue, int channel,
        int offset,
        int partitionSize) {
        Span<float> res = residue.GetRowSpan(channel);

        for (int i = 0; i < partitionSize / codebook.Dimensions; i++) {
            int entry = codebook.DecodeScalar(ref packet);
            if (entry == -1) {
                return true;
            }

            res.Slice(offset, codebook.Dimensions).Add(codebook.Lookup.GetRowSpan(entry));
        }

        return false;
    }

    private bool WriteVectors2(Codebook codebook, ref BitReader packet, Span2D<float> residue, int offset,
        int partitionSize) {
        int chPtr = 0;

        offset /= _channels;
        for (int c = 0; c < partitionSize;) {
            int entry = codebook.DecodeScalar(ref packet);
            if (entry == -1) {
                return true;
            }

            for (int d = 0; d < codebook.Dimensions; d++, c++) {
                residue[chPtr, offset] += codebook.Lookup[entry, d];
                if (++chPtr == _channels) {
                    chPtr = 0;
                    offset++;
                }
            }
        }

        return false;
    }
}