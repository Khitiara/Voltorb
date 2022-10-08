using System.Buffers;

namespace Voltorb.Bits;

/// <summary>
/// A wrapper to support reading individual bits from a <see cref="SequenceReader{Byte}"/>.
/// Note this class maintains some internal buffering of necessity, and thus should not be used concurrently with other
/// reading methods - if bit reading is necessary for only a sub-sequence of read data then care should be taken to
/// ensure that the state of this reader has been advanced to a whole-byte boundary
/// </summary>
public ref struct BitReader
{
    private ulong _bitBucket;

    /// <summary>
    /// The number of bits buffered within this bitreader and available for reading.
    /// Can be interpreted as an offset backwards from the read head of the sequencereader
    /// e.g. when the sequencereader.consumed = 9 and _bitsavailable = 2, we can read 2 bits before
    /// advancing the sequencereader and thus the logical read position is 9*8-2 bits
    ///
    /// this field can be negative after a seek operation - such would mean that the logical read head
    /// is located after the physical read head, and thus a read will be performed and partially discarded
    /// before returning new bits
    /// </summary>
    private int _bitsAvailable;

    private byte                 _overflowBits;
    private SequenceReader<byte> _stream;

    public BitReader(ReadOnlyMemory<byte> memory) : this(new ReadOnlySequence<byte>(memory)) { }
    public BitReader(ReadOnlySequence<byte> sequence) : this(new SequenceReader<byte>(sequence)) { }

    public BitReader(SequenceReader<byte> stream) {
        _stream = stream;
    }

    /// <summary>
    /// The position of this reader from the start of its source sequence, in bits
    /// </summary>
    public long Position => 8 * _stream.Consumed - _bitsAvailable;

    /// <summary>
    /// Peek up to the specified number of bits ahead
    /// </summary>
    /// <param name="count">The number of bits to peek</param>
    /// <param name="bits">The value of bits successfully peeked</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="count"/> is less than 0 or more than 64</exception>
    public void PeekBits(scoped ref int count, out ulong bits) {
        // my IDE reallllly does not like the scoped keyword
        switch (count) {
            case < 0 or > 64:
                throw new ArgumentOutOfRangeException(nameof(count), count, "");
            case 0:
                bits = 0ul;
                count = 0;
                return;
        }

        // Read more bits in, 8 at a time, until either end of stream or we have enough. 
        while (_bitsAvailable < count) {
            if (!_stream.TryRead(out byte b)) {
                count = _bitsAvailable;
                bits = _bitBucket;
                return;
            }

            // or the new bits into the bit bucket, shifted left so as to not overwrite available bits.
            // if bitsavailable is negative, then shifting will be right to discard unread bits before
            // the logical read head
            _bitBucket |= MathExtensions.OmniLeftShift((ulong)(b & 0xFF), _bitsAvailable);
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

        bits = value;
    }

    /// <summary>
    /// Advance the read pointer <paramref name="count"/> bits, draining data asynchronously from the source stream if
    /// necessary
    /// </summary>
    /// <param name="count">The number of bits to advance past</param>
    /// <returns><c>true</c> if the requested number of bits were drained, <c>false</c> otherwise</returns>
    public bool Advance(int count) {
        // cant go backwards and going nowhere is a nop
        if (count <= 0) return true;

        // we have enough bits already buffered for a discard, dont involve the sequence and there will be leftover bits
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
                _bitBucket |= MathExtensions.OmniLeftShift(_overflowBits, 64 - count);

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

        // count > _bitsAvailable, advance the reader to discard. use SequenceReader.Remaining to return false early
        // and advance using sequencereader itself instead of discarding individual bytes
        if (count > 8) {
            int bytes = count / 8;
            if (bytes > _stream.Remaining)
                return false;
            _stream.Advance(bytes);
            count %= 8;
        }

        // Partial byte needed, retrieve and partial discard
        if (count > 0) {
            if (!_stream.TryRead(out byte tmp))
                return false;
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
        // we cant seek forward from the end obv
        if (origin == SeekOrigin.End && relBits > 0)
            throw new ArgumentOutOfRangeException(nameof(relBits), relBits, "");
        
        if (origin == SeekOrigin.Current) {
            // move the logical read head to match the physical one and adjust the seek amount to compensate
            relBits -= _bitsAvailable;
            _bitsAvailable = 0;
        }

        // if theres nothin to do then theres nothin to do and return early
        if (relBits == 0 && origin == SeekOrigin.Current)
            return;

        // split given bits into bytes and bits. this can probably be made more efficient with bitwise ops
        // but im not awake enough to figure out the logic so meh
        (long bytes, long rem) = Math.DivRem(relBits, 8);

        // if going backwards a non-whole number of bytes, then go back further and adjust the remainder to be positive
        // e.g. -11 -> (-1, -3) -> (-2, +5)
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

        // finished adjusting the partial byte information. that being done, all thats left is to move the whole-byte
        // read index 
        if (bytes == 0) return;

        // Actually seek some number of bits
        switch (origin) {
            case SeekOrigin.Begin:
                // sequencereader has no reset so we calculate an offset and seek from the current position
                if (bytes < _stream.Consumed) {
                    _stream.Rewind(_stream.Consumed - bytes);
                } else {
                    _stream.Advance(bytes - _stream.Consumed);
                }

                break;
            case SeekOrigin.Current:
                if (bytes < 0) {
                    _stream.Rewind(-bytes);
                } else {
                    _stream.Advance(bytes);
                }

                break;
            case SeekOrigin.End:
                // use the sequencereader to advance to the end and work backwards.
                // seek forwards from end is invalid and thrown earlier
                _stream.AdvanceToEnd();
                _stream.Rewind(-bytes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }
    }
}