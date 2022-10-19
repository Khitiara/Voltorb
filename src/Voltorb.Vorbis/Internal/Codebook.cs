using System.Collections;
using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal sealed class Codebook
{
    // FastRange is "borrowed" from GitHub: TechnologicalPizza/MonoGame.NVorbis
    private sealed class FastRange : IReadOnlyList<int>
    {
        [ThreadStatic]
        private static FastRange? _cachedRange;

        internal static FastRange Get(int start, int count) {
            FastRange fr = _cachedRange ??= new FastRange();
            fr._start = start;
            fr.Count = count;
            return fr;
        }

        private int _start;

        private FastRange() { }

        public int this[int index] => index > Count
            ? throw new ArgumentOutOfRangeException(nameof(index), index, "")
            : _start + index;

        public int Count { get; private set; }

        public IEnumerator<int> GetEnumerator() => Enumerable.Range(_start, Count).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly float[]                        _lookupTable;
    private readonly IReadOnlyList<HuffmanListNode> _overflowList;
    private readonly List<HuffmanListNode?>         _prefixList;
    private readonly int                            _prefixBitLength;
    private readonly int                            _maxBits;

    public Codebook(ref BitReader packet, Huffman huffman) {
        // first, check the sync pattern
        ulong chkVal = packet.ReadBits(24);
        if (chkVal != 0x564342UL) throw new InvalidDataException("Book header had invalid signature!");

        // get the counts
        Dimensions = (int)packet.ReadBits(16);
        Entries = (int)packet.ReadBits(24);

        // init the storage
        int[] lengths = new int[Entries];

        bool sparse1;
        int total = 0;

        int maxLen;
        if (packet.ReadBit()) {
            // ordered
            int len1 = (int)packet.ReadBits(5) + 1;
            for (int i1 = 0; i1 < Entries;) {
                int cnt = (int)packet.ReadBits(MathExtensions.ILog(Entries - i1));

                while (--cnt >= 0) {
                    lengths[i1++] = len1;
                }

                ++len1;
            }

            total = 0;
            sparse1 = false;
            maxLen = len1;
        } else {
            // unordered
            maxLen = -1;
            sparse1 = packet.ReadBit();
            for (int i2 = 0; i2 < Entries; i2++) {
                if (!sparse1 || packet.ReadBit()) {
                    lengths[i2] = (int)packet.ReadBits(5) + 1;
                    ++total;
                } else {
                    // mark the entry as unused
                    lengths[i2] = -1;
                }

                if (lengths[i2] > maxLen) {
                    maxLen = lengths[i2];
                }
            }
        }

        // figure out the maximum bit size; if all are unused, don't do anything else
        if ((_maxBits = maxLen) > -1) {
            int[]? codewordLengths1 = null;
            if (sparse1 && total >= Entries >> 2) {
                codewordLengths1 = new int[Entries];
                Array.Copy(lengths, codewordLengths1, Entries);

                sparse1 = false;
            }

            // compute size of sorted tables
            int sortedCount = sparse1 ? total : 0;

            int[]? values1 = null;
            int[] codewords1 = null!;
            if (!sparse1) {
                codewords1 = new int[Entries];
            } else if (sortedCount != 0) {
                codewordLengths1 = new int[sortedCount];
                codewords1 = new int[sortedCount];
                values1 = new int[sortedCount];
            }

            if (!ComputeCodewords(sparse1, codewords1, codewordLengths1!, lengths, Entries, values1))
                throw new InvalidDataException();

            IReadOnlyList<int> valueList = (IReadOnlyList<int>?)values1 ?? FastRange.Get(0, codewords1.Length);

            huffman.GenerateTable(valueList, codewordLengths1 ?? lengths, codewords1);
            _prefixList = huffman.PrefixTree;
            _prefixBitLength = huffman.TableBits;
            _overflowList = huffman.OverflowList ?? new List<HuffmanListNode>();
        } else {
            _prefixList = new List<HuffmanListNode?>();
            _prefixBitLength = 0;
            _overflowList = new List<HuffmanListNode>();
        }

        MapType = (int)packet.ReadBits(4);
        if (MapType == 0) {
            _lookupTable = Array.Empty<float>();
        } else {
            float minValue1 = MathExtensions.ConvertFromVorbisFloat32((uint)packet.ReadBits(32));
            float deltaValue1 = MathExtensions.ConvertFromVorbisFloat32((uint)packet.ReadBits(32));
            int valueBits1 = (int)packet.ReadBits(4) + 1;
            bool sequenceP1 = packet.ReadBit();

            int lookupValueCount1 = Entries * Dimensions;
            float[] lookupTable1 = new float[lookupValueCount1];
            if (MapType == 1) {
                lookupValueCount1 = Lookup1Values();
            }

            uint[] multiplicands1 = new uint[lookupValueCount1];
            for (int i3 = 0; i3 < lookupValueCount1; i3++) {
                multiplicands1[i3] = (uint)packet.ReadBits(valueBits1);
            }

            // now that we have the initial data read in, calculate the entry tree
            if (MapType == 1) {
                for (int idx1 = 0; idx1 < Entries; idx1++) {
                    double last1 = 0.0;
                    int idxDiv1 = 1;
                    for (int i4 = 0; i4 < Dimensions; i4++) {
                        int moff1 = idx1 / idxDiv1 % lookupValueCount1;
                        double value1 = multiplicands1[moff1] * deltaValue1 + minValue1 + last1;
                        lookupTable1[idx1 * Dimensions + i4] = (float)value1;

                        if (sequenceP1) last1 = value1;

                        idxDiv1 *= lookupValueCount1;
                    }
                }
            } else {
                for (int idx2 = 0; idx2 < Entries; idx2++) {
                    double last2 = 0.0;
                    int moff2 = idx2 * Dimensions;
                    for (int i5 = 0; i5 < Dimensions; i5++) {
                        double value2 = multiplicands1[moff2] * deltaValue1 + minValue1 + last2;
                        lookupTable1[idx2 * Dimensions + i5] = (float)value2;

                        if (sequenceP1) last2 = value2;

                        ++moff2;
                    }
                }
            }

            _lookupTable = lookupTable1;
        }

        int Lookup1Values() {
            int r = (int)Math.Floor(Math.Exp(Math.Log(Entries) / Dimensions));

            if (Math.Floor(Math.Pow(r + 1, Dimensions)) <= Entries) ++r;

            return r;
        }

        bool ComputeCodewords(bool sparse, int[] codewords, int[] codewordLengths, int[] len, int n,
            int[]? values) {
            int i, k, m = 0;
            uint[] available = new uint[32];

            for (k = 0; k < n; ++k)
                if (len[k] > 0)
                    break;
            if (k == n) return true;

            AddEntry(sparse, codewords, codewordLengths, 0, k, m++, len[k], values);

            for (i = 1; i <= len[k]; ++i) available[i] = 1U << (32 - i);

            for (i = k + 1; i < n; ++i) {
                uint res;
                int z = len[i], y;
                if (z <= 0) continue;

                while (z > 0 && available[z] == 0) --z;
                if (z == 0) return false;
                res = available[z];
                available[z] = 0;
                AddEntry(sparse, codewords, codewordLengths, MathExtensions.BitReverse(res), i, m++, len[i], values);

                if (z != len[i]) {
                    for (y = len[i]; y > z; --y) {
                        available[y] = res + (1U << (32 - y));
                    }
                }
            }

            return true;
        }

        void AddEntry(bool sparse, int[] codewords, int[] codewordLengths, uint huffCode, int symbol, int count,
            int len, int[]? values) {
            if (sparse) {
                codewords[count] = (int)huffCode;
                codewordLengths[count] = len;
                values![count] = symbol;
            } else {
                codewords[symbol] = (int)huffCode;
            }
        }
    }

    public int DecodeScalar(ref BitReader packet) {
        int data = packet.PeekBits(_prefixBitLength, out ulong bitsRead);
        if (bitsRead == 0) return -1;

        // try to get the value from the prefix list...
        HuffmanListNode? node = _prefixList[data];
        if (node != null) {
            packet.TryAdvance(node.Length);
            return node.Value;
        }

        // nope, not possible... run through the overflow nodes
        data = packet.PeekBits(_maxBits, out _);

        foreach (HuffmanListNode t in _overflowList) {
            if (t.Bits == (data & t.Mask)) {
                packet.TryAdvance(t.Length);
                return t.Value;
            }
        }

        return -1;
    }

    public float this[int entry, int dim] => _lookupTable[entry * Dimensions + dim];

    public int Dimensions { get; }

    public int Entries { get; }

    public int MapType { get; }
}