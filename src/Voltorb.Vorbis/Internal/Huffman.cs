namespace Voltorb.Vorbis.Internal;

internal sealed class Huffman :  IComparer<HuffmanListNode>
{
    private const int MaxTableBits = 10;

    public int TableBits { get; private set; }
    public List<HuffmanListNode?> PrefixTree { get; private set; }
    public IReadOnlyList<HuffmanListNode>? OverflowList { get; private set; }

    public void GenerateTable(IReadOnlyList<int> values, int[] lengthList, int[] codeList)
    {
        HuffmanListNode[] list = new HuffmanListNode[lengthList.Length];

        int maxLen = 0;
        for (int i = 0; i < list.Length; i++)
        {
            list[i] = new HuffmanListNode
            {
                Value = values[i],
                Length = lengthList[i] <= 0 ? 99999 : lengthList[i],
                Bits = codeList[i],
                Mask = (1 << lengthList[i]) - 1,
            };
            if (lengthList[i] > 0 && maxLen < lengthList[i])
            {
                maxLen = lengthList[i];
            }
        }

        Array.Sort(list, 0, list.Length, this);

        int tableBits = maxLen > MaxTableBits ? MaxTableBits : maxLen;

        List<HuffmanListNode?> prefixList = new(1 << tableBits);
        List<HuffmanListNode>? overflowList = null;
        for (int i = 0; i < list.Length && list[i].Length < 99999; i++)
        {
            int itemBits = list[i].Length;
            if (itemBits > tableBits)
            {
                overflowList = new List<HuffmanListNode>(list.Length - i);
                for (; i < list.Length && list[i].Length < 99999; i++)
                {
                    overflowList.Add(list[i]);
                }
            }
            else
            {
                int maxVal = 1 << (tableBits - itemBits);
                HuffmanListNode item = list[i];
                for (int j = 0; j < maxVal; j++)
                {
                    int idx = (j << itemBits) | item.Bits;
                    while (prefixList.Count <= idx)
                    {
                        prefixList.Add(null);
                    }
                    prefixList[idx] = item;
                }
            }
        }

        while (prefixList.Count < 1 << tableBits)
        {
            prefixList.Add(null);
        }

        TableBits = tableBits;
        PrefixTree = prefixList;
        OverflowList = overflowList;
    }

    int IComparer<HuffmanListNode>.Compare(HuffmanListNode? x, HuffmanListNode? y) {
        if (x is null || y is null) return x == y ? 0 : x is null ? -1 : 1;
        int len = x.Length - y.Length;
        if (len == 0)
        {
            return x.Bits - y.Bits;
        }
        return len;
    }
}