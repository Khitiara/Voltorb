namespace Voltorb.Bits;

/// <summary>
/// A stream wrapper to support reading individual bits from a <see cref="Stream"/>.
/// Note this class maintains some internal buffering of necessity, and thus should not be used concurrently with other
/// reading methods - if bit reading is necessary for a sub-sequence of read data a <see cref="MemoryStream"/> should be
/// used to back this, or else manual bitwise operation
/// </summary>
public sealed class BitReader
{
    private          ulong  _bitBucket;
    private          int    _bitsAvailable;
    private          byte   _overflowBits;
    private readonly Stream _stream;

    public BitReader(Stream stream) {
        _stream = stream;
    }

    private async ValueTask<short> ReadNextByteAsync() {
        byte[] buf = new byte[1];
        return await _stream.ReadAsync(buf) < 1 ? (short)-1 : buf[0];
    }

    /// <summary>
    /// Reads a single bit from the stream and returns it, interpreted as a bool
    /// </summary>
    public async ValueTask<bool> ReadBitAsync() => await ReadBitsAsync(1) > 0;

    /// <summary>
    /// Reads the specified number of bits from the stream and advances the read position.
    /// </summary>
    /// <param name="count">The number of bits to read.</param>
    /// <returns>The value read. If not enough bits could be read, this will be a truncated value.</returns>
    public async ValueTask<ulong> ReadBitsAsync(int count) {
        switch (count) {
            case < 0 or > 64:
                throw new ArgumentOutOfRangeException(nameof(count), count, "");
            case 0:
                return 0UL;
        }
        (count, ulong bits) = await PeekBitsAsync(count);
        await AdvanceAsync(count);
        return bits;
    }

    /// <summary>
    /// Peek up to the specified number of bits ahead
    /// </summary>
    /// <param name="count">The number of bits to peek</param>
    /// <returns>The number and value of bits successfully peeked</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="count"/> is less than 0 or more than 64</exception>
    public async ValueTask<(int Count, ulong Bits)> PeekBitsAsync(int count) {
        switch (count) {
            case < 0 or > 64:
                throw new ArgumentOutOfRangeException(nameof(count), count, "");
            case 0:
                return (0, 0UL);
        }

        // Read more bits in, 8 at a time, until either end of stream or we have enough. 
        while (_bitsAvailable < count) {
            short b;
            if ((b = await ReadNextByteAsync()) < 0) {
                return (_bitsAvailable, _bitBucket);
            }

            _bitBucket |= (ulong)(b & 0xFF) << _bitsAvailable;
            _bitsAvailable += 8;
            
            // As count is at most 64, when this line executes _bitsAvailable is at most 72 and such the overflow bits
            // (if any exist) fit in a single byte
            if (_bitsAvailable > 64) {
                _overflowBits = (byte)(b >> (72 - _bitsAvailable));
                break;
            }
        }

        ulong value = _bitBucket;
        if (count < 64) {
            // Mask off un-requested bits
            value &= (1UL << count) - 1;
        }

        return (count, value);
    }

    /// <summary>
    /// Advance the read pointer <paramref name="count"/> bits, draining data asynchronously from the source stream if
    /// necessary
    /// </summary>
    /// <param name="count">The number of bits to advance past</param>
    /// <returns><c>true</c> if the requested number of bits were drained, <c>false</c> otherwise</returns>
    public async ValueTask<bool> AdvanceAsync(int count) {
        // cant go backwards
        if(count < 0) return true;
        
        // we have enough bits already buffered for a discard, no I/O will be performed and there will be leftover bits
        if (count < _bitsAvailable) {
            // if count >= 64 then the entire bit bucket may be discarded by storing 0
            if (count > 63)
                _bitBucket = 0;
            else // otherwise discard bits using right shift
                _bitBucket >>= count;

            // if we had overflow bits then we need to shift them down into the bucket
            if (_bitsAvailable > 64) {
                // Bit hacky with the ?: to define behavior of left shift by negative value
                // operation is logically equivalent to _bitBucket |= _overflowBits << (64 - count)
                // if a << -b is defined as a >> b
                // When count <= 64, overflow values are left shifted to avoid modifying the remaining main bits
                // otherwise, overflow values are right shifted to discard the remainder of count - 64
                int shift = 64 - count;
                _bitBucket |= shift > 0 ? (ulong)_overflowBits << shift : (ulong)_overflowBits >> -shift;

                if (_bitsAvailable - 64 > count)
                {
                    // if theres still overflow bits somehow after stuff gets shifted down then we shift stored overflow
                    _overflowBits >>= count;
                }
            }
            // mark bits as discarded
            _bitsAvailable -= count;
            return true;
        } 
        // count >= _bitsAvailable
        count -= _bitsAvailable;
        _bitsAvailable = 0;
        _bitBucket = 0;
        if(count <= 0) return true;

        // count > _bitsAvailable, start draining to discard full bytes
        while (count > 8) {
            if (await ReadNextByteAsync() == -1)
                return false;

            count -= 8;
        }

        // Partial byte needed, retrieve and partial discard
        if (count > 0) {
            short tmp = await ReadNextByteAsync();
            if (tmp == -1) return false;
            _bitBucket = (ulong)(tmp >> count);
            _bitsAvailable = 8 - count;
        }

        return true;
    }
}