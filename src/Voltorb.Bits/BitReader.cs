namespace Voltorb.Bits;

/// <summary>
/// A stream wrapper to support reading individual bits from a <see cref="Stream"/>.
/// Note this class maintains some internal buffering of necessity, and thus should not be used concurrently with other
/// reading methods - if bit reading is necessary for only a sub-sequence of read data then care should be taken to
/// ensure that the state of this reader has been advanced to a whole-byte boundary
/// </summary>
public sealed class BitReader
{
    private          ulong  _bitBucket;
    private          int    _bitsAvailable;
    private          byte   _overflowBits;
    private readonly Stream _stream;
    private readonly byte[] _singleByteReadBuffer = new byte[1];

    public BitReader(Stream stream) {
        _stream = stream;
    }

    /// <summary>
    /// simple wrapper to read one byte from the stream asynchronously, equivalent to Stream.ReadByte
    /// but asynchronous returns a short to support returning -1 in the end-of-stream case
    /// </summary>
    private async ValueTask<short> ReadNextByteAsync() =>
        await _stream.ReadAsync(_singleByteReadBuffer) < 1 ? (short)-1 : _singleByteReadBuffer[0];

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
        // since we already peeked the bits and count <= 64, count <= _bitsAvailable and
        // advance must complete synchronously. ValueTask facilitates avoiding unnecessary
        // allocation here so extracting the synchronous part of AdvanceAsync is unnecessary
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

            if (_bitsAvailable > 0) // shift the new bits into the current bucket
                _bitBucket |= (ulong)(b & 0xFF) << _bitsAvailable;
            else // we have a pending skip from Seek, shift out the skipped bits
                _bitBucket |= (ulong)(b & 0xFF) >> -_bitsAvailable;
            // note the newly read bits
            _bitsAvailable += 8;

            // As count is at most 64, when this line executes _bitsAvailable ranges from 65 to 72 and thus
            // 72 - _bitsAvailable ranges from 0 to 7, and the right shift leaves at least one bit in the overflow
            if (_bitsAvailable > 64) {
                _overflowBits = (byte)(b >> (72 - _bitsAvailable));
                break;
            }
        }

        // temporary value so non-requested bits may be discarded
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
        // cant go backwards and going nowhere is a nop
        if (count <= 0) return true;

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
                _bitBucket |= shift > 0 
                    ? (ulong)_overflowBits << shift 
                    : (ulong)_overflowBits >> -shift;

                if (_bitsAvailable - 64 > count) {
                    // if theres still overflow bits somehow after stuff gets shifted down then we shift stored overflow
                    _overflowBits >>= count;
                }
            }

            // mark bits as discarded - we no longer need the original _bitsAvailable value as the bit buffer has
            // already had the discarded bits removed 
            _bitsAvailable -= count;
            return true;
        }

        // count >= _bitsAvailable, discard the buffered information completely for efficiency
        count -= _bitsAvailable;
        _bitsAvailable = 0;
        _bitBucket = 0;

        // if count was exactly _bitsAvailable, then we're done
        if (count == 0) return true;

        // count > _bitsAvailable, start reading to discard full bytes
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

    /// <summary>
    /// Seek in the stream a certain signed number of bits relative to the specified seek origin.
    /// This method will never perform a read operation, instead manipulating internal state to facilitate seeking
    /// a non-whole number of bytes
    /// </summary>
    public void Seek(long relBits, SeekOrigin origin) {
        if (origin == SeekOrigin.Current)
            if (relBits < 0) // backwards seek, take out available bits first
                relBits += _bitsAvailable;
            else // forwards seek, short-circuit skip the available bits
                relBits -= _bitsAvailable;
        
        if(relBits == 0 && origin == SeekOrigin.Current)
            return;

        // split given bits into bytes and bits. this can probably be made more efficient with bitwise ops
        // but im not awake enough to figure out the logic so meh
        (long bytes, long rem) = Math.DivRem(relBits, 8);

        // if going backwards a non-whole number of bytes, then go back further and adjust the remainder to be positive
        // e.g. -11 -> -1, -3 -> -2, +5
        // since we're aligned at a byte boundary at this point, this cannot result in seeking past the beginning of
        // the stream in cases where such would not already logically happen, so this is valid and allows skipping the
        // extra bits needed after we finish
        if (relBits < 0 && rem != 0) {
            bytes--;
            rem += 8;
        }

        // if we have to skip some bits forward, then setting _bitsAvailable to the negative of that number will force
        // PeekBitsAsync and AdvanceAsync will read extra bits from the stream when used and avoid performing a read
        _bitsAvailable = (int)-rem;
        
        // Actually seek some number of bits
        _stream.Seek(bytes, origin);
    }
}