
using System.Collections.Frozen;
using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed class Floor0 : IFloor
{
    private readonly int                                 _order;
    private readonly int                                 _ampBits;
    private readonly int                                 _ampOfs;
    private readonly int                                 _ampDiv;
    private readonly Codebook[]                          _books;
    private readonly int                                 _bookBits;
    private readonly FrozenDictionary<int, int[]>        _barkMaps;
    private readonly FrozenDictionary<int, float[]> _wMap;

    private sealed class Data : IFloorData
    {
        internal readonly float[] Coeff;
        internal          float   Amp;

        public Data(float[] coeff, float amp) {
            Coeff = coeff;
            Amp = amp;
        }

        public bool ExecuteChannel => (ForceEnergy || Amp > 0f) && !ForceNoEnergy;

        public bool ForceEnergy { get; set; }
        public bool ForceNoEnergy { get; set; }
    }

    public Floor0(ref BitReader packet, ushort blockSize0, ushort blockSize1, Codebook[] codebooks) {
        // this is pretty well stolen directly from libvorbis...  BSD license
        _order = (int)packet.ReadBits(8);
        int rate = (int)packet.ReadBits(16);
        int barkMapSize = (int)packet.ReadBits(16);
        _ampBits = (int)packet.ReadBits(6);
        _ampOfs = (int)packet.ReadBits(8);

        Codebook[] books = new Codebook[(int)packet.ReadBits(4) + 1];

        if (_order < 1 || rate < 1 || barkMapSize < 1 || books.Length == 0) throw new InvalidDataException();
        _ampDiv = (1 << _ampBits) - 1;


        for (int i = 0; i < books.Length; i++) {
            int num = (int)packet.ReadBits(8);
            if (num < 0 || num >= codebooks.Length) throw new InvalidDataException();
            Codebook book = codebooks[num];

            if (book.MapType == 0 || book.Dimensions < 1) throw new InvalidDataException();

            books[i] = book;
        }

        _books = books;
        _bookBits = MathExtensions.ILog(_books.Length);

        _barkMaps = new Dictionary<int, int[]> {
            [blockSize0] = SynthesizeBarkCurve(blockSize0 / 2, rate, barkMapSize),
            [blockSize1] = SynthesizeBarkCurve(blockSize1 / 2, rate, barkMapSize)
        }.ToFrozenDictionary();

        _wMap = new Dictionary<int, float[]> {
            [blockSize0] = SynthesizeWDelMap(blockSize0 / 2, barkMapSize),
            [blockSize1] = SynthesizeWDelMap(blockSize1 / 2, barkMapSize)
        }.ToFrozenDictionary();


        static int[] SynthesizeBarkCurve(int n, int rate, int barkMapSize) {
            // ReSharper disable once PossibleLossOfFraction
            float scale = barkMapSize / ToBark(rate / 2);

            int[] map = new int[n + 1];

            for (int i = 0; i < n - 1; i++) {
                map[i] = Math.Min(barkMapSize - 1, (int)MathF.Floor(ToBark((rate / 2f) / n * i) * scale));
            }

            map[n] = -1;
            return map;
        }

        static float ToBark(double lsp) =>
            (float)(13.1 * Math.Atan(0.00074 * lsp) + 2.24 * Math.Atan(0.0000000185 * lsp * lsp) + .0001 * lsp);

        static float[] SynthesizeWDelMap(int n, int barkMapSize) {
            float wdel = MathF.PI / barkMapSize;

            float[] map = new float[n];
            for (int i = 0; i < n; i++) {
                map[i] = 2f * MathF.Cos(wdel * i);
            }

            return map;
        }
    }

    public IFloorData Unpack(ref BitReader packet, int blockSize, int channel) {
        Data data = new(new float[_order + 1], packet.ReadBits(_ampBits));

        if (data.Amp > 0f) {
            // this is pretty well stolen directly from libvorbis...  BSD license

            data.Amp = data.Amp / _ampDiv * _ampOfs;

            int bookNum = (int)packet.ReadBits(_bookBits);
            if (bookNum >= _books.Length) {
                // we ran out of data or the packet is corrupt...  0 the floor and return
                data.Amp = 0;
                return data;
            }

            Codebook book = _books[bookNum];

            // first, the book decode...
            for (int i = 0; i < _order;) {
                int entry = book.DecodeScalar(ref packet);
                if (entry == -1) {
                    // we ran out of data or the packet is corrupt...  0 the floor and return
                    data.Amp = 0;
                    return data;
                }

                for (int j = 0; i < _order && j < book.Dimensions; j++, i++) {
                    data.Coeff[i] = book.Lookup[entry, j];
                }
            }

            // then, the "averaging"
            float last = 0f;
            for (int j = 0; j < _order;) {
                for (int k = 0; j < _order && k < book.Dimensions; j++, k++) {
                    data.Coeff[j] += last;
                }

                last = data.Coeff[j - 1];
            }
        }

        return data;
    }

    public void Apply(IFloorData floorData, int blockSize, Span<float> residue) {
        if (floorData is not Data data) throw new ArgumentException("Incorrect packet data!");

        int n = blockSize / 2;

        if (data.Amp > 0f) {
            // this is pretty well stolen directly from libvorbis...  BSD license
            int[] barkMap = _barkMaps[blockSize];
            float[] wMap = _wMap[blockSize];

            int i = 0;
            for (; i < _order; i++) {
                data.Coeff[i] = 2f * MathF.Cos(data.Coeff[i]);
            }

            i = 0;
            while (i < n) {
                int j;
                int k = barkMap[i];
                float p = .5f;
                float q = .5f;
                float w = wMap[k];
                for (j = 1; j < _order; j += 2) {
                    q *= w - data.Coeff[j - 1];
                    p *= w - data.Coeff[j];
                }

                if (j == _order) {
                    // odd order filter; slightly asymmetric
                    q *= w - data.Coeff[j - 1];
                    p *= p * (4f - w * w);
                    q *= q;
                } else {
                    // even order filter; still symmetric
                    p *= p * (2f - w);
                    q *= q * (2f + w);
                }

                // calc the dB of this bark section
                q = data.Amp / MathF.Sqrt(p + q) - _ampOfs;

                // now convert to a linear sample multiplier
                q = MathF.Exp(q * 0.11512925f);

                residue[i] *= q;

                while (barkMap[++i] == k) residue[i] *= q;
            }
        } else {
            residue[..n].Clear();
        }
    }
}