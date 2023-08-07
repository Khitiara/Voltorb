using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed partial class Floor1 : IFloor
{
    private sealed class Data : IFloorData
    {
        internal readonly int[] Posts = new int[64];
        internal          int   PostCount;

        public bool ExecuteChannel => (ForceEnergy || PostCount > 0) && !ForceNoEnergy;

        public bool ForceEnergy { get; set; }
        public bool ForceNoEnergy { get; set; }
    }

    private readonly int[] _partitionClass;

    private readonly int[] _classDimensions;

    private readonly int[] _classSubclasses;

    private readonly int[] _xList;

    private readonly int[] _hNeigh;

    private readonly int[] _lNeigh;

    private readonly int[] _sortIdx;

    private readonly int           _multiplier;
    private readonly int           _range;
    private readonly int           _yBits;
    private readonly Codebook?[]   _classMasterbooks;
    private readonly Codebook?[][] _subclassBooks;

    // these get compiled to .data, no allocation actually occurs
    private static ReadOnlySpan<int> RangeLookup => new[]{ 256, 128, 86, 64, };
    private static ReadOnlySpan<int> YBitsLookup => new[] { 8, 7, 7, 6, };

    public Floor1(ref BitReader packet, Codebook[] codebooks) {
        int maximumClass = -1;
        _partitionClass = new int[(int)packet.ReadBits(5)];
        for (int i = 0; i < _partitionClass.Length; i++) {
            _partitionClass[i] = (int)packet.ReadBits(4);
            if (_partitionClass[i] > maximumClass) {
                maximumClass = _partitionClass[i];
            }
        }

        _classDimensions = new int[++maximumClass];
        _classSubclasses = new int[maximumClass];
        _classMasterbooks = new Codebook?[maximumClass];
        Span<int> classMasterBookIndex = stackalloc int[maximumClass];
        _subclassBooks = new Codebook?[maximumClass][];
        for (int i = 0; i < maximumClass; i++) {
            _classDimensions[i] = (int)packet.ReadBits(3) + 1;
            _classSubclasses[i] = (int)packet.ReadBits(2);
            if (_classSubclasses[i] > 0) {
                classMasterBookIndex[i] = (int)packet.ReadBits(8);
                _classMasterbooks[i] = codebooks[classMasterBookIndex[i]];
            }

            _subclassBooks[i] = new Codebook[1 << _classSubclasses[i]];
            for (int j = 0; j < _subclassBooks[i].Length; j++) {
                int bookNum = (int)packet.ReadBits(8) - 1;
                if (bookNum >= 0) _subclassBooks[i][j] = codebooks[bookNum];
            }
        }

        _multiplier = (int)packet.ReadBits(2);

        _range = RangeLookup[_multiplier];
        _yBits = YBitsLookup[_multiplier];

        ++_multiplier;

        int rangeBits = (int)packet.ReadBits(4);

        List<int> xList = new() {
            0,
            1 << rangeBits,
        };

        foreach (int classNum in _partitionClass) {
            for (int j = 0; j < _classDimensions[classNum]; j++) {
                xList.Add((int)packet.ReadBits(rangeBits));
            }
        }

        _xList = xList.ToArray();

        // precalc the low and high neighbors (and init the sort table)
        _lNeigh = new int[xList.Count];
        _hNeigh = new int[xList.Count];
        _sortIdx = new int[xList.Count];
        _sortIdx[0] = 0;
        _sortIdx[1] = 1;
        for (int i = 2; i < _lNeigh.Length; i++) {
            _lNeigh[i] = 0;
            _hNeigh[i] = 1;
            _sortIdx[i] = i;
            for (int j = 2; j < i; j++) {
                int temp = _xList[j];
                if (temp < _xList[i]) {
                    if (temp > _xList[_lNeigh[i]]) _lNeigh[i] = j;
                } else {
                    if (temp < _xList[_hNeigh[i]]) _hNeigh[i] = j;
                }
            }
        }

        // precalc the sort table
        for (int i = 0; i < _sortIdx.Length - 1; i++) {
            for (int j = i + 1; j < _sortIdx.Length; j++) {
                if (_xList[i] == _xList[j]) throw new InvalidDataException();

                if (_xList[_sortIdx[i]] > _xList[_sortIdx[j]]) {
                    // swap the sort indexes
                    (_sortIdx[i], _sortIdx[j]) = (_sortIdx[j], _sortIdx[i]);
                }
            }
        }
    }

    public IFloorData Unpack(ref BitReader packet, int blockSize, int channel) {
        Data data = new();

        // hoist ReadPosts to here since that's all we're doing...
        if (packet.ReadBit()) {
            int postCount = 2;
            data.Posts[0] = (int)packet.ReadBits(_yBits);
            data.Posts[1] = (int)packet.ReadBits(_yBits);

            for (int i = 0; i < _partitionClass.Length; i++) {
                int clsNum = _partitionClass[i];
                int cdim = _classDimensions[clsNum];
                int cbits = _classSubclasses[clsNum];
                int csub = (1 << cbits) - 1;
                uint cval = 0U;
                if (cbits > 0) {
                    if ((cval = (uint)_classMasterbooks[clsNum]!.DecodeScalar(ref packet)) == uint.MaxValue) {
                        // we read a bad value...  bail
                        postCount = 0;
                        break;
                    }
                }

                for (int j = 0; j < cdim; j++) {
                    Codebook? book = _subclassBooks[clsNum][cval & csub];
                    cval >>= cbits;
                    if (book != null) {
                        if ((data.Posts[postCount] = book.DecodeScalar(ref packet)) == -1) {
                            // we read a bad value... bail
                            postCount = 0;
                            i = _partitionClass.Length;
                            break;
                        }
                    }

                    ++postCount;
                }
            }

            data.PostCount = postCount;
        }

        return data;
    }

    public void Apply(IFloorData floorData, int blockSize, Span<float> residue) {
        if (floorData is not Data data) throw new ArgumentException("Incorrect packet data!", nameof(floorData));

        int n = blockSize / 2;

        if (data.PostCount > 0) {
            Span<bool> stepFlags = stackalloc bool[64];
            UnwrapPosts(data, stepFlags);

            int lx = 0;
            int ly = data.Posts[0] * _multiplier;
            for (int i = 1; i < data.PostCount; i++) {
                int idx = _sortIdx[i];

                if (stepFlags[idx]) {
                    int hx = _xList[idx];
                    int hy = data.Posts[idx] * _multiplier;
                    if (lx < n) RenderLineMulti(lx, ly, Math.Min(hx, n), hy, residue);
                    lx = hx;
                    ly = hy;
                }

                if (lx >= n) break;
            }

            if (lx < n) {
                RenderLineMulti(lx, ly, n, ly, residue);
            }
        } else {
            residue[..n].Clear();
        }

        void UnwrapPosts(Data d, Span<bool> stepFlags) {
            stepFlags[0] = true;
            stepFlags[1] = true;

            Span<int> finalY = stackalloc int[64];
            finalY[0] = d.Posts[0];
            finalY[1] = d.Posts[1];

            for (int i = 2; i < d.PostCount; i++) {
                int lowOfs = _lNeigh[i];
                int highOfs = _hNeigh[i];

                int predicted = RenderPoint(_xList[lowOfs], finalY[lowOfs], _xList[highOfs], finalY[highOfs],
                    _xList[i]);

                int val = d.Posts[i];
                int highroom = _range - predicted;
                int lowroom = predicted;
                int room;
                if (highroom < lowroom) {
                    room = highroom * 2;
                } else {
                    room = lowroom * 2;
                }

                if (val != 0) {
                    stepFlags[lowOfs] = true;
                    stepFlags[highOfs] = true;
                    stepFlags[i] = true;

                    if (val >= room) {
                        if (highroom > lowroom) {
                            finalY[i] = val - lowroom + predicted;
                        } else {
                            finalY[i] = predicted - val + highroom - 1;
                        }
                    } else {
                        if (val % 2 == 1) {
                            // odd
                            finalY[i] = predicted - ((val + 1) / 2);
                        } else {
                            // even
                            finalY[i] = predicted + (val / 2);
                        }
                    }
                } else {
                    stepFlags[i] = false;
                    finalY[i] = predicted;
                }
            }

            finalY[..d.PostCount].CopyTo(d.Posts);
        }

        int RenderPoint(int x0, int y0, int x1, int y1, int x) {
            int dy = y1 - y0;
            int adx = x1 - x0;
            int ady = Math.Abs(dy);
            int err = ady * (x - x0);
            int off = err / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        void RenderLineMulti(int x0, int y0, int x1, int y1, Span<float> v) {
            int dy = y1 - y0;
            int adx = x1 - x0;
            int ady = Math.Abs(dy);
            int sy = dy < 0 ? -1 : 1;
            int b = dy / adx;
            int y = y0;
            int err = -adx;

            v[x0] *= InverseDbTable[y0];
            ady -= Math.Abs(b) * adx;

            for (int x = x0; ++x < x1;) {
                y += b;
                err += ady;
                if (err >= 0) {
                    err -= adx;
                    y += sy;
                }

                v[x] *= InverseDbTable[y];
            }
        }
    }
}