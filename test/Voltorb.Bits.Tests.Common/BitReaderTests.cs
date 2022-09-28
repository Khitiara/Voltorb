using FluentAssertions;

namespace Voltorb.Bits.Tests.Common;

public static class BitReaderTests
{
    [Theory]
    [InlineData(new byte[] { 0b11111010, 0x23, 0x34, }, 5, 0b11010)]
    public static async ValueTask TestReadSimple(byte[] data, int count, ulong expected) {
        MemoryStream stream = new(data);
        BitReader bitReader = new(stream);
        (await bitReader.ReadBitsAsync(count)).Should().Be(expected);
    }

    [Fact]
    public static async ValueTask TestPeek() {
        MemoryStream stream = new(new byte[] { 0b11111010, 0x23, 0x34, });
        BitReader bitReader = new(stream);
        (int count, ulong bits) = await bitReader.PeekBitsAsync(5);
        count.Should().Be(5);
        bits.Should().Be(0b11010);
        (count, ulong bits2) = await bitReader.PeekBitsAsync(8);
        count.Should().Be(8);
        bits.Should().Be(0b11111010);
        (bits2 & 0b11111).Should().Be(bits);
    }

    [Fact]
    public static async ValueTask TestPeekAdvance() {
        MemoryStream stream = new(new byte[] { 0b11111010, 0x23, 0x34, });
        BitReader bitReader = new(stream);

        (await bitReader.AdvanceAsync(11)).Should().BeTrue();
        (await bitReader.ReadBitsAsync(3)).Should().Be(0x23 & 0b111000);
    }
}