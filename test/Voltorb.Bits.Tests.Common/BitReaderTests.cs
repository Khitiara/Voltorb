using FluentAssertions;

namespace Voltorb.Bits.Tests.Common;

public class BitReaderTests
{
    private readonly MemoryStream _stream;
    private readonly BitReader    _bitReader;

    public BitReaderTests() {
        _stream = new MemoryStream(new byte[] { 0b11111010, 0x23, 0x34, 0x51, 0x25, 0x8f, 0x40, 0x01, 0xf7, });
        _bitReader = new BitReader(_stream);
    }

    [Fact]
    public async Task TestReadSimple() {
        (await _bitReader.ReadBitsAsync(5)).Should().Be(0b11010);
    }

    [Fact]
    public async Task TestPeek() {
        MemoryStream stream = new(new byte[] { 0b11111010, 0x23, 0x34, });
        BitReader bitReader = new(stream);
        (int count, ulong bits) = await bitReader.PeekBitsAsync(5);
        count.Should().Be(5);
        bits.Should().Be(0b11010);
        (count, ulong bits2) = await bitReader.PeekBitsAsync(8);
        count.Should().Be(8);
        bits2.Should().Be(0b11111010);
        (bits2 & 0b11111).Should().Be(bits);
    }

    [Fact]
    public async Task TestPeekAdvance() {
        (await _bitReader.AdvanceAsync(11)).Should().BeTrue();
        (await _bitReader.ReadBitsAsync(3)).Should().Be((0x23 & 0b111000) >> 3);
    }

    [Fact]
    public async Task TestBitReaderThrowing() {
        await _bitReader.Awaiting(b => b.PeekBitsAsync(65)).Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        await _bitReader.Awaiting(b => b.ReadBitsAsync(65)).Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        await _bitReader.Awaiting(b => b.PeekBitsAsync(-1)).Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("count");
        await _bitReader.Awaiting(b => b.ReadBitsAsync(-1)).Should()
            .ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("count");
    }

    [Fact]
    public async Task TestBitReaderZero() {
        (await _bitReader.PeekBitsAsync(0)).Should().Be((0, 0UL));
        (await _bitReader.ReadBitsAsync(0)).Should().Be(0UL);
        (await _bitReader.AdvanceAsync(-1)).Should().BeTrue();
    }

    [Fact]
    public async Task TestBitReaderBig() {
        (await _bitReader.AdvanceAsync(5)).Should().BeTrue();
        (await _bitReader.PeekBitsAsync(63)).Should().Be((63, 0x380A04792A89A11FUL));
        (await _bitReader.AdvanceAsync(1)).Should().BeTrue();
        (await _bitReader.AdvanceAsync(64)).Should().BeTrue();
        
        _bitReader.Seek(-69, SeekOrigin.Current);
        _bitReader.Seek(5, SeekOrigin.Current);
        _bitReader.Seek(-5, SeekOrigin.Current);
        (await _bitReader.PeekBitsAsync(4)).Should().Be((4, 0xFUL));
        _bitReader.Seek(-7, SeekOrigin.Current);
        (await _bitReader.ReadBitAsync()).Should().BeTrue();
    }
}