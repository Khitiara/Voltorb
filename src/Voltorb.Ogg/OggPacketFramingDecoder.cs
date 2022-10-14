using System.Buffers;
using Nerdbank.Streams;

namespace Voltorb.Ogg;

/// <summary>
/// De-frames packets from a sequence of Ogg pages from the same logical bitstream. If the codec for a logical bitstream
/// lacks semantic packet boundaries, this type may be inefficient due to allocations of individual byte sequences for
/// packets requiring extra buffers and copies.
/// </summary>
public sealed class OggPacketFramingDecoder
{
    private readonly MemoryPool<byte> _memoryPool;
    private          Sequence<byte>   _currentPacketBuffer;

    public OggPacketFramingDecoder(MemoryPool<byte> memoryPool) {
        _memoryPool = memoryPool;
        _currentPacketBuffer = new Sequence<byte>(_memoryPool);
    }

    /// <summary>
    /// Submits an ogg page to be split into packets, with individual packet byte sequences yielded into the returned
    /// enumerable. The page's byte data will be released from memory when enumeration of the returned sequence
    /// concludes, and failure to do that enumeration constitutes a likely memory leak. Individual returned segments
    /// should be disposed by the consumer.
    ///
    /// A partial packet from a previous page will be continued and yielded if the submitted page has the necessary
    /// flag set in its header, and the final packet on a page will not be returned if incomplete. Data from a previous
    /// partial packet will be released if the submitted page does not have the relevant flag.
    /// </summary>
    /// <remarks>
    /// Demultiplexing of ogg logical bitstreams and validation of page sequence numbers are not performed. It is
    /// expected that separate instances of <see cref="OggPacketFramingDecoder"/> will be maintained for each logical
    /// bitstream, and loss of pages will be detected. As pages not marked as a continuation result in discarding
    /// previous partial packet data when submitted, a corrupt packet will only occur when two out of sequence pages
    /// are submitted, of which the first has an incomplete final packet and the second is a packet continuation,
    /// which is expected to be rare, and detectable and recoverable by calling code.
    /// </remarks>
    public IEnumerable<Sequence<byte>> SubmitPage(OggPageReader.PageData page) {
        // we consume and discard the full packet bitstream
        using (page) {
            // on the first iteration we wont reset if the page has the continuation flag
            bool shouldReset = !page.PacketType.HasFlagFast(HeaderType.ContinuesPacket);
            foreach (int length in page.PacketLengths) {
                if (!shouldReset) {
                    // if we didnt reset now, we will before future packets this page
                    shouldReset = true;
                } else if (_currentPacketBuffer.Length > 0) {
                    // not a continuation, reset. if we have anything stored, then we have a previous data packet to
                    // yield calling code is responsible for disposing of the yielded data
                    yield return _currentPacketBuffer;
                    _currentPacketBuffer = new Sequence<byte>(_memoryPool);
                }

                // slice out the packet's data from the pagebits. we advance pagebits, so start always from 0
                ReadOnlySequence<byte> readOnlySequence = page.PageBits.AsReadOnlySequence.Slice(0, length);
                // dont bother allocating a buffer for the packet, just write out the segments of the binary sequence
                foreach (ReadOnlyMemory<byte> seg in readOnlySequence) {
                    _currentPacketBuffer.Write(seg.Span);
                }

                // advance past this packet so we can read the next
                page.PageBits.AdvanceTo(readOnlySequence.End);
            }

            // if the final packet is complete (so no continuation on future page) then we can yield it and reset the
            // stored buffer for next time. if the final packet will have a continuation, then we save the packet so far
            // to append to next page.
            if (page.FinalPacketIsComplete) {
                yield return _currentPacketBuffer;
                _currentPacketBuffer = new Sequence<byte>(_memoryPool);
            }
        }
    }
}