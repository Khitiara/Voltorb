using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Voltorb.Bits;

public static class BitReaderExtensions
{
    /// <summary>
    /// Reads the specified number of bits from the stream and advances the read position.
    /// </summary>
    /// <param name="b">The BitReader to read bits from</param>
    /// <param name="count">The number of bits to read.</param>
    /// <returns>The value read. If not enough bits could be read, this will be a truncated value.</returns>
    public static ulong ReadBits(this ref BitReader b, int count) {
        if (b.ReadBits(count, out ulong bits) < count)
            throw new IOException();
        return bits;
    }

    public static T ReadNumeric<T>(this ref BitReader b) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> {
        int octets = Marshal.SizeOf<T>();
        ulong bits = b.ReadBits(octets * 8);
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(s, bits);
        return T.ReadLittleEndian(s[..octets], true);
    }

    /// <summary>
    /// Reads the specified number of bits from the stream and advances the read position.
    /// </summary>
    /// <param name="b">The BitReader to read bits from</param>
    /// <param name="count">The number of bits to read.</param>
    /// <param name="bits">The value read. If not enough bits could be read, this will be a truncated value.</param>
    /// <returns>The number of bits actually read, which may be less than <paramref name="count"/></returns>
    public static int ReadBits(this ref BitReader b, int count, out ulong bits) {
        switch (count) {
            case < 0 or > 64:
                throw new ArgumentOutOfRangeException(nameof(count), count, "");
            case 0:
                bits = 0UL;
                return 0;
        }

        int bitsRead = b.PeekBits(count, out bits);;
        b.TryAdvance(bitsRead);
        return bitsRead;
    }

    /// <summary>
    /// Reads a single bit from the stream and returns it, interpreted as a bool
    /// </summary>
    /// <param name="bitReader"></param>
    public static bool ReadBit(this ref BitReader bitReader) => bitReader.ReadBits(1) > 0;
}