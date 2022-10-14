using FluentAssertions;

namespace Voltorb.Bits.Tests.Common;

public class BitReaderTests
{
    private readonly byte[] _stream = { 0xFA, 0x23, 0x34, 0x51, 0x25, 0x8f, 0x40, 0x01, 0xf7, };

    [Fact]
    public void TestReadSimple() {
        BitReader bitReader = new(_stream);
        bitReader.ReadBits(5).Should().Be(0b11010);
        bitReader.Position.Should().Be(5);
    }

    [Fact]
    public void TestPeek() {
        BitReader bitReader = new(_stream);
        int count = 5;
        count = bitReader.PeekBits(count, out ulong bits);
        count.Should().Be(5);
        bits.Should().Be(0b11010);
        count = 8;
        count = bitReader.PeekBits(count, out ulong bits2);
        count.Should().Be(8);
        bits2.Should().Be(0b11111010);
        (bits2 & 0b11111).Should().Be(bits);
    }

    [Fact]
    public void TestPeekAdvance() {
        BitReader bitReader = new(_stream);
        bitReader.TryAdvance(11).Should().BeTrue();
        bitReader.ReadBits(3).Should().Be((0x23 & 0b111000) >> 3);
    }

    [Fact]
    public void TestBitReaderThrowing() {
        new Action(() => PeekHelper(new BitReader(_stream),65)).Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        new Action(() => {
                BitReader b = new(_stream);
                b.ReadBits(65);
            }).Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        new Action(() => PeekHelper(new BitReader(_stream),-1)).Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        new Action(() => {
                BitReader b = new(_stream);
                b.ReadBits(-1);
            }).Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("count");
    }

    private static (int, ulong) PeekHelper(BitReader reader, int count) {
        count = reader.PeekBits(count, out var b);
        return (count, b);
    }

    [Fact]
    public void TestBitReaderZero() {
        BitReader bitReader = new(_stream);
        PeekHelper(bitReader, 0).Should().Be((0, 0UL));
        bitReader.ReadBits(0).Should().Be(0UL);
        bitReader.TryAdvance(-1).Should().BeTrue();
    }

    [Fact]
    public void TestBitReaderBig() {
        BitReader bitReader = new(_stream);
        bitReader.TryAdvance(5).Should().BeTrue();
        PeekHelper(bitReader,63).Should().Be((63, 0x380A04792A89A11FUL));
        bitReader.Position.Should().Be(5);
        bitReader.TryAdvance(1).Should().BeTrue();
        bitReader.TryAdvance(64).Should().BeTrue();
        bitReader.Position.Should().Be(70);

        bitReader.Seek(-69, SeekOrigin.Current);
        bitReader.Position.Should().Be(1);
        bitReader.Seek(5, SeekOrigin.Current);
        bitReader.Seek(-5, SeekOrigin.Current);
        PeekHelper(bitReader,4).Should().Be((4, 0xDUL));
        bitReader.Seek(1, SeekOrigin.Current);
        bitReader.ReadBit().Should().BeFalse();
    }
}